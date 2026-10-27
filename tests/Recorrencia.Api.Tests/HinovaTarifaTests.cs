using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class HinovaTarifaTests(ApiFixture api)
{
    private sealed record Pending(int Count);
    private sealed record Wallet(List<WalletItem> Items, int TotalCount, int TotalAllCount);
    private sealed record WalletItem(Guid Id, decimal Valor, decimal Comissao, decimal? TarifaBancaria, decimal? ValorLiquido, bool TarifaPendente);
    private sealed record Entry(Guid BoletoId, string RuleType, decimal Base, decimal Amount);

    [Fact]
    public async Task Paginated_fee_sync_resumes_after_quota_without_publishing_partial_fees()
    {
        var s=await api.SeedAsync();var number=Guid.NewGuid().ToString();var day=new DateOnly(2026,10,1);
        var id=Guid.NewGuid();
        await api.SqlAsync("""
            insert into boletos(id,tenant_id,participante_id,associado_ref,associado_nome,valor,status,vencimento,pago_em,hinova_nosso_numero,tarifa_bancaria,tarifa_referencia)
            values(@id,@tenant,@owner,'retry','Teste',100,'recebido','2026-09-01','2026-09-30',@number,1.75,'2026-09-30')
            """,new{id,tenant=s.TenantId,owner=s.Joao,number});
        var fail=true;var pages=new List<int>();
        api.Hinova.TarifaResponse=(date,page,size)=> {
            Assert.Equal(1000,size);pages.Add(page);
            if(page==1 && fail)throw new HinovaAuthException(HttpStatusCode.Forbidden,extractionLimit:true);
            var rows=page==0?Enumerable.Range(0,1000).Select(i=>new HinovaTarifaAlteracao(i==0?number:number+i,i+100,
                new DateTimeOffset(2026,10,1,0,0,0,TimeSpan.Zero),2m)).ToList():[new HinovaTarifaAlteracao(number+"last",2000,new DateTimeOffset(2026,10,1,0,0,0,TimeSpan.Zero),0m)];
            return new HinovaTarifaPagina(1001,rows);
        };
        try {
            async Task Run()=>await HinovaTarifaSync.SyncAsync(api.Service<Database>(),api.Hinova,s.TenantId,s.Admin,"fake",day,day,CancellationToken.None);
            await Assert.ThrowsAsync<HinovaAuthException>(Run);
            Assert.Equal(0,await api.SqlScalarAsync<int>("select count(*)::int from hinova_tarifa_sync_dias where tenant_id=@tenant",new{tenant=s.TenantId}));
            Assert.Equal(1.75m,await api.SqlScalarAsync<decimal>("select tarifa_bancaria from boletos where id=@id",new{id}));
            fail=false;await Run();
            Assert.Equal(new[]{0,1,0,1},pages);
            Assert.Equal(2m,await api.SqlScalarAsync<decimal>("select tarifa_bancaria from boletos where id=@id",new{id}));
            Assert.Equal(1,await api.SqlScalarAsync<int>("select count(*)::int from hinova_tarifa_sync_dias where tenant_id=@tenant",new{tenant=s.TenantId}));
        }finally{api.Hinova.TarifaResponse=null;}
    }

    [Fact]
    public async Task Net_commission_preserves_gross_pays_all_levels_and_flags_unknown_without_leaking_other_tenants()
    {
        var s=await api.SeedAsync();var other=await api.SeedAsync();
        var admin=api.Client(s.Slug);await admin.LoginAsync($"admin@{s.Slug}.local",ApiFixture.Password);
        var planResponse=await admin.PostAsync("/commission-plans",new {name="721 líquido",effectiveFrom="2026-09-01",rules=new object[]{new{type="own",rate=.07m},new{type="upline",rate=.02m,level=1},new{type="upline",rate=.01m,level=2}}});
        await ApiClient.ExpectAsync(planResponse,HttpStatusCode.Created);
        using var planJson=System.Text.Json.JsonDocument.Parse(await planResponse.Content.ReadAsStringAsync());
        var planId=planJson.RootElement.GetProperty("id").GetGuid();
        await ApiClient.ExpectAsync(await admin.PostAsync($"/commission-plans/{planId}/activate"),HttpStatusCode.OK);
        var consultor=api.Client(s.Slug);await consultor.LoginAsync($"joao@{s.Slug}.local",ApiFixture.Password);
        var id=Guid.NewGuid();var unknown=Guid.NewGuid();var zero=Guid.NewGuid();var invalid=Guid.NewGuid();
        var prefix=Guid.NewGuid().ToString();
        foreach(var b in new[]{(id,"paid",s.Pedro),(unknown,"unknown",s.Joao),(zero,"zero",s.Joao),(invalid,"invalid",s.Joao)})
            await api.SqlAsync("""
                insert into boletos(id,tenant_id,participante_id,associado_ref,associado_nome,valor,status,vencimento,pago_em,hinova_nosso_numero)
                values(@id,@tenant,@owner,'fee-test','Associado',100,'recebido','2026-09-01','2026-09-30',@number)
                """,new{id=b.Item1,tenant=s.TenantId,owner=b.Item3,number=prefix+b.Item2});
        api.Hinova.Tarifas.AddRange([
            new(prefix+"paid",20,new DateTimeOffset(2026,10,1,0,0,0,TimeSpan.FromHours(-3)),1.75m),
            new(prefix+"zero",21,new DateTimeOffset(2026,10,1,0,0,0,TimeSpan.FromHours(-3)),0m),
            new(prefix+"invalid",22,new DateTimeOffset(2026,10,1,0,0,0,TimeSpan.FromHours(-3)),101m)
        ]);
        try
        {
            await HinovaTarifaSync.SyncAsync(api.Service<Database>(),api.Hinova,s.TenantId,s.Admin,"fake",new(2026,10,1),new(2026,10,1),CancellationToken.None);
            Assert.Equal(100m,await api.SqlScalarAsync<decimal>("select valor from boletos where id=@id",new{id}));
            Assert.Equal(98.25m,await api.SqlScalarAsync<decimal>("select comissao_base from boletos where id=@id",new{id}));
            var pedro=await admin.GetJsonAsync<List<Entry>>($"/commissions/2026-09/entries?beneficiaryId={s.Pedro}");
            Assert.Contains(pedro,e=>e.BoletoId==id&&e.Base==98.25m&&e.Amount==6.88m);
            var maria=await admin.GetJsonAsync<List<Entry>>($"/commissions/2026-09/entries?beneficiaryId={s.Maria}");
            Assert.Contains(maria,e=>e.BoletoId==id&&e.RuleType=="upline"&&e.Amount==1.96m);
            var joao=await consultor.GetJsonAsync<List<Entry>>($"/commissions/2026-09/entries?beneficiaryId={s.Joao}");
            Assert.Contains(joao,e=>e.BoletoId==id&&e.RuleType=="upline"&&e.Amount==0.98m);
            Assert.DoesNotContain(joao,e=>e.BoletoId==unknown||e.BoletoId==invalid);
            Assert.Contains(joao,e=>e.BoletoId==zero&&e.Amount==7m);
            Assert.Equal(2,(await admin.GetJsonAsync<Pending>("/commissions/2026-09/pending-fees")).Count);
            var wallet=await admin.GetJsonAsync<Wallet>("/carteira?status=recebido&tarifas=pendentes&competencia=2026-09");
            Assert.Equal(2,wallet.Items.Count);Assert.All(wallet.Items,i=>Assert.True(i.TarifaPendente));
            Assert.Empty((await consultor.GetJsonAsync<Wallet>("/carteira?status=recebido&tarifas=pendentes&competencia=2026-10")).Items);
            Assert.DoesNotContain((await admin.GetJsonAsync<Wallet>("/carteira?status=recebido&competencia=2026-09")).Items,b=>b.Id==unknown||b.Id==invalid);
            var visible=await consultor.GetJsonAsync<Wallet>("/carteira?status=recebido&competencia=2026-09");
            Assert.DoesNotContain(visible.Items,b=>b.Id==unknown||b.Id==invalid);
            Assert.All(visible.Items,b=>Assert.False(b.TarifaPendente));
            Assert.Empty((await consultor.GetJsonAsync<Wallet>("/carteira?status=recebido&tarifas=pendentes&competencia=2026-09")).Items);
            await ApiClient.ExpectAsync(await consultor.GetAsync("/commissions/2026-09/pending-fees"),HttpStatusCode.Forbidden);
            var close=await admin.PostAsync("/fechamentos/2026-09/confirm");
            Assert.Equal("fechamento.tarifas_pendentes",await ApiClient.CodeAsync(close));
            Assert.Equal(0,await api.Service<Database>().InTenantAsync(other.TenantId,other.Admin,tx=>tx.ExecuteScalarAsync<int>(
                "select count(*)::int from hinova_boleto_tarifas where tenant_id=@tenant",new{tenant=s.TenantId}),CancellationToken.None));
            // Out-of-order/replayed histories never replace a more recent confirmed tariff.
            api.Hinova.Tarifas.Add(new(prefix+"paid",19,new DateTimeOffset(2026,10,1,0,0,0,TimeSpan.FromHours(-3)),9m));
            await HinovaTarifaSync.SyncAsync(api.Service<Database>(),api.Hinova,s.TenantId,s.Admin,"fake",new(2026,10,1),new(2026,10,1),CancellationToken.None);
            Assert.Equal(1.75m,await api.SqlScalarAsync<decimal>("select tarifa_bancaria from boletos where id=@id",new{id}));
            api.Hinova.Tarifas.Add(new(prefix+"unknown",30,new DateTimeOffset(2026,10,2,0,0,0,TimeSpan.FromHours(-3)),2m));
            await HinovaTarifaSync.SyncAsync(api.Service<Database>(),api.Hinova,s.TenantId,s.Admin,"fake",new(2026,10,2),new(2026,10,2),CancellationToken.None);
            var updated=await consultor.GetJsonAsync<Wallet>("/carteira?status=recebido&competencia=2026-09");
            Assert.Equal(visible.TotalCount+1,updated.TotalCount);
            Assert.Equal(visible.TotalAllCount+1,updated.TotalAllCount);
            Assert.Contains(updated.Items,b=>b.Id==unknown && !b.TarifaPendente && b.ValorLiquido==98m && b.Comissao==6.86m);
        }
        finally{api.Hinova.Tarifas.Clear();}
    }
}
