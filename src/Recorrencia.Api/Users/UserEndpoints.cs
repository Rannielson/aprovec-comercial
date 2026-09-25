using System.Net.Mail;
using Microsoft.Extensions.Options;
using Npgsql;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Email;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.Users;

public static class UserEndpoints
{
    public sealed record InviteUserRequest(string? Name, string? Email, Guid? SupervisorId, Guid[]? RoleIds,
        string? CodigoVoluntario, string? NomeHinova, string? CpfHinova, string? Password);
    public sealed record SupervisorRequest(Guid? SupervisorId);
    public sealed record UserRolesRequest(Guid[]? RoleIds);
    public sealed record SetUserPasswordRequest(string? Password);
    public sealed record UpdateUserRequest(string? Name, string? Email);

    public sealed class UserRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Status { get; set; } = "";
        public Guid? SupervisorId { get; set; }
        public Guid[] RoleIds { get; set; } = [];
    }

    public sealed class SupervisorRow
    {
        public Guid? SupervisorId { get; set; }
    }

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/users", ListAsync).RequirePermission("estrutura.visualizar");
        app.MapPost("/users", InviteAsync).RequirePermission("usuarios.convidar");
        app.MapPut("/users/{id:guid}", UpdateAsync).RequirePermission("usuarios.convidar");
        app.MapPut("/users/{id:guid}/supervisor", ChangeSupervisorAsync).RequirePermission("estrutura.editar");
        app.MapPost("/users/{id:guid}/deactivate", DeactivateAsync).RequirePermission("usuarios.desligar");
        app.MapPut("/users/{id:guid}/password", SetPasswordAsync).RequirePermission("usuarios.convidar");
        app.MapPut("/users/{id:guid}/roles", SetRolesAsync).RequirePermission("usuarios.gerenciar_perfis");
    }

    private static async Task<IResult> ListAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var users = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), tx => tx.QueryAsync<UserRow>(
            """
            select u.id, u.name, u.email, u.status, u.supervisor_id,
                   coalesce(array_agg(ur.role_id) filter (where ur.role_id is not null), '{}')::uuid[] as role_ids
              from users u
              left join user_roles ur on ur.user_id = u.id
             group by u.id, u.name, u.email, u.status, u.supervisor_id
             order by u.name
            """), ct);
        return Results.Ok(users);
    }

    private static async Task<IResult> InviteAsync(InviteUserRequest body, RequestContext request, Database db,
        CurrentPermissions permissions, IEmailSender email, LinkBuilder links, IOptions<AuthOptions> options, PasswordHasher hasher, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var name = (body.Name ?? "").Trim();
        var address = (body.Email ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.name_required");
        if (!MailAddress.TryCreate(address, out var parsedAddress) || parsedAddress.Address != address)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.invalid_email");

        var codigoVoluntario = (body.CodigoVoluntario ?? "").Trim();
        var nomeHinova = (body.NomeHinova ?? "").Trim();
        var cpfHinova = (body.CpfHinova ?? "").Trim();
        var hasHinovaFields = body.CodigoVoluntario is not null || body.NomeHinova is not null || body.CpfHinova is not null;

        var roleIds = body.RoleIds ?? [];
        var mine = await permissions.GetAsync(ct);
        if (roleIds.Length > 0 && !mine.Has("usuarios.gerenciar_perfis"))
            throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");
        // Hierarchy position drives upline commission, so placing a new hire under a given
        // supervisor is as sensitive as granting a role -- gated the same way, behind
        // estrutura.editar, rather than left open to anyone who can merely invite.
        if (body.SupervisorId is not null && !mine.Has("estrutura.editar"))
            throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");
        // Creating a Hinova voluntário mapping is exactly as sensitive as the Fase 3
        // credentials/mapping screen that owns hinova_voluntario_mapping -- same permission.
        if (hasHinovaFields && !mine.Has("integracoes.gerenciar"))
            throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");
        // All three or none -- a partial set would otherwise either crash on a NOT NULL
        // violation or (worse) silently insert an empty placeholder value.
        if (hasHinovaFields && (body.CodigoVoluntario is null || body.NomeHinova is null || body.CpfHinova is null))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.campos_incompletos");

        string? passwordHash = null;
        if (body.Password is not null)
        {
            PasswordPolicy.Validate(body.Password);
            passwordHash = hasher.Hash(body.Password);
        }

        var id = Guid.CreateVersion7();
        var token = "";
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            await RoleGuards.EnsureCanGrantRolesAsync(tx, mine, roleIds);
            await CreateUserWithRolesAsync(tx, tenant, id, name, address, body.SupervisorId, roleIds);
            if (hasHinovaFields)
                await LinkHinovaAsync(tx, tenant, id, actor, codigoVoluntario, nomeHinova, cpfHinova);
            if (passwordHash is not null)
            {
                await tx.ExecuteAsync("select app.set_password_admin(@id, @passwordHash)", new { id, passwordHash });
                await WriteAsync(tx, tenant, actor, "users.cadastrar_com_senha", "users", id, null,
                    new { name, email = address, supervisorId = body.SupervisorId, roleIds });
            }
            else
            {
                token = await CreateInviteTokenAsync(tx, tenant, actor, id, name, address, body.SupervisorId, roleIds, options.Value.InviteTtlSeconds);
            }
            return 0;
        }, ct);

        if (passwordHash is null)
        {
            await email.SendAsync(address, "Convite de acesso",
                EmailTemplates.AccessLink(
                    "Convite de acesso",
                    $"Você foi convidado para acessar a plataforma de {request.TenantName}.",
                    "Definir minha senha",
                    links.SetPassword(request.TenantSlug!, token),
                    "O link vale por 72 horas.",
                    links.Logo(request.TenantSlug!)), ct);
        }
        return Results.Created($"/users/{id}", new { id });
    }

    /// <summary>
    /// Insere o usuário e seus papéis. Reaproveitado por InviteAsync (POST /users) e pela
    /// aprovação de solicitação de cadastro (Fase 5) -- a mesma operação, dois pontos de entrada.
    /// </summary>
    internal static async Task CreateUserWithRolesAsync(Tx tx, Guid tenant, Guid id, string name, string email,
        Guid? supervisorId, IReadOnlyCollection<Guid> roleIds)
    {
        try
        {
            await tx.ExecuteAsync(
                "insert into users (id, tenant_id, name, email, supervisor_id) values (@id, @tenant, @name, @email, @supervisor)",
                new { id, tenant, name, email, supervisor = supervisorId });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ApiProblem(StatusCodes.Status409Conflict, "users.email_taken");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.invalid_supervisor");
        }

        foreach (var roleId in roleIds.Distinct())
            await tx.ExecuteAsync("insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @id, @roleId)", new { tenant, id, roleId });
    }

    internal static async Task LinkHinovaAsync(Tx tx, Guid tenant, Guid userId, Guid actor, string codigoVoluntario, string nomeHinova, string cpfHinova)
    {
        try
        {
            await tx.ExecuteAsync(
                """
                insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
                values (@tenant, @userId, @codigoVoluntario, @nomeHinova, @cpfHinova, @actor)
                """,
                new { tenant, userId, codigoVoluntario, nomeHinova, cpfHinova, actor });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ApiProblem(StatusCodes.Status409Conflict, "hinova.vinculo_duplicado");
        }
        await WriteAsync(tx, tenant, actor, "hinova.vincular", "hinova_voluntario_mapping", userId, null, new { codigoVoluntario, nomeHinova, cpfHinova });
    }

    internal static async Task<string> CreateInviteTokenAsync(Tx tx, Guid tenant, Guid actor, Guid userId, string name, string email,
        Guid? supervisorId, Guid[] roleIds, int ttlSeconds)
    {
        var token = Tokens.New();
        await tx.ExecuteAsync("select app.create_invite(@userId, @hash, 'convite', @ttl)",
            new { userId, hash = Tokens.Hash(token), ttl = ttlSeconds });
        await WriteAsync(tx, tenant, actor, "users.invite", "users", userId, null, new { name, email, supervisorId, roleIds });
        return token;
    }

    private static async Task<IResult> ChangeSupervisorAsync(Guid id, SupervisorRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var current = await tx.QuerySingleOrDefaultAsync<SupervisorRow>(
                "select supervisor_id from users where id = @id for update", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
            try
            {
                await tx.ExecuteAsync("update users set supervisor_id = @supervisor where id = @id", new { supervisor = body.SupervisorId, id });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new ApiProblem(StatusCodes.Status400BadRequest, "users.invalid_supervisor");
            }
            await WriteAsync(tx, tenant, actor, "users.change_supervisor", "users", id,
                new { current.SupervisorId }, new { body.SupervisorId });
            return 0;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeactivateAsync(Guid id, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var status = await tx.QuerySingleOrDefaultAsync<string>("select status from users where id = @id for update", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
            if (status == "desligado")
                return 0;
            await tx.ExecuteAsync("update users set status = 'desligado' where id = @id", new { id });
            await RoleGuards.EnsureProfileManagerRemainsAsync(tx);
            await tx.ExecuteAsync("select app.revoke_user_sessions(@id)", new { id });
            await WriteAsync(tx, tenant, actor, "users.deactivate", "users", id, new { status }, new { status = "desligado" });
            return 0;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateUserRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var name = (body.Name ?? "").Trim();
        var address = (body.Email ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.name_required");
        if (!MailAddress.TryCreate(address, out var parsedAddress) || parsedAddress.Address != address)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.invalid_email");

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var current = await tx.QuerySingleOrDefaultAsync<UserRow>("select id, name, email from users where id = @id for update", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
            try
            {
                await tx.ExecuteAsync("update users set name = @name, email = @address where id = @id", new { name, address, id });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "users.email_taken");
            }
            await WriteAsync(tx, tenant, actor, "users.update", "users", id,
                new { current.Name, current.Email }, new { name, address });
            return 0;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetPasswordAsync(Guid id, SetUserPasswordRequest body, RequestContext request,
        Database db, PasswordHasher hasher, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        PasswordPolicy.Validate(body.Password);
        var hash = hasher.Hash(body.Password!);
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var status = await tx.QuerySingleOrDefaultAsync<string>("select status from users where id = @id for update", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
            if (status == "desligado")
                throw new ApiProblem(StatusCodes.Status409Conflict, "users.desligado");
            await tx.ExecuteAsync("select app.set_password_admin(@id, @hash)", new { id, hash });
            await WriteAsync(tx, tenant, actor, "users.set_password_admin", "users", id, null, null);
            return 0;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetRolesAsync(Guid id, UserRolesRequest body, RequestContext request, Database db,
        CurrentPermissions permissions, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var mine = await permissions.GetAsync(ct);
        var desired = (body.RoleIds ?? []).Distinct().ToArray();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            _ = await tx.QuerySingleOrDefaultAsync<Guid?>("select id from users where id = @id", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
            var current = (await tx.QueryAsync<Guid>("select role_id from user_roles where user_id = @id", new { id })).ToArray();
            var added = desired.Except(current).ToArray();
            await RoleGuards.EnsureCanGrantRolesAsync(tx, mine, added);

            // Removing a role the actor doesn't personally have full standing over must be
            // blocked the same way granting it would be -- otherwise a role-manager could
            // strip a user of permissions far beyond their own reach.
            var removedRoleIds = current.Except(desired).ToArray();
            await RoleGuards.EnsureCanGrantRolePermissionsAsync(tx, mine, removedRoleIds);

            foreach (var roleId in added)
                await tx.ExecuteAsync("insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @id, @roleId)", new { tenant, id, roleId });
            await tx.ExecuteAsync("delete from user_roles where user_id = @id and not (role_id = any(@desired))", new { id, desired });

            await RoleGuards.EnsureProfileManagerRemainsAsync(tx);
            await WriteAsync(tx, tenant, actor, "users.set_roles", "users", id, new { roleIds = current }, new { roleIds = desired });
            return 0;
        }, ct);
        return Results.NoContent();
    }
}
