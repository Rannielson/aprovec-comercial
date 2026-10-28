using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Tests;
[Collection(ApiCollection.Name)]
public class FechamentoRevisionTests(ApiFixture api)
{
    private sealed record Entry(Guid BoletoId, decimal Base, decimal GrossBase, decimal BankFee);
    [Fact]
    public async Task Reopen_archives_confirmed_statement_and_recalculates_net_without_bypassing_final_payment_guards()
    {
        var s=await api.SeedAsync();var admin=api.Client(s.Slug);await admin.LoginAsync($"admin@{s.Slug}.local",ApiFixture.Password);
        var consultant=api.Client(s.Slug);await consultant.LoginAsync($"joao@{s.Slug}.local",ApiFixture.Password);
        var id=await api.SqlScalarAsync<Guid>("select id from boletos where tenant_id=@tenant and participante_id=@owner and status='recebido' and pago_em>='2026-09-01' and pago_em<'2026-10-01' limit 1",new{tenant=s.TenantId,owner=s.Joao});
        await api.SqlAsync("update boletos set hinova_nosso_numero=@numero,tarifa_bancaria=1.75,tarifa_referencia='2026-09-30' where id=@id",new{id,numero=Guid.NewGuid().ToString()});
        await ApiClient.ExpectAsync(await admin.PostAsync("/fechamentos/2026-09/confirm"),HttpStatusCode.OK);
        var frozen=(await admin.GetJsonAsync<List<Entry>>($"/commissions/2026-09/entries?beneficiaryId={s.Joao}")).Single(x=>x.BoletoId==id);
        Assert.Equal(1.75m,frozen.BankFee);Assert.Equal(frozen.GrossBase-1.75m,frozen.Base);
        await api.SqlAsync("update boletos set tarifa_bancaria=2 where id=@id",new{id});
        var old=(await admin.GetJsonAsync<List<Entry>>($"/commissions/2026-09/entries?beneficiaryId={s.Joao}")).Single(x=>x.BoletoId==id);
        Assert.Equal(frozen,old);
        var closing=await api.SqlScalarAsync<Guid>("select id from fechamentos where tenant_id=@tenant and competencia='2026-09-01'",new{tenant=s.TenantId});
        var count=await api.SqlScalarAsync<int>("select count(*)::int from fechamento_detalhes where fechamento_id=@closing",new{closing});
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>api.Service<Database>().InTenantAsync(s.TenantId,s.Admin,
            tx=>tx.ExecuteAsync("update fechamentos set status='apuracao' where id=@closing",new{closing}),CancellationToken.None));
        await ApiClient.ExpectAsync(await consultant.PostAsync("/fechamentos/2026-09/reopen",new{motivo="Corrigir tarifas bancarias"}),HttpStatusCode.Forbidden);
        await ApiClient.ExpectAsync(await admin.PostAsync("/fechamentos/2026-09/reopen",new{motivo="Recalculo liquido com tarifas bancarias confirmadas"}),HttpStatusCode.OK);
        Assert.Equal("apuracao",await api.SqlScalarAsync<string>("select status from fechamentos where id=@closing",new{closing}));
        Assert.Equal(count,await api.SqlScalarAsync<int>("select jsonb_array_length(detalhes) from fechamento_revisoes where fechamento_id=@closing",new{closing}));
        Assert.Equal("confirmado",await api.SqlScalarAsync<string>("select fechamento->>'status' from fechamento_revisoes where fechamento_id=@closing",new{closing}));
        Assert.Equal(0,await api.SqlScalarAsync<int>("select count(*)::int from fechamento_detalhes where fechamento_id=@closing",new{closing}));
        var updated=(await admin.GetJsonAsync<List<Entry>>($"/commissions/2026-09/entries?beneficiaryId={s.Joao}")).Single(x=>x.BoletoId==id);
        Assert.Equal(2m,updated.BankFee);Assert.Equal(updated.GrossBase-2m,updated.Base);
        await ApiClient.ExpectAsync(await admin.PostAsync("/fechamentos/2026-09/confirm"),HttpStatusCode.OK);
        await ApiClient.ExpectAsync(await admin.PostAsync("/fechamentos/2026-09/provision"),HttpStatusCode.OK);
        await ApiClient.ExpectAsync(await admin.PostAsync("/fechamentos/2026-09/reopen",new{motivo="Recalculo de pagamento provisionado"}),HttpStatusCode.Conflict);
        Assert.Equal(1,await api.SqlScalarAsync<int>("select count(*)::int from fechamento_revisoes where fechamento_id=@closing",new{closing}));
    }
}
