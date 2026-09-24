namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class ProvisioningTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    [Fact]
    public async Task Provisioning_creates_modules_roles_admin_and_plan()
    {
        var (t, admin) = await _seed.ProvisionAsync();

        Assert.Equal(7, await _seed.ScalarAsync<int>("select count(*)::int from tenant_modules where tenant_id = @t and enabled", new { t }));
        Assert.Equal(3, await _seed.ScalarAsync<int>("select count(*)::int from roles where tenant_id = @t", new { t }));
        Assert.Equal(26, await _seed.ScalarAsync<int>("select count(*)::int from role_permissions where tenant_id = @t", new { t }));
        Assert.Equal("convidado|administrador", await _seed.ScalarAsync<string>(
            """
            select u.status || '|' || r.source_template_key
              from users u join user_roles ur on ur.user_id = u.id join roles r on r.id = ur.role_id
             where u.id = @admin
            """, new { admin }));

        var rules = (await (await db.OpenAsync(db.OwnerConnectionString)).QueryAsync<(string Type, decimal Rate, int? Level, string? Group)>(
            """
            select r.type, r.rate, r.level, g.name
              from commission_plans p
              join commission_rules r on r.plan_id = p.id
              left join commission_groups g on g.id = r.group_id
             where p.tenant_id = @t and p.status = 'ativo' and p.effective_from = date '2026-08-01'
             order by r.type
            """, new { t })).ToList();

        Assert.Equal(
            new List<(string, decimal, int?, string?)> { ("global", 0.0100m, null, "Coordenação"), ("own", 0.0700m, null, null), ("upline", 0.0200m, 1, null) },
            rules.Select(r => (r.Type, r.Rate, r.Level, r.Group)).ToList());
    }

    [Fact]
    public async Task Provisioning_without_plan_template_creates_no_plan()
    {
        var (t, _) = await _seed.ProvisionAsync(planTemplate: null);
        Assert.Equal(0, await _seed.ScalarAsync<int>("select count(*)::int from commission_plans where tenant_id = @t", new { t }));
    }

    [Fact]
    public async Task Unknown_plan_template_rolls_everything_back()
    {
        var slug = Seed.UniqueSlug();
        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ProvisionAsync(slug, planTemplate: "inexistente"));

        Assert.Equal("plan.template_not_found", ex.MessageText);
        Assert.Equal(0, await _seed.ScalarAsync<int>("select count(*)::int from tenants where slug = @slug", new { slug }));
    }

    [Fact]
    public async Task App_user_cannot_provision_tenants()
    {
        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(null, null, (c, t) => c.ExecuteAsync(
            "select * from app.provision_tenant('x', 'X', 'Admin', 'a@b.c', null, null)", transaction: t)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }
}
