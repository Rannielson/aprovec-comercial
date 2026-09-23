using Dapper;
using Npgsql;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Cli;

public sealed record SeededTenant(Guid TenantId, string Slug, Guid Admin, Guid Joao, Guid Maria, Guid Pedro, Guid Coord);

public static partial class DevSeed
{
    public const string DevPassword = "senha-dev-123";

    public static async Task<SeededTenant> SeedTenantAsync(DataSources sources, PasswordHasher hasher, string slug, string password, CancellationToken ct = default)
    {
        var hash = hasher.Hash(password);
        await using var conn = await sources.Superadmin.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var (tenant, admin) = await conn.QuerySingleAsync<(Guid, Guid)>(
            """
            select tenant_id, admin_user_id
              from app.provision_tenant(@slug, @name, 'Administração', @adminEmail, 'aprovec', date '2026-08-01')
            """,
            new { slug, name = slug == "aprovec" ? "APROVEC" : $"Empresa {slug}", adminEmail = $"admin@{slug}.local" },
            tx);
        await conn.ExecuteAsync("update users set status = 'ativo', password_hash = @hash where id = @admin", new { hash, admin }, tx);

        async Task<Guid> UserAsync(string name, string login, Guid? supervisor, string template)
        {
            var id = Guid.CreateVersion7();
            await conn.ExecuteAsync(
                """
                insert into users (id, tenant_id, name, email, password_hash, status, supervisor_id)
                values (@id, @tenant, @name, @email, @hash, 'ativo', @supervisor)
                """,
                new { id, tenant, name, email = $"{login}@{slug}.local", hash, supervisor }, tx);
            await conn.ExecuteAsync(
                """
                insert into user_roles (tenant_id, user_id, role_id)
                select @tenant, @id, id from roles where tenant_id = @tenant and source_template_key = @template
                """,
                new { tenant, id, template }, tx);
            return id;
        }

        var joao = await UserAsync("João Silva", "joao", null, "consultor");
        var maria = await UserAsync("Maria Oliveira", "maria", joao, "consultor");
        var pedro = await UserAsync("Pedro Santos", "pedro", maria, "consultor");
        var coord = await UserAsync("Carla Coordenação", "coordenacao", null, "coordenador");
        await conn.ExecuteAsync(
            """
            insert into commission_group_members (tenant_id, group_id, user_id)
            select @tenant, id, @coord from commission_groups where tenant_id = @tenant and name = 'Coordenação'
            """,
            new { tenant, coord }, tx);

        await BoletosAsync(conn, tx, tenant, joao, "J7", 70, 200m, "2026-09-01");
        await BoletosAsync(conn, tx, tenant, joao, "J9", 20, 300m, "2026-09-01");
        await BoletosAsync(conn, tx, tenant, joao, "JA", 12, 300m, "2026-08-01", "atraso");
        await BoletosAsync(conn, tx, tenant, joao, "JC", 4, 200m, "2026-09-01", "cancelado");
        await BoletosAsync(conn, tx, tenant, maria, "M9", 50, 200m, "2026-09-01");
        await BoletosAsync(conn, tx, tenant, pedro, "P9", 25, 200m, "2026-09-01");
        await BoletosAsync(conn, tx, tenant, joao, "J8", 90, 200m, "2026-08-01");
        await BoletosAsync(conn, tx, tenant, maria, "M8", 45, 200m, "2026-08-01");
        await BoletosAsync(conn, tx, tenant, pedro, "P8", 20, 200m, "2026-08-01");

        await tx.CommitAsync(ct);
        return new SeededTenant(tenant, slug, admin, joao, maria, pedro, coord);
    }

    private static Task BoletosAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenant, Guid owner, string prefix,
        int count, decimal valor, string month, string status = "recebido") =>
        conn.ExecuteAsync(
            """
            insert into boletos (tenant_id, participante_id, associado_ref, associado_nome, placa, valor, status, vencimento, pago_em)
            select @tenant, @owner, @prefix || '-' || g, 'Associado ' || @prefix || ' ' || g,
                   'TST' || lpad(g::text, 4, '0'), @valor, @status, @month::date + 4,
                   case when @status = 'recebido' then @month::date + (g % 10) end
              from generate_series(1, @count) g
            """,
            new { tenant, owner, prefix, count, valor, month, status }, tx);
}
