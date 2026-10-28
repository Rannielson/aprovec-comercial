using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Integracoes;

public static class HinovaTarifaSync
{
    // Daily slices avoid Hinova's expensive monthly history query. Never infer zero
    // from a missing event. A durable cache also preserves fees registered after payment.
    public static async Task SyncAsync(Database db, IHinovaClient hinova, Guid tenant, Guid actor,
        string token, DateOnly inicio, DateOnly hoje, CancellationToken ct, Func<Task>? progress = null)
    {
        var ultimoDia = await db.InTenantAsync(tenant, actor, tx => tx.ExecuteScalarAsync<DateOnly?>(
            "select max(dia) from hinova_tarifa_sync_dias where tenant_id=@tenant", new { tenant }), ct);
        if (ultimoDia is { } ultimo && ultimo.AddDays(1) < inicio) inicio = ultimo.AddDays(1);
        var dias = (await db.InTenantAsync(tenant, actor, tx => tx.QueryAsync<DateOnly>(
            "select dia from hinova_tarifa_sync_dias where tenant_id=@tenant and dia>=@inicio",
            new { tenant, inicio }), ct)).ToHashSet();
        for (var dia = inicio; dia <= hoje; dia = dia.AddDays(1))
        {
            if (dia < hoje.AddDays(-5) && dias.Contains(dia)) continue;
            var consultados = 0;
            var vistos = new HashSet<long>();
            for (var pagina = 0; ; pagina++)
            {
                ct.ThrowIfCancellationRequested();
                var page = await hinova.ListarAlteracoesTarifaAsync(token, dia, pagina, 1000, ct);
                if (page.Alteracoes.Count == 0 && consultados < page.Total)
                    throw new HttpRequestException("Histórico de tarifas incompleto.");
                if (page.Alteracoes.Any(r => !vistos.Add(r.CodigoAlteracao) || DateOnly.FromDateTime(r.DataAlteracao.DateTime) != dia))
                    throw new HttpRequestException("Paginação do histórico de tarifas inconsistente.");
                await db.InTenantAsync(tenant, actor, async tx => {
                    foreach (var row in page.Alteracoes)
                        await tx.ExecuteAsync("""
                            insert into hinova_boleto_tarifas(tenant_id,nosso_numero,codigo_alteracao,data_alteracao,valor)
                            values(@tenant,@NossoNumero,@CodigoAlteracao,@DataAlteracao,@Valor)
                            on conflict(tenant_id,nosso_numero) do update
                            set codigo_alteracao=excluded.codigo_alteracao,data_alteracao=excluded.data_alteracao,
                                valor=excluded.valor,consultado_em=now()
                            where (excluded.data_alteracao,excluded.codigo_alteracao) >=
                                  (hinova_boleto_tarifas.data_alteracao,hinova_boleto_tarifas.codigo_alteracao)
                            """, new { tenant, row.NossoNumero, row.CodigoAlteracao, DataAlteracao = row.DataAlteracao.UtcDateTime, row.Valor });
                    return 0;
                }, ct);
                consultados += page.Alteracoes.Count;
                if (progress is not null) await progress();
                if (consultados >= page.Total) break;
            }
            await db.InTenantAsync(tenant, actor, tx => tx.ExecuteAsync("""
                insert into hinova_tarifa_sync_dias(tenant_id,dia) values(@tenant,@dia)
                on conflict(tenant_id,dia) do update set consultado_em=now()
                """, new { tenant, dia }), ct);
        }
        // Do not publish intermediate pages as a finished reconciliation. This also
        // catches already-imported payments whose fee arrived in a later bank file.
        // A newer report confirmation must survive an older initial zero in the API cache.
        await db.InTenantAsync(tenant, actor, tx => tx.ExecuteAsync("""
            update boletos b set tarifa_bancaria=t.valor,tarifa_confirmada_em=t.consultado_em,
                tarifa_referencia=(t.data_alteracao at time zone 'America/Sao_Paulo')::date,updated_at=now()
              from hinova_boleto_tarifas t
             where b.tenant_id=@tenant and t.tenant_id=b.tenant_id and t.nosso_numero=b.hinova_nosso_numero
               and (b.tarifa_referencia is null or
                    (t.data_alteracao at time zone 'America/Sao_Paulo')::date >= b.tarifa_referencia)
               and (b.tarifa_bancaria is distinct from t.valor or b.tarifa_confirmada_em is null
                    or b.tarifa_referencia is distinct from (t.data_alteracao at time zone 'America/Sao_Paulo')::date)
            """, new { tenant }), ct);
    }
}
