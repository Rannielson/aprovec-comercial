using Npgsql;
using static Recorrencia.Api.Audit.Audit;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Integracoes;

public static class HinovaEndpoints
{
    public sealed record CredenciaisStatusResponse(bool Configurado, DateTimeOffset? AtualizadoEm, string? AtualizadoPor);
    public sealed record SalvarCredenciaisRequest(string Usuario, string Senha, string TokenSga);
    public sealed record VoluntarioResponse(string Codigo, string Nome, string Cpf, string? Telefone, IReadOnlyList<string> Cooperativas, bool JaVinculado, string? VinculadoA);
    public sealed record MapeamentoResponse(Guid UserId, string UserName, string CodigoVoluntario, string NomeHinova, string CpfHinova, DateTimeOffset MappedAt);
    public sealed record CriarMapeamentoRequest(Guid UserId, string CodigoVoluntario, string NomeHinova, string CpfHinova);

    private sealed class StatusRow
    {
        public DateTimeOffset UpdatedAt { get; set; }
        public string UpdatedByName { get; set; } = "";
    }

    private sealed class MappingRow
    {
        public string CodigoVoluntario { get; set; } = "";
        public string VinculadoA { get; set; } = "";
    }

    private sealed class MapeamentoRow
    {
        public Guid UserId { get; set; }
        public string UserName { get; set; } = "";
        public string CodigoVoluntario { get; set; } = "";
        public string NomeHinova { get; set; } = "";
        public string CpfHinova { get; set; } = "";
        public DateTimeOffset MappedAt { get; set; }
    }

    public static void MapHinovaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/integracoes/hinova/credenciais", ObterCredenciaisStatusAsync).RequirePermission("integracoes.gerenciar");
        app.MapPut("/integracoes/hinova/credenciais", SalvarCredenciaisAsync).RequirePermission("integracoes.gerenciar");
        app.MapGet("/integracoes/hinova/voluntarios", ListarVoluntariosAsync).RequirePermission("integracoes.gerenciar");
        app.MapGet("/integracoes/hinova/mapeamentos", ListarMapeamentosAsync).RequirePermission("integracoes.gerenciar");
        app.MapPost("/integracoes/hinova/mapeamentos", CriarMapeamentoAsync).RequirePermission("integracoes.gerenciar");
        app.MapDelete("/integracoes/hinova/mapeamentos/{userId:guid}", RemoverMapeamentoAsync).RequirePermission("integracoes.gerenciar");
    }

    private static async Task<IResult> ObterCredenciaisStatusAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        var row = await db.InTenantAsync(tenant, user, tx => tx.QuerySingleOrDefaultAsync<StatusRow>(
            """
            select hc.updated_at, u.name as updated_by_name
              from hinova_credenciais hc join users u on u.id = hc.updated_by
             where hc.tenant_id = @tenant
            """,
            new { tenant }), ct);

        return Results.Ok(row is null
            ? new CredenciaisStatusResponse(false, null, null)
            : new CredenciaisStatusResponse(true, row.UpdatedAt, row.UpdatedByName));
    }

    private static async Task<IResult> SalvarCredenciaisAsync(SalvarCredenciaisRequest body, RequestContext request, Database db,
        IHinovaClient hinova, AesGcmCipher cipher, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        if (string.IsNullOrWhiteSpace(body.Usuario) || string.IsNullOrWhiteSpace(body.Senha) || string.IsNullOrWhiteSpace(body.TokenSga))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "request.invalid");

        try
        {
            await hinova.AutenticarAsync(body.Usuario, body.Senha, body.TokenSga, ct);
        }
        catch (HinovaAuthException)
        {
            throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.credenciais_invalidas");
        }

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            await tx.ExecuteAsync(
                """
                insert into hinova_credenciais (tenant_id, usuario_enc, senha_enc, token_sga_enc, updated_by)
                values (@tenant, @usuario, @senha, @tokenSga, @actor)
                on conflict (tenant_id) do update set
                  usuario_enc = excluded.usuario_enc, senha_enc = excluded.senha_enc,
                  token_sga_enc = excluded.token_sga_enc, updated_at = now(), updated_by = excluded.updated_by
                """,
                new
                {
                    tenant, actor,
                    usuario = cipher.Encrypt(body.Usuario),
                    senha = cipher.Encrypt(body.Senha),
                    tokenSga = cipher.Encrypt(body.TokenSga),
                });
            await WriteAsync(tx, tenant, actor, "hinova.credenciais.salvar", "hinova_credenciais", null, null, new { body.Usuario });
            return 0;
        }, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ListarVoluntariosAsync(string? query, RequestContext request, Database db, IHinovaClient hinova,
        AesGcmCipher cipher, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();

        var tokenUsuario = await HinovaAuth.GetTokenUsuarioAsync(db, tenant, user, cipher, hinova, ct);
        var voluntarios = await hinova.ListarVoluntariosAsync(tokenUsuario, ct);

        var filtered = string.IsNullOrWhiteSpace(query)
            ? voluntarios
            : voluntarios.Where(v => v.Nome.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        var mapped = (await db.InTenantAsync(tenant, user, tx => tx.QueryAsync<MappingRow>(
            """
            select m.codigo_voluntario, u.name as vinculado_a
              from hinova_voluntario_mapping m join users u on u.id = m.user_id
             where m.tenant_id = @tenant
            """,
            new { tenant }), ct)).ToDictionary(m => m.CodigoVoluntario, m => m.VinculadoA);

        return Results.Ok(filtered.Select(v => new VoluntarioResponse(
            v.Codigo, v.Nome, v.Cpf, v.Telefone, v.Cooperativas, mapped.ContainsKey(v.Codigo), mapped.GetValueOrDefault(v.Codigo))).ToList());
    }

    private static async Task<IResult> ListarMapeamentosAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        var rows = await db.InTenantAsync(tenant, user, tx => tx.QueryAsync<MapeamentoRow>(
            """
            select m.user_id, u.name as user_name, m.codigo_voluntario, m.nome_hinova, m.cpf_hinova, m.mapped_at
              from hinova_voluntario_mapping m join users u on u.id = m.user_id
             where m.tenant_id = @tenant
             order by u.name
            """,
            new { tenant }), ct);

        return Results.Ok(rows.Select(r => new MapeamentoResponse(
            r.UserId, r.UserName, r.CodigoVoluntario, r.NomeHinova, r.CpfHinova, r.MappedAt)).ToList());
    }

    private static async Task<IResult> CriarMapeamentoAsync(CriarMapeamentoRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            try
            {
                await tx.ExecuteAsync(
                    """
                    insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
                    values (@tenant, @userId, @codigo, @nome, @cpf, @actor)
                    """,
                    new { tenant, actor, userId = body.UserId, codigo = body.CodigoVoluntario, nome = body.NomeHinova, cpf = body.CpfHinova });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "hinova.vinculo_duplicado");
            }
            await WriteAsync(tx, tenant, actor, "hinova.vincular", "hinova_voluntario_mapping", body.UserId, null, body);
            return 0;
        }, ct);

        return Results.Created($"/integracoes/hinova/mapeamentos/{body.UserId}", body);
    }

    private static async Task<IResult> RemoverMapeamentoAsync(Guid userId, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var deleted = await tx.ExecuteAsync(
                "delete from hinova_voluntario_mapping where tenant_id = @tenant and user_id = @userId", new { tenant, userId });
            if (deleted == 0)
                throw new ApiProblem(StatusCodes.Status404NotFound, "hinova.vinculo_not_found");
            await WriteAsync(tx, tenant, actor, "hinova.desvincular", "hinova_voluntario_mapping", userId, null, null);
            return 0;
        }, ct);

        return Results.NoContent();
    }
}
