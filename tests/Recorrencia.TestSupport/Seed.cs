using Dapper;

namespace Recorrencia.TestSupport;

public sealed class Seed(PostgresFixture db)
{
    public static string UniqueSlug() => "t" + Guid.NewGuid().ToString("N")[..20];

    public async Task ExecAsync(string sql, object? param = null)
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        await conn.ExecuteAsync(sql, param);
    }

    public async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        return (await conn.ExecuteScalarAsync<T>(sql, param))!;
    }

    public Task<Guid> TenantAsync(string? slug = null) =>
        ScalarAsync<Guid>(
            "insert into tenants (slug, name) values (@slug, 'Empresa de teste') returning id",
            new { slug = slug ?? UniqueSlug() });

    public Task<Guid> UserAsync(Guid tenantId, string name, Guid? supervisorId = null, string status = "ativo", string? email = null) =>
        ScalarAsync<Guid>(
            """
            insert into users (tenant_id, name, email, password_hash, status, supervisor_id)
            values (@tenantId, @name, @email, @hash, @status, @supervisorId)
            returning id
            """,
            new
            {
                tenantId,
                name,
                email = email ?? $"{Guid.NewGuid():N}@teste.local",
                hash = status == "convidado" ? null : "hash-de-teste",
                status,
                supervisorId,
            });

    public Task EnableModulesAsync(Guid tenantId) =>
        ExecAsync(
            "insert into tenant_modules (tenant_id, module_key) select @tenantId, key from modules on conflict do nothing",
            new { tenantId });

    public async Task<Guid> RoleAsync(Guid tenantId, string name, params (string Key, string? Scope)[] permissions)
    {
        var roleId = await ScalarAsync<Guid>(
            "insert into roles (tenant_id, name) values (@tenantId, @name) returning id", new { tenantId, name });
        foreach (var (key, scope) in permissions)
        {
            await ExecAsync(
                "insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenantId, @roleId, @key, @scope)",
                new { tenantId, roleId, key, scope });
        }
        return roleId;
    }

    public Task AssignRoleAsync(Guid tenantId, Guid userId, Guid roleId) =>
        ExecAsync(
            "insert into user_roles (tenant_id, user_id, role_id) values (@tenantId, @userId, @roleId)",
            new { tenantId, userId, roleId });

    public Task<(Guid TenantId, Guid AdminId)> ProvisionAsync(string? slug = null, string? planTemplate = "aprovec", string effectiveFrom = "2026-08-01") =>
        db.AsSuperadminAsync(null, (c, t) => c.QuerySingleAsync<(Guid, Guid)>(
            """
            select tenant_id, admin_user_id
              from app.provision_tenant(@slug, 'Empresa provisionada', 'Admin', @adminEmail, @planTemplate, @effectiveFrom::date)
            """,
            new { slug = slug ?? UniqueSlug(), adminEmail = $"admin-{Guid.NewGuid():N}@teste.local", planTemplate, effectiveFrom },
            t));

    public Task AssignTemplateRoleAsync(Guid tenantId, Guid userId, string templateKey) =>
        ExecAsync(
            """
            insert into user_roles (tenant_id, user_id, role_id)
            select @tenantId, @userId, id from roles where tenant_id = @tenantId and source_template_key = @templateKey
            """,
            new { tenantId, userId, templateKey });

    public Task<Guid> BoletoAsync(Guid tenantId, Guid ownerId, decimal valor, string status, string? paidOn) =>
        ScalarAsync<Guid>(
            """
            insert into boletos (tenant_id, participante_id, associado_ref, associado_nome, placa, valor, status, vencimento, pago_em)
            values (@tenantId, @ownerId, @reference, 'Associado de teste', 'TST0A00', @valor, @status,
                    coalesce(@paidOn::date, date '2026-09-01'), @paidOn::date)
            returning id
            """,
            new { tenantId, ownerId, reference = "APV-" + Guid.NewGuid().ToString("N")[..8], valor, status, paidOn });
}
