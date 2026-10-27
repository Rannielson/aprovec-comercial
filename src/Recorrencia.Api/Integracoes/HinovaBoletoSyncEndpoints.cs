using Recorrencia.Api.Authorization;
using Recorrencia.Api.Commissions;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Integracoes;

public static class HinovaBoletoSyncEndpoints
{
    public sealed record StartRequest(string? Competencia, bool SomenteVencimentos = false, bool SomentePagamentos = false);
    public sealed class JobResponse
    {
        public Guid Id { get; set; }
        public DateOnly Competencia { get; set; }
        public string Status { get; set; } = "";
        public bool SomentePagamentos { get; set; }
        public string Fase { get; set; } = "";
        public DateTimeOffset RequestedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public int? TotalBoletos { get; set; }
        public int? Importados { get; set; }
        public int? SemVinculo { get; set; }
        public int? ConflitoMultiploVendedor { get; set; }
        public int? NaoPago { get; set; }
        public int? VencimentoRetornados { get; set; }
        public int? VencimentoVinculados { get; set; }
        public int PagamentosConsultados { get; set; }
        public int PagamentosProcessados { get; set; }
        public int VencimentosConsultados { get; set; }
        public int VencimentosProcessados { get; set; }
        public string? ErrorCode { get; set; }
    }

    public static void MapHinovaBoletoSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/integracoes/hinova/boletos/syncs", StartAsync).RequirePermission("integracoes.gerenciar");
        app.MapGet("/integracoes/hinova/boletos/syncs", ListAsync).RequirePermission("integracoes.gerenciar");
        app.MapGet("/integracoes/hinova/boletos/syncs/{id:guid}", GetAsync).RequirePermission("integracoes.gerenciar");
    }

    private static async Task<IResult> StartAsync(StartRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        if (body.SomenteVencimentos && body.SomentePagamentos)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "request.invalid");
        if (string.IsNullOrWhiteSpace(body.Competencia))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "commission.invalid_competencia");
        var competencia = CommissionService.ParseCompetencia(body.Competencia);
        var today = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "America/Sao_Paulo"));
        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        if (competencia > currentMonth)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.sync_future_month");

        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var job = await db.InTenantAsync(tenant, actor, async tx =>
        {
            if (!await tx.ExecuteScalarAsync<bool>(
                "select exists(select 1 from hinova_credenciais where tenant_id = @tenant)", new { tenant }))
                throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.nao_configurado");
            if (!await tx.ExecuteScalarAsync<bool>(
                "select exists(select 1 from hinova_voluntario_mapping where tenant_id = @tenant)", new { tenant }))
                throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.sem_mapeamentos");

            var id = await tx.ExecuteScalarAsync<Guid?>(
                """
                insert into hinova_boleto_sync_jobs (tenant_id, competencia, requested_by, somente_vencimentos, somente_pagamentos)
                values (@tenant, @competencia, @actor, @SomenteVencimentos, @SomentePagamentos)
                on conflict (tenant_id, competencia) where status in ('queued', 'running') do nothing
                returning id
                """, new { tenant, competencia, actor, body.SomenteVencimentos, body.SomentePagamentos });
            return await tx.QuerySingleAsync<JobResponse>(
                """
                select id, competencia, status, fase, somente_pagamentos, requested_at, started_at, finished_at, total_boletos,
                       importados, sem_vinculo, conflito_multiplo_vendedor, nao_pago,
                       vencimento_retornados, vencimento_vinculados, pagamentos_consultados,
                       pagamentos_processados, vencimentos_consultados, vencimentos_processados, error_code
                  from hinova_boleto_sync_jobs
                 where tenant_id = @tenant and
                       ((@id is not null and id = @id) or (@id is null and competencia = @competencia and status in ('queued', 'running')))
                 order by requested_at desc limit 1
                """, new { tenant, competencia, id });
        }, ct);
        return Results.Accepted($"/integracoes/hinova/boletos/syncs/{job.Id}", job);
    }

    private static async Task<IResult> ListAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var jobs = await db.InTenantAsync(tenant, actor, tx => tx.QueryAsync<JobResponse>(
            """
            select id, competencia, status, fase, somente_pagamentos, requested_at, started_at, finished_at, total_boletos,
                   importados, sem_vinculo, conflito_multiplo_vendedor, nao_pago,
                   vencimento_retornados, vencimento_vinculados, pagamentos_consultados,
                   pagamentos_processados, vencimentos_consultados, vencimentos_processados, error_code
              from hinova_boleto_sync_jobs
             where tenant_id = @tenant order by requested_at desc limit 12
            """, new { tenant }), ct);
        return Results.Ok(jobs);
    }

    private static async Task<IResult> GetAsync(Guid id, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var job = await db.InTenantAsync(tenant, actor, tx => tx.QuerySingleOrDefaultAsync<JobResponse>(
            """
            select id, competencia, status, fase, somente_pagamentos, requested_at, started_at, finished_at, total_boletos,
                   importados, sem_vinculo, conflito_multiplo_vendedor, nao_pago,
                   vencimento_retornados, vencimento_vinculados, pagamentos_consultados,
                   pagamentos_processados, vencimentos_consultados, vencimentos_processados, error_code
              from hinova_boleto_sync_jobs where tenant_id = @tenant and id = @id
            """, new { tenant, id }), ct);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }
}
