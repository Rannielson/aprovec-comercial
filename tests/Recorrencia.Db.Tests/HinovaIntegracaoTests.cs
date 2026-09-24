namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class HinovaIntegracaoTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    // provision_tenant leaves the admin as status = 'convidado' (no password set yet) —
    // app.effective_permissions() only considers users with status = 'ativo', so every
    // permission check for a freshly-provisioned admin fails until activated. Same fix
    // RlsScenario.CreateAsync already applies for the same reason.
    private Task ActivateAsync(Guid userId) =>
        _seed.ExecAsync("update users set status = 'ativo', password_hash = 'hash-de-teste' where id = @userId", new { userId });

    [Fact]
    public async Task Administrador_can_write_and_read_credenciais()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);

        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_credenciais (tenant_id, usuario_enc, senha_enc, token_sga_enc, updated_by)
            values (@tenant, @bytes, @bytes, @bytes, @admin)
            """,
            new { tenant, bytes = new byte[] { 1, 2, 3 }, admin }, tx));

        var found = await db.AsAppUserAsync(tenant, admin, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from hinova_credenciais where tenant_id = @tenant", new { tenant }, tx));
        Assert.Equal(1, found);
    }

    [Fact]
    public async Task User_without_the_permission_cannot_write_credenciais()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "Sem integrações");
        await _seed.AssignRoleAsync(t, u, await _seed.RoleAsync(t, "Sem integrações", ("usuarios.convidar", null)));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t, u, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_credenciais (tenant_id, usuario_enc, senha_enc, token_sga_enc, updated_by)
            values (@t, @bytes, @bytes, @bytes, @u)
            """,
            new { t, bytes = new byte[] { 1 }, u }, tx)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task User_without_the_permission_sees_no_rows_on_select()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_credenciais (tenant_id, usuario_enc, senha_enc, token_sga_enc, updated_by)
            values (@tenant, @bytes, @bytes, @bytes, @admin)
            """,
            new { tenant, bytes = new byte[] { 1 }, admin }, tx));

        var u = await _seed.UserAsync(tenant, "Sem integrações");
        await _seed.AssignRoleAsync(tenant, u, await _seed.RoleAsync(tenant, "Sem integrações", ("usuarios.convidar", null)));

        var count = await db.AsAppUserAsync(tenant, u, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from hinova_credenciais where tenant_id = @tenant", new { tenant }, tx));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Tenant_isolation_hides_another_tenants_credenciais()
    {
        var (tenantA, adminA) = await _seed.ProvisionAsync();
        await ActivateAsync(adminA);
        var (tenantB, adminB) = await _seed.ProvisionAsync();
        await ActivateAsync(adminB);
        await db.AsAppUserAsync(tenantB, adminB, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_credenciais (tenant_id, usuario_enc, senha_enc, token_sga_enc, updated_by)
            values (@tenantB, @bytes, @bytes, @bytes, @adminB)
            """,
            new { tenantB, bytes = new byte[] { 1 }, adminB }, tx));

        var count = await db.AsAppUserAsync(tenantA, adminA, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from hinova_credenciais where tenant_id = @tenantB", new { tenantB }, tx));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task A_user_cannot_be_mapped_to_two_voluntarios()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        var vendedor = await _seed.UserAsync(tenant, "Vendedor");

        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
            values (@tenant, @vendedor, '101', 'Nome Hinova', '11111111111', @admin)
            """,
            new { tenant, vendedor, admin }, tx));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
            values (@tenant, @vendedor, '102', 'Outro Nome', '22222222222', @admin)
            """,
            new { tenant, vendedor, admin }, tx)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task A_codigo_voluntario_cannot_be_mapped_to_two_users()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        var vendedorA = await _seed.UserAsync(tenant, "Vendedor A");
        var vendedorB = await _seed.UserAsync(tenant, "Vendedor B");

        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
            values (@tenant, @vendedorA, '101', 'Nome Hinova', '11111111111', @admin)
            """,
            new { tenant, vendedorA, admin }, tx));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
            values (@tenant, @vendedorB, '101', 'Nome Hinova', '11111111111', @admin)
            """,
            new { tenant, vendedorB, admin }, tx)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task Backfill_statements_are_idempotent_and_grant_the_permission()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);

        // Simulates re-running 0014's backfill against a tenant that already has the row
        // (provision_tenant already granted it, since this tenant was created after 0014).
        // The point is proving `on conflict do nothing` doesn't error the second time.
        await _seed.ExecAsync(
            """
            insert into tenant_modules (tenant_id, module_key) select id, 'integracoes' from tenants on conflict do nothing;
            insert into role_permissions (role_id, tenant_id, permission_key, scope)
            select r.id, r.tenant_id, 'integracoes.gerenciar', null from roles r where r.source_template_key = 'administrador'
            on conflict do nothing;
            """);

        var has = await db.AsAppUserAsync(tenant, admin, (c, tx) =>
            c.ExecuteScalarAsync<bool>("select app.has_permission('integracoes.gerenciar')", null, tx));
        Assert.True(has);
    }
}
