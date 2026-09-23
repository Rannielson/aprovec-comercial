namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class CommissionSchemaTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private Task<Guid> DraftPlanAsync(Guid tenant, string effectiveFrom = "2026-10-01") =>
        _seed.ScalarAsync<Guid>(
            "insert into commission_plans (tenant_id, name, effective_from) values (@tenant, 'Rascunho', @effectiveFrom::date) returning id",
            new { tenant, effectiveFrom });

    [Fact]
    public async Task Active_plan_rules_cannot_change()
    {
        var (t, _) = await _seed.ProvisionAsync();

        var update = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update commission_rules set rate = 0.0800 where tenant_id = @t and type = 'own'", new { t }));
        Assert.Equal("plan.active_immutable", update.MessageText);

        var delete = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "delete from commission_plans where tenant_id = @t", new { t }));
        Assert.Equal("plan.active_immutable", delete.MessageText);
    }

    [Fact]
    public async Task Empty_plan_cannot_be_activated()
    {
        var (t, _) = await _seed.ProvisionAsync(planTemplate: null);
        var plan = await DraftPlanAsync(t);

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update commission_plans set status = 'ativo', activated_at = now() where id = @plan", new { plan }));
        Assert.Equal("plan.empty", ex.MessageText);
    }

    [Fact]
    public async Task Upline_rule_requires_level_and_levels_are_unique()
    {
        var (t, _) = await _seed.ProvisionAsync(planTemplate: null);
        var plan = await DraftPlanAsync(t);

        var noLevel = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "insert into commission_rules (tenant_id, plan_id, type, rate) values (@t, @plan, 'upline', 0.02)", new { t, plan }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, noLevel.SqlState);

        await _seed.ExecAsync("insert into commission_rules (tenant_id, plan_id, type, rate, level) values (@t, @plan, 'upline', 0.02, 1)", new { t, plan });
        var duplicate = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "insert into commission_rules (tenant_id, plan_id, type, rate, level) values (@t, @plan, 'upline', 0.01, 1)", new { t, plan }));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [Fact]
    public async Task Rate_must_be_between_zero_and_one()
    {
        var (t, _) = await _seed.ProvisionAsync(planTemplate: null);
        var plan = await DraftPlanAsync(t);

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "insert into commission_rules (tenant_id, plan_id, type, rate) values (@t, @plan, 'own', 1.5)", new { t, plan }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Two_active_plans_cannot_share_the_effective_date()
    {
        var (t, _) = await _seed.ProvisionAsync();
        var plan = await DraftPlanAsync(t, "2026-08-01");
        await _seed.ExecAsync("insert into commission_rules (tenant_id, plan_id, type, rate) values (@t, @plan, 'own', 0.05)", new { t, plan });

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update commission_plans set status = 'ativo', activated_at = now() where id = @plan", new { plan }));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task Effective_date_must_be_the_first_day_of_a_month()
    {
        var (t, _) = await _seed.ProvisionAsync(planTemplate: null);
        var ex = await DbExtensions.ThrowsPgAsync(() => DraftPlanAsync(t, "2026-10-15"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }
}
