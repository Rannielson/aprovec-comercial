namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class ConvitesIndicacaoTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private Task ActivateAsync(Guid userId) =>
        _seed.ExecAsync("update users set status = 'ativo', password_hash = 'hash-de-teste' where id = @userId", new { userId });

    [Fact]
    public async Task Anyone_can_insert_a_solicitacao_without_being_a_real_user()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        var indicador = await _seed.UserAsync(tenant, "Indicador");
        var id = Guid.NewGuid();

        // "Sem usuário" = a mesma chamada que o backend faz via db.InTenantAsync(tenant, null, ...)
        // para o fluxo público: tenant setado, app.current_user_id() nulo. O id é gerado aqui em
        // C# e inserido explicitamente -- nunca via "returning", que sob RLS é filtrado pelas
        // policies de SELECT da tabela: uma sessão anônima sem nenhuma policy de select anônimo
        // não veria a linha de volta (RETURNING de zero linhas visíveis, não um erro).
        await db.AsAppUserAsync(tenant, null, (c, tx) => c.ExecuteAsync(
            """
            insert into solicitacoes_cadastro
              (id, tenant_id, indicador_user_id, nome, cpf, celular, email, cep, logradouro, numero, bairro, cidade, estado)
            values (@id, @tenant, @indicador, 'Fulano', '11111111111', '11999999999', 'fulano@teste.local',
                    '30000000', 'Rua X', '1', 'Bairro', 'Cidade', 'UF')
            """,
            new { id, tenant, indicador }, tx));

        // Confirma que a linha existe de verdade lendo como admin (que já tem select legítimo
        // via solicitacoes_cadastro_select_admin) -- não como o próprio anônimo: o fluxo público
        // nunca precisa ler solicitacoes_cadastro de volta, então a tabela não tem (nem deveria
        // ter) uma policy de select anônimo, que exporia CPF/endereço/e-mail de todo mundo no tenant.
        var count = await db.AsAppUserAsync(tenant, admin, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from solicitacoes_cadastro where id = @id", new { id }, tx));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task User_without_usuarios_convidar_cannot_see_pending_solicitacoes()
    {
        var tenant = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(tenant);
        var indicador = await _seed.UserAsync(tenant, "Indicador");
        await db.AsAppUserAsync(tenant, null, (c, tx) => c.ExecuteAsync(
            """
            insert into solicitacoes_cadastro
              (tenant_id, indicador_user_id, nome, cpf, celular, email, cep, logradouro, numero, bairro, cidade, estado)
            values (@tenant, @indicador, 'Fulano', '11111111111', '11999999999', 'fulano@teste.local',
                    '30000000', 'Rua X', '1', 'Bairro', 'Cidade', 'UF')
            """,
            new { tenant, indicador }, tx));

        var semPermissao = await _seed.UserAsync(tenant, "Sem convidar");
        await _seed.AssignRoleAsync(tenant, semPermissao, await _seed.RoleAsync(tenant, "Só estrutura", ("estrutura.visualizar", "tenant")));

        var count = await db.AsAppUserAsync(tenant, semPermissao, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from solicitacoes_cadastro where tenant_id = @tenant", new { tenant }, tx));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Admin_with_both_permissions_can_see_and_update_solicitacoes()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        var indicador = await _seed.UserAsync(tenant, "Indicador");
        var id = Guid.NewGuid();
        await db.AsAppUserAsync(tenant, null, (c, tx) => c.ExecuteAsync(
            """
            insert into solicitacoes_cadastro
              (id, tenant_id, indicador_user_id, nome, cpf, celular, email, cep, logradouro, numero, bairro, cidade, estado)
            values (@id, @tenant, @indicador, 'Fulano', '11111111111', '11999999999', 'fulano@teste.local',
                    '30000000', 'Rua X', '1', 'Bairro', 'Cidade', 'UF')
            """,
            new { id, tenant, indicador }, tx));

        var count = await db.AsAppUserAsync(tenant, admin, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from solicitacoes_cadastro where tenant_id = @tenant", new { tenant }, tx));
        Assert.Equal(1, count);

        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            "update solicitacoes_cadastro set status = 'rejeitado', resolvido_em = now(), resolvido_por = @admin where id = @id",
            new { admin, id }, tx));
    }

    [Fact]
    public async Task Approving_without_a_resultado_user_id_is_rejected_by_the_check_constraint()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        var indicador = await _seed.UserAsync(tenant, "Indicador");
        var id = Guid.NewGuid();
        await db.AsAppUserAsync(tenant, null, (c, tx) => c.ExecuteAsync(
            """
            insert into solicitacoes_cadastro
              (id, tenant_id, indicador_user_id, nome, cpf, celular, email, cep, logradouro, numero, bairro, cidade, estado)
            values (@id, @tenant, @indicador, 'Fulano', '11111111111', '11999999999', 'fulano@teste.local',
                    '30000000', 'Rua X', '1', 'Bairro', 'Cidade', 'UF')
            """,
            new { id, tenant, indicador }, tx));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            "update solicitacoes_cadastro set status = 'aprovado', resolvido_em = now(), resolvido_por = @admin where id = @id",
            new { admin, id }, tx)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task A_user_can_create_and_read_their_own_convite_link()
    {
        var tenant = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(tenant);
        var user = await _seed.UserAsync(tenant, "Consultor");

        await db.AsAppUserAsync(tenant, user, (c, tx) => c.ExecuteAsync(
            "insert into convite_links (tenant_id, user_id, token) values (@tenant, @user, 'token-de-teste')",
            new { tenant, user }, tx));

        var token = await db.AsAppUserAsync(tenant, user, (c, tx) =>
            c.ExecuteScalarAsync<string>("select token from convite_links where tenant_id = @tenant and user_id = @user", new { tenant, user }, tx));
        Assert.Equal("token-de-teste", token);
    }

    [Fact]
    public async Task Anyone_can_resolve_a_convite_link_by_token_without_being_a_real_user()
    {
        var tenant = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(tenant);
        var user = await _seed.UserAsync(tenant, "Consultor");
        await db.AsAppUserAsync(tenant, user, (c, tx) => c.ExecuteAsync(
            "insert into convite_links (tenant_id, user_id, token) values (@tenant, @user, 'token-publico')",
            new { tenant, user }, tx));

        var found = await db.AsAppUserAsync(tenant, null, (c, tx) =>
            c.ExecuteScalarAsync<Guid?>("select user_id from convite_links where tenant_id = @tenant and token = 'token-publico'", new { tenant }, tx));
        Assert.Equal(user, found);
    }

    [Fact]
    public async Task A_user_cannot_insert_a_convite_link_for_someone_else()
    {
        var tenant = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(tenant);
        var user = await _seed.UserAsync(tenant, "Consultor");
        var outro = await _seed.UserAsync(tenant, "Outro");

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(tenant, user, (c, tx) => c.ExecuteAsync(
            "insert into convite_links (tenant_id, user_id, token) values (@tenant, @outro, 'token-indevido')",
            new { tenant, outro }, tx)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Tenant_isolation_hides_another_tenants_solicitacoes()
    {
        var (tenantA, adminA) = await _seed.ProvisionAsync();
        await ActivateAsync(adminA);
        var (tenantB, adminB) = await _seed.ProvisionAsync();
        await ActivateAsync(adminB);
        var indicadorB = await _seed.UserAsync(tenantB, "Indicador B");
        await db.AsAppUserAsync(tenantB, null, (c, tx) => c.ExecuteAsync(
            """
            insert into solicitacoes_cadastro
              (tenant_id, indicador_user_id, nome, cpf, celular, email, cep, logradouro, numero, bairro, cidade, estado)
            values (@tenantB, @indicadorB, 'Fulano', '11111111111', '11999999999', 'fulano@teste.local',
                    '30000000', 'Rua X', '1', 'Bairro', 'Cidade', 'UF')
            """,
            new { tenantB, indicadorB }, tx));

        var count = await db.AsAppUserAsync(tenantA, adminA, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from solicitacoes_cadastro where tenant_id = @tenantB", new { tenantB }, tx));
        Assert.Equal(0, count);
    }
}
