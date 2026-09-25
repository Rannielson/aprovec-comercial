namespace Recorrencia.TestSupport;

public sealed record RlsScenario(
    Guid TenantA, Guid TenantB, Guid Admin, Guid Joao, Guid Maria, Guid Pedro, Guid Coord, Guid OutsiderAdmin)
{
    public static async Task<RlsScenario> CreateAsync(PostgresFixture db)
    {
        var seed = new Seed(db);
        var (a, admin) = await seed.ProvisionAsync();
        var (b, outsider) = await seed.ProvisionAsync();
        await seed.ExecAsync(
            "update users set status = 'ativo', password_hash = 'hash-de-teste' where id = any(@ids)",
            new { ids = new[] { admin, outsider } });

        var joao = await seed.UserAsync(a, "João");
        var maria = await seed.UserAsync(a, "Maria", joao);
        var pedro = await seed.UserAsync(a, "Pedro", maria);
        var coord = await seed.UserAsync(a, "Coordenação");
        foreach (var consultor in new[] { joao, maria, pedro })
            await seed.AssignTemplateRoleAsync(a, consultor, "consultor");
        await seed.AssignTemplateRoleAsync(a, coord, "coordenador");
        await seed.ExecAsync(
            """
            insert into commission_group_members (tenant_id, group_id, user_id)
            select @a, id, @coord from commission_groups where tenant_id = @a and name = 'Coordenação'
            """,
            new { a, coord });

        await seed.BoletoAsync(a, joao, 100m, "recebido", "2026-09-05");
        await seed.BoletoAsync(a, joao, 100m, "recebido", "2026-09-06");
        await seed.BoletoAsync(a, joao, 300m, "atraso", null);
        await seed.BoletoAsync(a, joao, 80m, "recebido", "2026-08-10");
        await seed.BoletoAsync(a, maria, 50m, "recebido", "2026-09-07");
        await seed.BoletoAsync(a, maria, 50m, "recebido", "2026-09-08");
        await seed.BoletoAsync(a, pedro, 40m, "recebido", "2026-09-09");
        await seed.BoletoAsync(b, outsider, 999m, "recebido", "2026-09-05");

        return new RlsScenario(a, b, admin, joao, maria, pedro, coord, outsider);
    }
}
