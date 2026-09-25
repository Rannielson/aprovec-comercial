namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class PlanosCarreiraTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private Task ActivateAsync(Guid userId) =>
        _seed.ExecAsync("update users set status = 'ativo', password_hash = 'hash-de-teste' where id = @userId", new { userId });

    [Fact]
    public async Task Administrador_can_create_a_plano_with_faixas_and_regras()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);

        var planoId = Guid.NewGuid();
        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into planos_carreira (id, tenant_id, name, classificacao, janela_apuracao_dias, recorrencia_ativa)
            values (@planoId, @tenant, 'Consultor CLT Externo', 'clt_externo', 30, true)
            """,
            new { planoId, tenant }, tx));
        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into planos_carreira_faixas_bonus (id, tenant_id, plano_carreira_id, quantidade_min, quantidade_max, valor_por_placa)
            values (@id, @tenant, @planoId, 1, 14, 33)
            """,
            new { id = Guid.NewGuid(), tenant, planoId }, tx));
        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into planos_carreira_regras_recorrencia (id, tenant_id, plano_carreira_id, tipo, nivel, taxa)
            values (@id, @tenant, @planoId, 'propria', null, 0.07)
            """,
            new { id = Guid.NewGuid(), tenant, planoId }, tx));

        var count = await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteScalarAsync<int>(
            "select count(*) from planos_carreira_faixas_bonus where plano_carreira_id = @planoId", new { planoId }, tx));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task User_without_regras_comissao_editar_cannot_create_a_plano()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "Sem permissão");
        var role = await _seed.RoleAsync(t, "Sem regras", ("estrutura.visualizar", "tenant"));
        await _seed.AssignRoleAsync(t, u, role);

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t, u, (c, tx) => c.ExecuteAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao) values (@id, @t, 'X', 'clt_interno')",
            new { id = Guid.NewGuid(), t }, tx)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Setting_plano_carreira_without_estrutura_editar_is_rejected()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var admin = await _seed.UserAsync(t, "Admin", status: "ativo");
        var alvo = await _seed.UserAsync(t, "Alvo");
        var planoId = Guid.NewGuid();
        await _seed.ExecAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao) values (@planoId, @t, 'Plano', 'clt_interno')",
            new { planoId, t });
        // estrutura.visualizar/'tenant' é necessária pra ESTE ator conseguir ver a linha do
        // alvo (users_select) -- sem ela, o UPDATE afeta 0 linhas silenciosamente e o trigger
        // de plano_carreira_id nunca chega a rodar, o que pareceria (erradamente) uma rejeição
        // "correta" por um motivo totalmente diferente do que este teste quer provar.
        var role = await _seed.RoleAsync(t, "Sem estrutura editar", ("usuarios.convidar", null), ("estrutura.visualizar", "tenant"));
        await _seed.AssignRoleAsync(t, admin, role);

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t, admin, (c, tx) => c.ExecuteAsync(
            "update users set plano_carreira_id = @planoId where id = @alvo", new { planoId, alvo }, tx)));
        Assert.Equal("auth.forbidden", ex.MessageText);
    }

    [Fact]
    public async Task Setting_plano_carreira_with_estrutura_editar_succeeds()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var admin = await _seed.UserAsync(t, "Admin", status: "ativo");
        var alvo = await _seed.UserAsync(t, "Alvo");
        var planoId = Guid.NewGuid();
        await _seed.ExecAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao) values (@planoId, @t, 'Plano', 'clt_interno')",
            new { planoId, t });
        var role = await _seed.RoleAsync(t, "Com estrutura editar", ("estrutura.editar", null), ("estrutura.visualizar", "tenant"));
        await _seed.AssignRoleAsync(t, admin, role);

        await db.AsAppUserAsync(t, admin, (c, tx) => c.ExecuteAsync(
            "update users set plano_carreira_id = @planoId where id = @alvo", new { planoId, alvo }, tx));

        var result = await _seed.ScalarAsync<Guid?>("select plano_carreira_id from users where id = @alvo", new { alvo });
        Assert.Equal(planoId, result);
    }

    [Fact]
    public async Task Meta_minima_requires_both_fields_together()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao, meta_minima_contratos) values (@id, @tenant, 'X', 'clt_interno', 10)",
            new { id = Guid.NewGuid(), tenant }, tx)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Regra_upline_requires_a_nivel()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        var planoId = Guid.NewGuid();
        await _seed.ExecAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao) values (@planoId, @tenant, 'Plano', 'clt_interno')",
            new { planoId, tenant });

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            "insert into planos_carreira_regras_recorrencia (id, tenant_id, plano_carreira_id, tipo, nivel, taxa) values (@id, @tenant, @planoId, 'upline', null, 0.02)",
            new { id = Guid.NewGuid(), tenant, planoId }, tx)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Regra_recorrencia_cannot_duplicate_tipo_e_nivel()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        var planoId = Guid.NewGuid();
        await _seed.ExecAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao) values (@planoId, @tenant, 'Plano', 'clt_interno')",
            new { planoId, tenant });
        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            "insert into planos_carreira_regras_recorrencia (id, tenant_id, plano_carreira_id, tipo, nivel, taxa) values (@id, @tenant, @planoId, 'propria', null, 0.07)",
            new { id = Guid.NewGuid(), tenant, planoId }, tx));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            "insert into planos_carreira_regras_recorrencia (id, tenant_id, plano_carreira_id, tipo, nivel, taxa) values (@id, @tenant, @planoId, 'propria', null, 0.05)",
            new { id = Guid.NewGuid(), tenant, planoId }, tx)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task Assigning_a_plano_from_another_tenant_is_rejected()
    {
        var (tenant1, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        var alvo = await _seed.UserAsync(tenant1, "Alvo");
        var tenant2 = await _seed.TenantAsync();
        var planoOutroTenant = Guid.NewGuid();
        await _seed.ExecAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao) values (@id, @tenant2, 'Outro', 'clt_interno')",
            new { id = planoOutroTenant, tenant2 });

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(tenant1, admin, (c, tx) => c.ExecuteAsync(
            "update users set plano_carreira_id = @planoOutroTenant where id = @alvo", new { planoOutroTenant, alvo }, tx)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }
}
