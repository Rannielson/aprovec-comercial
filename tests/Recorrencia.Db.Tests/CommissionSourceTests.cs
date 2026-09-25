namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class CommissionSourceTests(PostgresFixture db)
{
    private static readonly DateOnly Setembro = new(2026, 9, 1);

    private Task<List<(Guid BoletoId, Guid Owner, decimal Valor)>> SourcesAsync(Guid tenant, Guid user, DateOnly competencia, params Guid[] beneficiaries) =>
        db.AsAppUserAsync(tenant, user, async (c, t) => (await c.QueryAsync<(Guid, Guid, decimal)>(
            "select boleto_id, participante_id, valor from app.commission_source_boletos(@competencia, @beneficiaries)",
            new { competencia, beneficiaries }, t)).ToList());

    [Fact]
    public async Task Consultor_gets_own_and_first_level_boletos_of_the_month()
    {
        var s = await RlsScenario.CreateAsync(db);
        var rows = await SourcesAsync(s.TenantA, s.Joao, Setembro, s.Joao);

        Assert.Equal(4, rows.Count);
        Assert.Equal(300m, rows.Sum(r => r.Valor));
        Assert.DoesNotContain(rows, r => r.Owner == s.Pedro);
    }

    [Fact]
    public async Task Member_of_a_global_group_gets_every_paid_boleto_of_the_tenant()
    {
        var s = await RlsScenario.CreateAsync(db);
        var rows = await SourcesAsync(s.TenantA, s.Coord, Setembro, s.Coord);

        Assert.Equal(5, rows.Count);
        Assert.Equal(340m, rows.Sum(r => r.Valor));
    }

    [Fact]
    public async Task Consultor_cannot_ask_for_someone_else()
    {
        var s = await RlsScenario.CreateAsync(db);
        var ex = await DbExtensions.ThrowsPgAsync(() => SourcesAsync(s.TenantA, s.Joao, Setembro, s.Maria));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Equal("forbidden", ex.MessageText);
    }

    [Fact]
    public async Task Competencia_must_be_the_first_day_of_the_month()
    {
        var s = await RlsScenario.CreateAsync(db);
        var ex = await DbExtensions.ThrowsPgAsync(() => SourcesAsync(s.TenantA, s.Joao, new DateOnly(2026, 9, 15), s.Joao));
        Assert.Equal("commission.invalid_competencia", ex.MessageText);
    }

    [Fact]
    public async Task Months_before_any_plan_have_no_sources()
    {
        var s = await RlsScenario.CreateAsync(db);
        Assert.Empty(await SourcesAsync(s.TenantA, s.Coord, new DateOnly(2026, 7, 1), s.Coord));
    }

    [Fact]
    public async Task Paths_stop_at_the_deepest_upline_level_of_the_plan()
    {
        var s = await RlsScenario.CreateAsync(db);
        var paths = await db.AsAppUserAsync(s.TenantA, s.Joao, async (c, t) => (await c.QueryAsync<(Guid, Guid, int)>(
            "select ancestor_id, descendant_id, depth from app.commission_source_paths(@competencia, @beneficiaries)",
            new { competencia = Setembro, beneficiaries = new[] { s.Joao } }, t)).ToList());

        Assert.Equal(new List<(Guid, Guid, int)> { (s.Joao, s.Maria, 1) }, paths);
    }

    [Fact]
    public async Task Beneficiaries_follow_the_comissoes_scope()
    {
        var s = await RlsScenario.CreateAsync(db);

        var joao = await db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) =>
            c.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()", transaction: t));
        Assert.Equal(new[] { s.Joao }, joao);

        var coord = await db.AsAppUserAsync(s.TenantA, s.Coord, (c, t) =>
            c.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()", transaction: t));
        Assert.Equal(5, coord!.Length);
    }

    [Fact]
    public async Task Plan_for_competencia_picks_the_latest_active_version()
    {
        var s = await RlsScenario.CreateAsync(db);
        var seed = new Seed(db);
        var october = await seed.ScalarAsync<Guid>(
            "insert into commission_plans (tenant_id, name, effective_from) values (@a, 'Outubro', date '2026-10-01') returning id",
            new { a = s.TenantA });
        await seed.ExecAsync("insert into commission_rules (tenant_id, plan_id, type, rate) values (@a, @october, 'own', 0.05)", new { a = s.TenantA, october });
        await seed.ExecAsync("update commission_plans set status = 'ativo', activated_at = now() where id = @october", new { october });

        async Task<Guid?> PlanFor(DateOnly competencia) => await db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) =>
            c.ExecuteScalarAsync<Guid?>("select app.commission_plan_for(@competencia)", new { competencia }, t));

        Assert.NotEqual(october, await PlanFor(Setembro));
        Assert.Equal(october, await PlanFor(new DateOnly(2026, 10, 1)));
        Assert.Equal(october, await PlanFor(new DateOnly(2026, 12, 1)));
    }

    [Fact]
    public async Task Fechamento_status_ignores_the_visualization_permission()
    {
        var s = await RlsScenario.CreateAsync(db);
        var seed = new Seed(db);
        var f = await seed.ScalarAsync<Guid>(
            "insert into fechamentos (tenant_id, competencia) values (@a, date '2026-08-01') returning id", new { a = s.TenantA });
        var semPerfil = await seed.UserAsync(s.TenantA, "Sem perfil");

        Task<List<(Guid, string)>> StatusAsync(Guid tenant, Guid user) =>
            db.AsAppUserAsync(tenant, user, async (c, t) => (await c.QueryAsync<(Guid, string)>(
                "select fechamento_id, status from app.fechamento_status(@competencia)",
                new { competencia = new DateOnly(2026, 8, 1) }, t)).ToList());

        Assert.Equal(new List<(Guid, string)> { (f, "apuracao") }, await StatusAsync(s.TenantA, semPerfil));
        Assert.Empty(await StatusAsync(s.TenantB, s.OutsiderAdmin));
    }
}
