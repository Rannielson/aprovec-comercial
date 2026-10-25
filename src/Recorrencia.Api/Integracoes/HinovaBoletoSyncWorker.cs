using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Integracoes;

public sealed class HinovaBoletoSyncWorker(IServiceScopeFactory scopes, ILogger<HinovaBoletoSyncWorker> logger, IConfiguration configuration)
    : BackgroundService
{
    private sealed class Job
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid RequestedBy { get; set; }
        public DateOnly Competencia { get; set; }
        public DateOnly? PagamentoInicio { get; set; }
        public DateOnly? PagamentoFim { get; set; }
        public DateOnly? AgendadoPara { get; set; }
        public bool SomenteVencimentos { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Database>();
            await db.AsPlatformAsync(null, async tx =>
            {
                await tx.ExecuteAsync(
                    """
                    update hinova_boleto_sync_jobs
                       set status = 'queued', fase = 'na_fila', started_at = null
                     where status = 'running'
                    """);
                return 0;
            }, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Database>();
                // A durable unique key makes the daily schedule safe across restarts/replicas.
                var window = configuration.GetValue("Hinova:BoletoSyncAutomatico", true)
                    ? HinovaBoletoSyncSchedule.Window(DateTimeOffset.UtcNow) : null;
                if (window is { } w)
                    await db.AsPlatformAsync(null, async tx =>
                    {
                        await tx.ExecuteAsync(
                            """
                            insert into hinova_boleto_sync_jobs
                                (tenant_id, requested_by, competencia, pagamento_inicio, pagamento_fim, agendado_para)
                            select t.id, admin.id, @competencia, @inicio, @fim, @dia
                              from tenants t
                              join hinova_credenciais c on c.tenant_id = t.id
                              cross join lateral (
                                select u.id from users u
                                join user_roles ur on ur.tenant_id=u.tenant_id and ur.user_id=u.id
                                join roles r on r.tenant_id=ur.tenant_id and r.id=ur.role_id
                                where u.tenant_id=t.id and u.status='ativo'
                                  and r.source_template_key='administrador'
                                order by u.created_at limit 1
                              ) admin
                             where t.status='ativo'
                            on conflict do nothing
                            """, new { competencia = new DateOnly(w.Fim.Year, w.Fim.Month, 1),
                                inicio = w.Inicio, fim = w.Fim, dia = w.Fim });
                        return 0;
                    }, stoppingToken);
                var job = await db.AsPlatformAsync(null, async tx =>
                {
                    var candidate = await tx.QuerySingleOrDefaultAsync<Job>(
                        """
                        select id, tenant_id, requested_by, competencia, pagamento_inicio, pagamento_fim, agendado_para, somente_vencimentos
                          from hinova_boleto_sync_jobs
                         where status = 'queued'
                         order by requested_at for update skip locked limit 1
                        """);
                    if (candidate is not null)
                        await tx.ExecuteAsync(
                            "update hinova_boleto_sync_jobs set status = 'running', fase = 'iniciando', started_at = now() where id = @id",
                            new { id = candidate.Id });
                    return candidate;
                }, stoppingToken);

                if (job is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
                    continue;
                }

                try
                {
                    var hinova = scope.ServiceProvider.GetRequiredService<IHinovaClient>();
                    var cipher = scope.ServiceProvider.GetRequiredService<AesGcmCipher>();
                    var result = await HinovaEndpoints.ImportarBoletosCoreAsync(job.Competencia, job.TenantId,
                        job.RequestedBy, db, hinova, cipher, stoppingToken, async (progress, token) =>
                        {
                            await db.AsPlatformAsync(null, async tx =>
                            {
                                await tx.ExecuteAsync(
                                    """
                                    update hinova_boleto_sync_jobs
                                       set fase = @Fase, total_boletos = @TotalBoletos,
                                           pagamentos_consultados = @PagamentosConsultados,
                                           pagamentos_processados = @PagamentosProcessados,
                                           importados = @Importados, sem_vinculo = @SemVinculo,
                                           conflito_multiplo_vendedor = @ConflitoMultiploVendedor,
                                           nao_pago = @NaoPago, vencimento_retornados = @VencimentoRetornados,
                                           vencimentos_consultados = @VencimentosConsultados,
                                           vencimentos_processados = @VencimentosProcessados,
                                           vencimento_vinculados = @VencimentoVinculados
                                     where id = @id
                                    """, new { job.Id, progress.Fase, progress.TotalBoletos,
                                        progress.PagamentosConsultados, progress.PagamentosProcessados,
                                        progress.Importados, progress.SemVinculo, progress.ConflitoMultiploVendedor,
                                        progress.NaoPago, progress.VencimentoRetornados,
                                        progress.VencimentosConsultados, progress.VencimentosProcessados,
                                        progress.VencimentoVinculados });
                                return 0;
                            }, token);
                        }, incluirVencimentos: true, pagamentoInicio: job.PagamentoInicio,
                        pagamentoFim: job.PagamentoFim, ignorarImportados: job.AgendadoPara is not null, somenteVencimentos: job.SomenteVencimentos);
                    await db.AsPlatformAsync(null, async tx =>
                    {
                        await tx.ExecuteAsync(
                            """
                            update hinova_boleto_sync_jobs
                               set status = 'completed', fase = 'concluido', finished_at = now(), total_boletos = @TotalBoletos,
                                   importados = @Importados, sem_vinculo = @SemVinculo,
                                   conflito_multiplo_vendedor = @ConflitoMultiploVendedor,
                                   nao_pago = @NaoPago, vencimento_retornados = @VencimentoRetornados,
                                   vencimento_vinculados = @VencimentoVinculados, error_code = null
                             where id = @id
                            """, new { job.Id, result.TotalBoletos, result.Importados, result.SemVinculo,
                                result.ConflitoMultiploVendedor, result.NaoPago, result.VencimentoRetornados,
                                result.VencimentoVinculados });
                        return 0;
                    }, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    await db.AsPlatformAsync(null, async tx =>
                    {
                        await tx.ExecuteAsync(
                            "update hinova_boleto_sync_jobs set status = 'queued', fase = 'na_fila', started_at = null where id = @id",
                            new { job.Id });
                        return 0;
                    }, CancellationToken.None);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Falha na sincronização de boletos {JobId}", job.Id);
                    var code = ex is ApiProblem problem ? problem.Code : "internal.error";
                    await db.AsPlatformAsync(null, async tx =>
                    {
                        await tx.ExecuteAsync(
                            """
                            update hinova_boleto_sync_jobs
                               set status = 'failed', fase = 'falhou', finished_at = now(), error_code = @code
                             where id = @id
                            """, new { job.Id, code });
                        return 0;
                    }, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro ao processar a fila de sincronização de boletos");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
