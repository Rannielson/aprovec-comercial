namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class HierarchyTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private async Task<HashSet<(Guid, Guid, int)>> PathsAsync(Guid tenantId)
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        var rows = await conn.QueryAsync<(Guid, Guid, int)>(
            "select ancestor_id, descendant_id, depth from hierarchy_paths where tenant_id = @tenantId",
            new { tenantId });
        return rows.ToHashSet();
    }

    [Fact]
    public async Task Inserting_users_builds_closure_paths()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);
        var pedro = await _seed.UserAsync(t, "Pedro", maria);

        var expected = new HashSet<(Guid, Guid, int)>
        {
            (joao, joao, 0), (maria, maria, 0), (pedro, pedro, 0),
            (joao, maria, 1), (maria, pedro, 1), (joao, pedro, 2),
        };
        Assert.True(expected.SetEquals(await PathsAsync(t)));
    }

    [Fact]
    public async Task Inserting_under_a_recently_moved_supervisor_sees_the_new_ancestor_chain()
    {
        // Regression for the advisory lock added to hierarchy_after_insert: the lock's
        // per-tenant scoping must line up with hierarchy_before_update's, so that a supervisor
        // move and a subsequent insert under that supervisor never interleave into a stale
        // ancestor chain. This exercises the same lock/read path sequentially (no real
        // concurrency), just to confirm the lock addition didn't change correctness.
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria");
        var bruno = await _seed.UserAsync(t, "Bruno", joao);

        await _seed.ExecAsync("update users set supervisor_id = @maria where id = @bruno", new { maria, bruno });
        var pedro = await _seed.UserAsync(t, "Pedro", bruno);

        var expected = new HashSet<(Guid, Guid, int)>
        {
            (joao, joao, 0), (maria, maria, 0), (bruno, bruno, 0), (pedro, pedro, 0),
            (maria, bruno, 1), (maria, pedro, 2), (bruno, pedro, 1),
        };
        Assert.True(expected.SetEquals(await PathsAsync(t)));
    }

    [Fact]
    public async Task Moving_a_user_moves_the_whole_subtree()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);
        var pedro = await _seed.UserAsync(t, "Pedro", maria);
        var bruno = await _seed.UserAsync(t, "Bruno");

        await _seed.ExecAsync("update users set supervisor_id = @bruno where id = @maria", new { bruno, maria });

        var expected = new HashSet<(Guid, Guid, int)>
        {
            (joao, joao, 0), (bruno, bruno, 0), (maria, maria, 0), (pedro, pedro, 0),
            (maria, pedro, 1), (bruno, maria, 1), (bruno, pedro, 2),
        };
        Assert.True(expected.SetEquals(await PathsAsync(t)));
    }

    [Fact]
    public async Task Removing_the_supervisor_detaches_the_subtree()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);

        await _seed.ExecAsync("update users set supervisor_id = null where id = @maria", new { maria });

        var expected = new HashSet<(Guid, Guid, int)> { (joao, joao, 0), (maria, maria, 0) };
        Assert.True(expected.SetEquals(await PathsAsync(t)));
    }

    [Fact]
    public async Task Cycles_are_rejected()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);
        var pedro = await _seed.UserAsync(t, "Pedro", maria);

        var ex = await DbExtensions.ThrowsPgAsync(() =>
            _seed.ExecAsync("update users set supervisor_id = @pedro where id = @joao", new { pedro, joao }));
        Assert.Equal("hierarchy.cycle", ex.MessageText);
    }

    [Fact]
    public async Task Self_supervision_is_rejected()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");

        var ex = await DbExtensions.ThrowsPgAsync(() =>
            _seed.ExecAsync("update users set supervisor_id = @joao where id = @joao", new { joao }));
        Assert.Equal("hierarchy.cycle", ex.MessageText);
    }

    [Fact]
    public async Task Supervisor_must_belong_to_the_same_tenant()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        var a = await _seed.UserAsync(t1, "A");

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.UserAsync(t2, "B", a));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    // Fix E: app.users_column_guard() must enforce that each column of `users` can only be
    // changed by a caller holding the specific permission that governs it, instead of the
    // RLS policy's coarse "estrutura.editar OR usuarios.desligar" OR-gate letting either
    // permission unlock every column.

    [Fact]
    public async Task Estrutura_editar_alone_cannot_change_status()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "Editor de estrutura");
        var role = await _seed.RoleAsync(t, "Só estrutura", ("estrutura.editar", null));
        await _seed.AssignRoleAsync(t, u, role);

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t, u, (c, tx) =>
            c.ExecuteAsync("update users set status = 'desligado' where id = @u", new { u }, tx)));
        Assert.Equal("auth.forbidden", ex.MessageText);
    }

    [Fact]
    public async Task Usuarios_desligar_alone_cannot_change_supervisor_id()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "Desliga usuários");
        var other = await _seed.UserAsync(t, "Outro");
        var role = await _seed.RoleAsync(t, "Só desligar", ("usuarios.desligar", null));
        await _seed.AssignRoleAsync(t, u, role);

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t, u, (c, tx) =>
            c.ExecuteAsync("update users set supervisor_id = @other where id = @u", new { u, other }, tx)));
        Assert.Equal("auth.forbidden", ex.MessageText);
    }

    [Fact]
    public async Task Estrutura_editar_alone_cannot_change_email()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "Editor de estrutura");
        var role = await _seed.RoleAsync(t, "Só estrutura", ("estrutura.editar", null));
        await _seed.AssignRoleAsync(t, u, role);

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t, u, (c, tx) =>
            c.ExecuteAsync("update users set email = 'novo@teste.local' where id = @u", new { u }, tx)));
        Assert.Equal("auth.forbidden", ex.MessageText);
    }

    [Fact]
    public async Task Estrutura_editar_can_change_supervisor_id()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "Editor de estrutura");
        var other = await _seed.UserAsync(t, "Outro");
        var role = await _seed.RoleAsync(t, "Só estrutura", ("estrutura.editar", null));
        await _seed.AssignRoleAsync(t, u, role);

        var affected = await db.AsAppUserAsync(t, u, (c, tx) =>
            c.ExecuteAsync("update users set supervisor_id = @other where id = @u", new { u, other }, tx));
        Assert.Equal(1, affected);
    }

    [Fact]
    public async Task Usuarios_desligar_can_change_status()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "Desliga usuários");
        var role = await _seed.RoleAsync(t, "Só desligar", ("usuarios.desligar", null));
        await _seed.AssignRoleAsync(t, u, role);

        var affected = await db.AsAppUserAsync(t, u, (c, tx) =>
            c.ExecuteAsync("update users set status = 'desligado' where id = @u", new { u }, tx));
        Assert.Equal(1, affected);
    }

    // Fix for the PUT /users/{id} 403 regression: app.users_column_guard() used to forbid
    // changing name/email unconditionally, with no permission check at all. It now mirrors
    // the supervisor_id/status blocks by gating name/email changes on 'usuarios.convidar',
    // the same permission that already gates PUT /users/{id} at the route level.

    [Fact]
    public async Task Without_usuarios_convidar_changing_name_or_email_is_rejected()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "Sem permissão");
        var role = await _seed.RoleAsync(t, "Só estrutura", ("estrutura.editar", null));
        await _seed.AssignRoleAsync(t, u, role);

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t, u, (c, tx) =>
            c.ExecuteAsync("update users set name = 'Novo Nome' where id = @u", new { u }, tx)));
        Assert.Equal("auth.forbidden", ex.MessageText);
    }

    [Fact]
    public async Task With_usuarios_convidar_changing_name_and_email_succeeds()
    {
        // users_update RLS (0011_rls.sql) only lets estrutura.editar/usuarios.desligar holders
        // reach a row for update at all; usuarios.convidar alone can insert (invite) but not
        // update. So this role also needs estrutura.editar to get past RLS, isolating what's
        // under test here to the column guard trigger's own name/email permission check.
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "Com permissão");
        var role = await _seed.RoleAsync(t, "Convida", ("estrutura.editar", null), ("usuarios.convidar", null));
        await _seed.AssignRoleAsync(t, u, role);

        var affected = await db.AsAppUserAsync(t, u, (c, tx) =>
            c.ExecuteAsync(
                "update users set name = 'Novo Nome', email = 'novo@teste.local' where id = @u", new { u }, tx));
        Assert.Equal(1, affected);
    }
}
