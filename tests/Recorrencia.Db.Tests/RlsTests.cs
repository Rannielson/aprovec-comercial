namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class RlsTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private Task<long> CountAsync(Guid? tenant, Guid? user, string table) =>
        db.AsAppUserAsync(tenant, user, (c, t) => c.ExecuteScalarAsync<long>($"select count(*) from {table}", transaction: t));

    private Task<HashSet<Guid>> BoletoOwnersAsync(Guid tenant, Guid user) =>
        db.AsAppUserAsync(tenant, user, async (c, t) =>
            (await c.QueryAsync<Guid>("select distinct participante_id from boletos", transaction: t)).ToHashSet());

    [Fact]
    public async Task Without_context_nothing_is_visible()
    {
        await RlsScenario.CreateAsync(db);
        Assert.Equal(0L, await CountAsync(null, null, "users"));
        Assert.Equal(0L, await CountAsync(null, null, "boletos"));
        Assert.Equal(0L, await CountAsync(null, null, "roles"));
    }

    [Fact]
    public async Task Consultor_sees_only_own_boletos()
    {
        var s = await RlsScenario.CreateAsync(db);
        Assert.Equal(4L, await CountAsync(s.TenantA, s.Joao, "boletos"));
        Assert.True(new HashSet<Guid> { s.Joao }.SetEquals(await BoletoOwnersAsync(s.TenantA, s.Joao)));
    }

    [Fact]
    public async Task Largest_scope_among_roles_wins()
    {
        var s = await RlsScenario.CreateAsync(db);
        var direct = await _seed.RoleAsync(s.TenantA, "Supervisor direto", ("carteira.visualizar", "direct"));
        await _seed.AssignRoleAsync(s.TenantA, s.Joao, direct);

        Assert.Equal(6L, await CountAsync(s.TenantA, s.Joao, "boletos"));
        Assert.True(new HashSet<Guid> { s.Joao, s.Maria }.SetEquals(await BoletoOwnersAsync(s.TenantA, s.Joao)));
    }

    [Fact]
    public async Task Subtree_scope_includes_every_level_below()
    {
        var s = await RlsScenario.CreateAsync(db);
        var subtree = await _seed.RoleAsync(s.TenantA, "Gerente", ("carteira.visualizar", "subtree"));
        await _seed.AssignRoleAsync(s.TenantA, s.Joao, subtree);

        Assert.Equal(7L, await CountAsync(s.TenantA, s.Joao, "boletos"));
    }

    [Fact]
    public async Task Tenant_scope_never_crosses_tenants()
    {
        var s = await RlsScenario.CreateAsync(db);
        Assert.Equal(7L, await CountAsync(s.TenantA, s.Coord, "boletos"));
        Assert.Equal(1L, await CountAsync(s.TenantB, s.OutsiderAdmin, "boletos"));
    }

    [Fact]
    public async Task Context_with_mismatched_tenant_sees_nothing()
    {
        var s = await RlsScenario.CreateAsync(db);
        Assert.Equal(0L, await CountAsync(s.TenantB, s.Coord, "boletos"));
    }

    [Fact]
    public async Task Users_follow_the_estrutura_scope()
    {
        var s = await RlsScenario.CreateAsync(db);

        var joaoSees = await db.AsAppUserAsync(s.TenantA, s.Joao, async (c, t) =>
            (await c.QueryAsync<Guid>("select id from users", transaction: t)).ToHashSet());
        Assert.True(new HashSet<Guid> { s.Joao, s.Maria }.SetEquals(joaoSees));

        var pedroSees = await db.AsAppUserAsync(s.TenantA, s.Pedro, async (c, t) =>
            (await c.QueryAsync<Guid>("select id from users", transaction: t)).ToHashSet());
        Assert.True(new HashSet<Guid> { s.Pedro }.SetEquals(pedroSees));

        Assert.Equal(5L, await CountAsync(s.TenantA, s.Coord, "users"));
    }

    [Fact]
    public async Task Hierarchy_paths_follow_the_estrutura_scope()
    {
        var s = await RlsScenario.CreateAsync(db);
        Assert.Equal(3L, await CountAsync(s.TenantA, s.Joao, "hierarchy_paths"));
    }

    [Fact]
    public async Task Disabled_module_hides_its_rows()
    {
        var s = await RlsScenario.CreateAsync(db);
        await _seed.ExecAsync("update tenant_modules set enabled = false where tenant_id = @t and module_key = 'carteira'", new { t = s.TenantA });
        Assert.Equal(0L, await CountAsync(s.TenantA, s.Coord, "boletos"));
    }

    [Fact]
    public async Task Writing_boletos_requires_integracoes_gerenciar()
    {
        // Consultor (Joao) doesn't hold integracoes.gerenciar -- the Hinova boleto import is the
        // only write path onto boletos, and it's gated the same way every other write this
        // integration makes already is.
        var s = await RlsScenario.CreateAsync(db);
        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) => c.ExecuteAsync(
            """
            insert into boletos (tenant_id, participante_id, associado_ref, associado_nome, valor, status, vencimento)
            values (@a, @a2, 'X', 'X', 10, 'a_vencer', date '2026-10-01')
            """, new { a = s.TenantA, a2 = s.Joao }, t)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Administrador_can_write_boletos()
    {
        var s = await RlsScenario.CreateAsync(db);
        var affected = await db.AsAppUserAsync(s.TenantA, s.Admin, (c, t) => c.ExecuteAsync(
            """
            insert into boletos (tenant_id, participante_id, associado_ref, associado_nome, valor, status, vencimento)
            values (@a, @a2, 'X', 'X', 10, 'a_vencer', date '2026-10-01')
            """, new { a = s.TenantA, a2 = s.Admin }, t));
        Assert.Equal(1, affected);
    }

    [Fact]
    public async Task Consultor_cannot_create_roles()
    {
        var s = await RlsScenario.CreateAsync(db);
        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) => c.ExecuteAsync(
            "insert into roles (tenant_id, name) values (@a, 'Invasor')", new { a = s.TenantA }, t)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Admin_cannot_write_into_another_tenant()
    {
        var s = await RlsScenario.CreateAsync(db);
        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(s.TenantA, s.Admin, (c, t) => c.ExecuteAsync(
            "insert into roles (tenant_id, name) values (@b, 'Perfil alheio')", new { b = s.TenantB }, t)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        await db.AsAppUserAsync(s.TenantA, s.Admin, (c, t) => c.ExecuteAsync(
            "insert into roles (tenant_id, name) values (@a, 'Perfil próprio')", new { a = s.TenantA }, t));
    }

    [Fact]
    public async Task Consultor_updates_to_users_affect_no_rows()
    {
        var s = await RlsScenario.CreateAsync(db);
        var affected = await db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) => c.ExecuteAsync(
            "update users set name = 'Alterado' where id = @m", new { m = s.Maria }, t));
        Assert.Equal(0, affected);
    }

    [Fact]
    public async Task Audit_entries_must_belong_to_the_current_user()
    {
        var s = await RlsScenario.CreateAsync(db);
        const string sql = "insert into audit_log (tenant_id, user_id, action, entity) values (@a, @u, 'teste', 'users')";

        await db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) => c.ExecuteAsync(sql, new { a = s.TenantA, u = s.Joao }, t));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) =>
            c.ExecuteAsync(sql, new { a = s.TenantA, u = s.Maria }, t)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Commission_tables_are_readable_by_everyone_but_writable_only_with_permission()
    {
        var s = await RlsScenario.CreateAsync(db);
        Assert.Equal(3L, await CountAsync(s.TenantA, s.Joao, "commission_rules"));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) => c.ExecuteAsync(
            "insert into commission_groups (tenant_id, name) values (@a, 'Grupo do João')", new { a = s.TenantA }, t)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        await db.AsAppUserAsync(s.TenantA, s.Admin, (c, t) => c.ExecuteAsync(
            "insert into commission_groups (tenant_id, name) values (@a, 'Grupo do admin')", new { a = s.TenantA }, t));
    }

    [Fact]
    public async Task Frozen_details_follow_the_comissoes_scope()
    {
        var s = await RlsScenario.CreateAsync(db);
        var f = await _seed.ScalarAsync<Guid>(
            "insert into fechamentos (tenant_id, competencia) values (@a, date '2026-09-01') returning id", new { a = s.TenantA });
        await _seed.ExecAsync(
            """
            insert into fechamento_detalhes
              (tenant_id, fechamento_id, beneficiario_id, origem_participante_id, boleto_id, rule_id, rule_type, rate, base, valor)
            select @a, @f, b.participante_id, b.participante_id, b.id, gen_random_uuid(), 'own', 0.07, b.valor, round(b.valor * 0.07, 2)
              from boletos b
             where b.tenant_id = @a and b.participante_id in (@j, @m) and b.status = 'recebido' and b.pago_em >= date '2026-09-01'
            """, new { a = s.TenantA, f, j = s.Joao, m = s.Maria });

        Assert.Equal(2L, await CountAsync(s.TenantA, s.Joao, "fechamento_detalhes"));
        Assert.Equal(4L, await CountAsync(s.TenantA, s.Coord, "fechamento_detalhes"));
    }

    [Fact]
    public async Task Fechamentos_require_permission_to_read()
    {
        var s = await RlsScenario.CreateAsync(db);
        await _seed.ExecAsync("insert into fechamentos (tenant_id, competencia) values (@a, date '2026-09-01')", new { a = s.TenantA });
        var noAccess = await _seed.UserAsync(s.TenantA, "Sem perfil");

        Assert.Equal(1L, await CountAsync(s.TenantA, s.Joao, "fechamentos"));
        Assert.Equal(0L, await CountAsync(s.TenantA, noAccess, "fechamentos"));
    }

    [Fact]
    public async Task Every_tenant_scoped_table_has_forced_rls()
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        var rows = (await conn.QueryAsync<(string RelName, bool RowSecurity, bool ForceRowSecurity)>(
            """
            select distinct c.relname, c.relrowsecurity, c.relforcerowsecurity
              from pg_class c
              join pg_attribute a on a.attrelid = c.oid
             where a.attname = 'tenant_id'
               and c.relkind = 'r'
               and c.relnamespace = 'public'::regnamespace
               and not a.attisdropped
            """)).ToList();

        Assert.NotEmpty(rows);
        foreach (var (relName, rowSecurity, forceRowSecurity) in rows)
        {
            Assert.True(rowSecurity, $"{relName} deveria ter row level security habilitado");
            Assert.True(forceRowSecurity, $"{relName} deveria forçar row level security");
        }
    }

    [Fact]
    public async Task Every_tenant_scoped_table_has_a_restrictive_tenant_isolation_policy()
    {
        // FORCE ROW SECURITY alone doesn't prove tenant isolation: both roles that could ever
        // bypass a policy (app_owner, app_superadmin) already have BYPASSRLS regardless of
        // FORCE. What actually guarantees isolation is a RESTRICTIVE policy covering every
        // command, which is ANDed with any permissive policy and can never be OR'd away.
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        var tables = (await conn.QueryAsync<string>(
            """
            select distinct c.relname
              from pg_class c
              join pg_attribute a on a.attrelid = c.oid
             where a.attname = 'tenant_id'
               and c.relkind = 'r'
               and c.relnamespace = 'public'::regnamespace
               and not a.attisdropped
            """)).ToList();

        Assert.NotEmpty(tables);
        foreach (var relName in tables)
        {
            var hasRestrictivePolicy = await conn.ExecuteScalarAsync<bool>(
                """
                select exists (
                  select 1 from pg_policies
                   where schemaname = 'public' and tablename = @relName
                     and permissive = 'RESTRICTIVE' and cmd = 'ALL'
                )
                """,
                new { relName });
            Assert.True(hasRestrictivePolicy, $"{relName} deveria ter uma policy restrictive de isolamento por tenant");
        }
    }

    [Fact]
    public async Task Update_cannot_move_a_row_into_another_tenant()
    {
        var s = await RlsScenario.CreateAsync(db);
        var roleId = await db.AsAppUserAsync(s.TenantA, s.Admin, (c, t) => c.ExecuteScalarAsync<Guid>(
            "insert into roles (tenant_id, name) values (@a, 'Papel móvel') returning id", new { a = s.TenantA }, t));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(s.TenantA, s.Admin, (c, t) => c.ExecuteAsync(
            "update roles set tenant_id = @b where id = @r", new { b = s.TenantB, r = roleId }, t)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task User_with_only_usuarios_convidar_can_update_users()
    {
        var s = await RlsScenario.CreateAsync(db);

        var inviterRole = await _seed.RoleAsync(s.TenantA, "Convite apenas", ("usuarios.convidar", null));
        var inviter = await _seed.UserAsync(s.TenantA, "Invitador");
        await _seed.AssignRoleAsync(s.TenantA, inviter, inviterRole);

        var affected = await db.AsAppUserAsync(s.TenantA, inviter, (c, t) => c.ExecuteAsync(
            "update users set name = 'Alterado pelo invitador' where id = @m", new { m = s.Maria }, t));
        Assert.Equal(1, affected);
    }
}
