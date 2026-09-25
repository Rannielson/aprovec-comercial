using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Carteira;

public static class CarteiraEndpoints
{
    public sealed record CarteiraItemResponse(Guid Id, string AssociadoNome, string? Placa, decimal Valor, string Status,
        DateOnly Vencimento, DateOnly? PagoEm, int? DiasAtraso, decimal Comissao);

    public sealed record CarteiraResponse(IReadOnlyList<CarteiraItemResponse> Items, int Page, int PageSize, int TotalCount,
        int TotalAllCount, IReadOnlyDictionary<string, int> StatusCounts, decimal TotalValor, decimal TotalComissao);

    private const int PageSize = 8;

    private const string CommissaoExpression = """
        case when b.status = 'recebido' then
          round(coalesce((
            select r.rate * b.valor from commission_rules r
             where r.type = 'own'
               and r.plan_id = (select p.id from commission_plans p
                                 where p.tenant_id = @tenant and p.status = 'ativo'
                                   and p.effective_from <= date_trunc('month', b.pago_em)::date
                                 order by p.effective_from desc limit 1)
          ), 0), 2)
        else 0 end
        """;

    public static void MapCarteiraEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/carteira", ListAsync).RequirePermission("carteira.visualizar");
    }

    private sealed class StatusCountRow
    {
        public string Status { get; set; } = "";
        public int Count { get; set; }
    }

    private sealed class ItemRow
    {
        public Guid Id { get; set; }
        public string AssociadoNome { get; set; } = "";
        public string? Placa { get; set; }
        public decimal Valor { get; set; }
        public string Status { get; set; } = "";
        public DateOnly Vencimento { get; set; }
        public DateOnly? PagoEm { get; set; }
        public int? DiasAtraso { get; set; }
        public decimal Comissao { get; set; }
    }

    private sealed class TotalsRow
    {
        public int TotalCount { get; set; }
        public decimal TotalValor { get; set; }
        public decimal TotalComissao { get; set; }
    }

    private static async Task<IResult> ListAsync(string? status, string? query, int? page, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        var currentPage = page is null or < 1 ? 1 : page.Value;
        currentPage = Math.Min(currentPage, 100_000);
        var statusFilter = status is null or "todos" ? null : status;
        var searchFilter = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        // Postgres ILIKE treats %, _ and \ as wildcard/escape characters; escape them so the
        // search behaves as a literal substring match (mirrors the mockup's .includes() intent).
        searchFilter = searchFilter?.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        var response = await db.InTenantAsync(tenant, user, async tx =>
        {
            var ownerIds = await tx.ExecuteScalarAsync<Guid[]>("select app.carteira_owner_ids()") ?? [];
            if (ownerIds.Length == 0)
                return new CarteiraResponse([], currentPage, PageSize, 0, 0, new Dictionary<string, int>(), 0, 0);

            var counts = (await tx.QueryAsync<StatusCountRow>(
                """
                select status, count(*)::int as count
                  from boletos b
                 where b.tenant_id = @tenant and b.participante_id = any(@ownerIds)
                 group by status
                """,
                new { tenant, ownerIds })).ToList();
            var statusCounts = counts.ToDictionary(c => c.Status, c => c.Count);
            var totalAllCount = counts.Sum(c => c.Count);

            var totals = await tx.QuerySingleAsync<TotalsRow>(
                $"""
                select count(*)::int as total_count, coalesce(sum(b.valor), 0) as total_valor,
                       coalesce(sum({CommissaoExpression}), 0) as total_comissao
                  from boletos b
                 where b.tenant_id = @tenant and b.participante_id = any(@ownerIds)
                   and (@statusFilter::text is null or b.status = @statusFilter)
                   and (@searchFilter::text is null or b.associado_nome ilike '%' || @searchFilter || '%' or b.placa ilike '%' || @searchFilter || '%')
                """,
                new { tenant, ownerIds, statusFilter, searchFilter });

            var items = await tx.QueryAsync<ItemRow>(
                $"""
                select b.id, b.associado_nome, b.placa, b.valor, b.status, b.vencimento, b.pago_em,
                       case when b.status = 'atraso' then ((now() at time zone 'America/Sao_Paulo')::date - b.vencimento)::int end as dias_atraso,
                       {CommissaoExpression} as comissao
                  from boletos b
                 where b.tenant_id = @tenant and b.participante_id = any(@ownerIds)
                   and (@statusFilter::text is null or b.status = @statusFilter)
                   and (@searchFilter::text is null or b.associado_nome ilike '%' || @searchFilter || '%' or b.placa ilike '%' || @searchFilter || '%')
                 order by coalesce(b.pago_em, b.vencimento) desc, b.id
                 limit @pageSize offset @offset
                """,
                new { tenant, ownerIds, statusFilter, searchFilter, pageSize = PageSize, offset = (currentPage - 1) * PageSize });

            return new CarteiraResponse(
                items.Select(i => new CarteiraItemResponse(i.Id, i.AssociadoNome, i.Placa, i.Valor, i.Status, i.Vencimento, i.PagoEm, i.DiasAtraso, i.Comissao)).ToList(),
                currentPage, PageSize, totals.TotalCount, totalAllCount, statusCounts, totals.TotalValor, totals.TotalComissao);
        }, ct);

        return Results.Ok(response);
    }
}
