namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class RbacTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private Task<string?> ScopeForAsync(Guid tenant, Guid user, string permission) =>
        db.AsAppUserAsync(tenant, user, (c, t) => c.ExecuteScalarAsync<string?>("select app.scope_for(@permission)", new { permission }, t));

    private Task<bool> HasAsync(Guid tenant, Guid user, string permission) =>
        db.AsAppUserAsync(tenant, user, (c, t) => c.ExecuteScalarAsync<bool>("select app.has_permission(@permission)", new { permission }, t));

    [Fact]
    public async Task Catalog_and_templates_are_seeded()
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        var rows = (await conn.QueryAsync<(string Template, string Permission, string? Scope)>(
            "select template_key, permission_key, scope from role_template_permissions")).ToList();

        var consultor = rows.Where(r => r.Template == "consultor").Select(r => (r.Permission, r.Scope)).ToHashSet();
        var expectedConsultor = new HashSet<(string, string?)>
        {
            ("carteira.visualizar", "own"), ("comissoes.visualizar", "own"),
            ("fechamento.visualizar", null), ("estrutura.visualizar", "direct"),
        };
        Assert.True(expectedConsultor.SetEquals(consultor));
        Assert.Equal(7, rows.Count(r => r.Template == "coordenador"));
        Assert.Equal(15, await conn.ExecuteScalarAsync<int>("select count(*) from permissions"));
        Assert.Equal(15, rows.Count(r => r.Template == "administrador"));
        Assert.All(rows.Where(r => r.Template == "administrador" && r.Scope is not null), r => Assert.Equal("tenant", r.Scope));
    }

    [Fact]
    public async Task Scoped_permission_requires_a_scope()
    {
        var t = await _seed.TenantAsync();
        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.RoleAsync(t, "Sem escopo", ("carteira.visualizar", null)));
        Assert.Equal("role.scope_required", ex.MessageText);
    }

    [Fact]
    public async Task Unscoped_permission_rejects_a_scope()
    {
        var t = await _seed.TenantAsync();
        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.RoleAsync(t, "Com escopo", ("usuarios.convidar", "own")));
        Assert.Equal("role.scope_not_allowed", ex.MessageText);
    }

    [Fact]
    public async Task Effective_scope_is_the_largest_among_roles()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "João");
        var r1 = await _seed.RoleAsync(t, "Próprio", ("carteira.visualizar", "own"));
        var r2 = await _seed.RoleAsync(t, "Estrutura", ("carteira.visualizar", "subtree"), ("carteira.exportar", null));
        await _seed.AssignRoleAsync(t, u, r1);
        await _seed.AssignRoleAsync(t, u, r2);

        Assert.Equal("subtree", await ScopeForAsync(t, u, "carteira.visualizar"));
        Assert.True(await HasAsync(t, u, "carteira.exportar"));
        Assert.Null(await ScopeForAsync(t, u, "carteira.exportar"));
        Assert.False(await HasAsync(t, u, "usuarios.convidar"));
    }

    [Fact]
    public async Task Disabled_module_removes_its_permissions()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "João");
        await _seed.AssignRoleAsync(t, u, await _seed.RoleAsync(t, "Carteira", ("carteira.visualizar", "own")));
        await _seed.ExecAsync("update tenant_modules set enabled = false where tenant_id = @t and module_key = 'carteira'", new { t });

        Assert.Null(await ScopeForAsync(t, u, "carteira.visualizar"));
        Assert.False(await HasAsync(t, u, "carteira.visualizar"));
    }

    [Fact]
    public async Task Deactivated_user_has_no_permissions()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "João");
        await _seed.AssignRoleAsync(t, u, await _seed.RoleAsync(t, "Carteira", ("carteira.visualizar", "own")));
        await _seed.ExecAsync("update users set status = 'desligado' where id = @u", new { u });

        Assert.False(await HasAsync(t, u, "carteira.visualizar"));
    }

    [Fact]
    public async Task Without_context_there_are_no_permissions()
    {
        var count = await db.AsAppUserAsync(null, null, (c, t) =>
            c.ExecuteScalarAsync<long>("select count(*) from app.effective_permissions()", transaction: t));
        Assert.Equal(0L, count);
    }

    [Fact]
    public async Task Visible_owner_ids_follow_the_scope()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);
        var pedro = await _seed.UserAsync(t, "Pedro", maria);

        async Task<HashSet<Guid>> Visible(string scope) =>
            (await db.AsAppUserAsync(t, joao, (c, tx) =>
                c.ExecuteScalarAsync<Guid[]>("select app.visible_owner_ids(@scope)", new { scope }, tx)))!.ToHashSet();

        Assert.True(new HashSet<Guid> { joao }.SetEquals(await Visible("own")));
        Assert.True(new HashSet<Guid> { joao, maria }.SetEquals(await Visible("direct")));
        Assert.True(new HashSet<Guid> { joao, maria, pedro }.SetEquals(await Visible("subtree")));
        Assert.Empty(await Visible("tenant"));
    }

    [Fact]
    public async Task Count_active_with_permission_ignores_deactivated_users()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var role = await _seed.RoleAsync(t, "Gestor", ("usuarios.gerenciar_perfis", null));
        var a = await _seed.UserAsync(t, "A");
        var b = await _seed.UserAsync(t, "B");
        await _seed.AssignRoleAsync(t, a, role);
        await _seed.AssignRoleAsync(t, b, role);
        await _seed.ExecAsync("update users set status = 'desligado' where id = @b", new { b });

        var count = await db.AsAppUserAsync(t, a, (c, tx) =>
            c.ExecuteScalarAsync<int>("select app.count_active_with_permission('usuarios.gerenciar_perfis')", transaction: tx));
        Assert.Equal(1, count);
    }
}
