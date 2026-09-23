namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class BoletoFechamentoTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private Task<Guid> FechamentoAsync(Guid tenant, string competencia = "2026-08-01") =>
        _seed.ScalarAsync<Guid>(
            "insert into fechamentos (tenant_id, competencia) values (@tenant, @competencia::date) returning id",
            new { tenant, competencia });

    [Fact]
    public async Task Paid_date_is_required_exactly_when_received()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");

        var missing = await DbExtensions.ThrowsPgAsync(() => _seed.BoletoAsync(t, u, 200m, "recebido", null));
        Assert.Equal(PostgresErrorCodes.CheckViolation, missing.SqlState);

        var unexpected = await DbExtensions.ThrowsPgAsync(() => _seed.BoletoAsync(t, u, 200m, "atraso", "2026-09-01"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, unexpected.SqlState);
    }

    [Fact]
    public async Task Paid_date_alone_does_not_trigger_the_check()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");

        // status = 'a_vencer' with pago_em null must pass (no payment yet).
        var naoPago = await _seed.BoletoAsync(t, u, 200m, "a_vencer", null);
        Assert.NotEqual(Guid.Empty, naoPago);

        // status = 'recebido' with pago_em set must pass (fully paid).
        var pago = await _seed.BoletoAsync(t, u, 200m, "recebido", "2026-09-01");
        Assert.NotEqual(Guid.Empty, pago);
    }

    [Fact]
    public async Task Boleto_owner_must_belong_to_the_tenant()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t1, "João");

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.BoletoAsync(t2, u, 200m, "recebido", "2026-09-01"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task Fechamento_starts_in_apuracao_and_competencia_is_a_month()
    {
        var t = await _seed.TenantAsync();

        var status = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "insert into fechamentos (tenant_id, competencia, status) values (@t, date '2026-08-01', 'conferencia')", new { t }));
        Assert.Equal("fechamento.invalid_initial_status", status.MessageText);

        var day = await DbExtensions.ThrowsPgAsync(() => FechamentoAsync(t, "2026-08-15"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, day.SqlState);
    }

    [Fact]
    public async Task Competencia_is_unique_per_tenant()
    {
        var t = await _seed.TenantAsync();
        await FechamentoAsync(t, "2026-08-01");

        var duplicate = await DbExtensions.ThrowsPgAsync(() => FechamentoAsync(t, "2026-08-01"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [Fact]
    public async Task Confirmation_requires_who_and_when_and_cannot_be_undone()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Admin");
        var f = await FechamentoAsync(t);

        var incomplete = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update fechamentos set status = 'confirmado' where id = @f", new { f }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, incomplete.SqlState);

        await _seed.ExecAsync(
            "update fechamentos set status = 'confirmado', confirmado_em = now(), confirmado_por = @u where id = @f", new { f, u });

        var back = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update fechamentos set status = 'apuracao' where id = @f", new { f }));
        Assert.Equal("fechamento.invalid_transition", back.MessageText);

        await _seed.ExecAsync(
            "update fechamentos set status = 'provisionado', provisionado_em = now(), provisionado_por = @u where id = @f", new { f, u });
    }

    [Fact]
    public async Task Provisioning_requires_who_and_when()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Admin");
        var f = await FechamentoAsync(t);

        await _seed.ExecAsync(
            "update fechamentos set status = 'confirmado', confirmado_em = now(), confirmado_por = @u where id = @f", new { f, u });

        var incomplete = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update fechamentos set status = 'provisionado' where id = @f", new { f }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, incomplete.SqlState);
    }

    [Fact]
    public async Task Competencia_cannot_change_after_creation()
    {
        var t = await _seed.TenantAsync();
        var f = await FechamentoAsync(t, "2026-08-01");

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update fechamentos set competencia = date '2026-09-01' where id = @f", new { f }));
        Assert.Equal("fechamento.immutable", ex.MessageText);
    }

    [Fact]
    public async Task Details_cannot_be_added_after_confirmation()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Admin");
        var boleto = await _seed.BoletoAsync(t, u, 200m, "recebido", "2026-08-05");
        var f = await FechamentoAsync(t);

        const string insertDetail = """
            insert into fechamento_detalhes
              (tenant_id, fechamento_id, beneficiario_id, origem_participante_id, boleto_id, rule_id, rule_type, level, group_id, rate, base, valor)
            values (@t, @f, @u, @u, @boleto, gen_random_uuid(), 'own', null, null, 0.07, 200, 14)
            """;
        await _seed.ExecAsync(insertDetail, new { t, f, u, boleto });

        await _seed.ExecAsync(
            "update fechamentos set status = 'confirmado', confirmado_em = now(), confirmado_por = @u where id = @f", new { f, u });

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(insertDetail, new { t, f, u, boleto }));
        Assert.Equal("fechamento.immutable", ex.MessageText);
    }
}
