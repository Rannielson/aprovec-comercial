using Recorrencia.Api.Commissions;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Integracoes;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Carteira;

public static class CarteiraEndpoints
{
    public sealed record CarteiraItemResponse(Guid Id, string AssociadoNome, string? Placa, decimal Valor, string Status,
        DateOnly Vencimento, DateOnly? PagoEm, int? DiasAtraso, decimal Comissao, decimal? TarifaBancaria, decimal? ValorLiquido, bool TarifaPendente);

    public sealed record CarteiraResponse(IReadOnlyList<CarteiraItemResponse> Items, int Page, int PageSize, int TotalCount,
        int TotalAllCount, IReadOnlyDictionary<string, int> StatusCounts, decimal TotalValor, decimal TotalComissao);

    private const int PageSize = 8;

    private const string CommissaoExpression = """
        case when b.status = 'recebido' and b.commission_hold_reason is null and b.comissao_base is not null then
          app.round_commission(coalesce((
            select r.rate * b.comissao_base from commission_rules r
             where r.type = 'own'
               and r.plan_id = (select p.id from commission_plans p
                                 where p.tenant_id = @tenant and p.status = 'ativo'
                                   and p.effective_from <= date_trunc('month', b.pago_em)::date
                                 order by p.effective_from desc limit 1)
          ), 0))
        else 0 end
        """;

    public static void MapCarteiraEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/carteira", ListAsync).RequirePermission("carteira.visualizar");
        app.MapGet("/carteira/{id:guid}/whatsapp", WhatsappAsync).RequirePermission("carteira.visualizar");
    }

    private sealed class HinovaCredenciaisRow
    {
        public byte[] UsuarioEnc { get; set; } = [];
        public byte[] SenhaEnc { get; set; } = [];
        public byte[] TokenSgaEnc { get; set; } = [];
    }

    private sealed class ContactBoletoRow
    {
        public string AssociadoRef { get; set; } = "";
        public string? HinovaNossoNumero { get; set; }
        public string Status { get; set; } = "";
    }

    private static async Task<IResult> WhatsappAsync(Guid id, RequestContext request, Database db,
        IHinovaClient hinova, AesGcmCipher cipher, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        // Check the same wallet scope as the list before touching Hinova credentials. An ID from
        // another consultant's wallet (or another tenant) must not reveal its contact.
        var local = await db.InTenantAsync(tenant, user, tx => tx.QuerySingleOrDefaultAsync<ContactBoletoRow>(
            """
            select b.associado_ref, b.hinova_nosso_numero, b.status from boletos b
             where b.tenant_id = @tenant and b.id = @id
               and b.participante_id = any(app.carteira_owner_ids())
            """, new { tenant, id }), ct);
        if (local is null || string.IsNullOrWhiteSpace(local.AssociadoRef))
            throw new ApiProblem(StatusCodes.Status404NotFound, "carteira.boleto_not_found");

        if (local.Status is "recebido" or "cancelado")
            throw new ApiProblem(StatusCodes.Status409Conflict, "carteira.cobranca_bloqueada");
        if (string.IsNullOrWhiteSpace(local.HinovaNossoNumero))
            throw new ApiProblem(StatusCodes.Status409Conflict, "carteira.cobranca_nao_verificada");

        // The integration credentials are admin-only under RLS. The server reads them through its
        // privileged connection only after the scoped boleto check above; they never reach the UI.
        var credentials = await db.AsPlatformAsync(tenant, tx => tx.QuerySingleOrDefaultAsync<HinovaCredenciaisRow>(
            "select usuario_enc, senha_enc, token_sga_enc from hinova_credenciais where tenant_id = @tenant",
            new { tenant }), ct);
        if (credentials is null)
            throw new ApiProblem(StatusCodes.Status503ServiceUnavailable, "hinova.nao_configurado");

        string token;
        try
        {
            token = await hinova.AutenticarAsync(cipher.Decrypt(credentials.UsuarioEnc),
                cipher.Decrypt(credentials.SenhaEnc), cipher.Decrypt(credentials.TokenSgaEnc), ct);
        }
        catch (HinovaAuthException ex)
        {
            throw HinovaAuth.ToProblem(ex, StatusCodes.Status503ServiceUnavailable);
        }

        // Recheck the exact boleto at click time. Any upstream failure stops the redirect.
        var live = await hinova.BuscarBoletoAsync(token, local.HinovaNossoNumero, ct);
        if (live is null || live.NossoNumero != local.HinovaNossoNumero || live.CodigoAssociado != local.AssociadoRef)
            throw new ApiProblem(StatusCodes.Status409Conflict, "carteira.cobranca_nao_verificada");
        if (HinovaBoletoStatus.IsPaid(live) || HinovaBoletoStatus.IsClosed(live))
        {
            // Scope was checked above. Consultants cannot write boletos under RLS, so update
            // only this previously authorized ID through the privileged connection.
            await db.AsPlatformAsync(tenant, async tx =>
            {
                await tx.ExecuteAsync("""
                    update boletos set status = @status,
                        valor = case when @paid then @valor else valor end,
                        pago_em = case when @paid then @pagamento else pago_em end,
                        commission_hold_reason = case when @paid then null else commission_hold_reason end
                    where tenant_id = @tenant and id = @id and status <> 'recebido'
                    """, new { tenant, id, paid = HinovaBoletoStatus.IsPaid(live),
                        status = HinovaBoletoStatus.IsPaid(live) ? "recebido" : "cancelado",
                        valor = live.ValorPagamento, pagamento = live.DataPagamento });
                return 0;
            }, ct);
            throw new ApiProblem(StatusCodes.Status409Conflict, "carteira.cobranca_bloqueada");
        }
        if (!HinovaBoletoStatus.CanCollect(live))
            throw new ApiProblem(StatusCodes.Status409Conflict, "carteira.cobranca_nao_verificada");

        var phone = await hinova.BuscarContatoAssociadoAsync(token, local.AssociadoRef, ct);
        if (phone is null)
            throw new ApiProblem(StatusCodes.Status404NotFound, "carteira.contato_indisponivel");
        return Results.Ok(new { telefone = phone });
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
        public decimal? TarifaBancaria { get; set; }
        public decimal? ValorLiquido { get; set; }
        public bool TarifaPendente { get; set; }
    }

    private sealed class TotalsRow
    {
        public int TotalCount { get; set; }
        public decimal TotalValor { get; set; }
        public decimal TotalComissao { get; set; }
    }

    private static async Task<IResult> ListAsync(string? status, string? query, int? page, string? tarifas, string? competencia, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        var currentPage = page is null or < 1 ? 1 : page.Value;
        currentPage = Math.Min(currentPage, 100_000);
        var mesPagamento = string.IsNullOrWhiteSpace(competencia) ? (DateOnly?)null : CommissionService.ParseCompetencia(competencia);
        var somenteTarifasPendentes = tarifas == "pendentes";
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
                   and (not @somenteTarifasPendentes or (b.status='recebido' and b.comissao_base is null))
                   and (@mesPagamento::date is null or (b.pago_em >= @mesPagamento and b.pago_em < @mesPagamento + interval '1 month'))
                   and (@searchFilter::text is null or b.associado_nome ilike '%' || @searchFilter || '%' or b.placa ilike '%' || @searchFilter || '%')
                """,
                new { tenant, ownerIds, statusFilter, somenteTarifasPendentes, mesPagamento, searchFilter });

            var items = await tx.QueryAsync<ItemRow>(
                $"""
                select b.id, b.associado_nome, b.placa, b.valor, b.status, b.vencimento, b.pago_em,
                       b.tarifa_bancaria, b.comissao_base as valor_liquido,
                       (b.status='recebido' and b.comissao_base is null) as tarifa_pendente,
                       case when b.status = 'atraso' then ((now() at time zone 'America/Sao_Paulo')::date - b.vencimento)::int end as dias_atraso,
                       {CommissaoExpression} as comissao
                  from boletos b
                 where b.tenant_id = @tenant and b.participante_id = any(@ownerIds)
                   and (@statusFilter::text is null or b.status = @statusFilter)
                   and (not @somenteTarifasPendentes or (b.status='recebido' and b.comissao_base is null))
                   and (@mesPagamento::date is null or (b.pago_em >= @mesPagamento and b.pago_em < @mesPagamento + interval '1 month'))
                   and (@searchFilter::text is null or b.associado_nome ilike '%' || @searchFilter || '%' or b.placa ilike '%' || @searchFilter || '%')
                 order by coalesce(b.pago_em, b.vencimento) desc, b.id
                 limit @pageSize offset @offset
                """,
                new { tenant, ownerIds, statusFilter, somenteTarifasPendentes, mesPagamento, searchFilter, pageSize = PageSize, offset = (currentPage - 1) * PageSize });

            return new CarteiraResponse(
                items.Select(i => new CarteiraItemResponse(i.Id, i.AssociadoNome, i.Placa, i.Valor, i.Status, i.Vencimento, i.PagoEm, i.DiasAtraso, i.Comissao, i.TarifaBancaria, i.ValorLiquido, i.TarifaPendente)).ToList(),
                currentPage, PageSize, totals.TotalCount, totalAllCount, statusCounts, totals.TotalValor, totals.TotalComissao);
        }, ct);

        return Results.Ok(response);
    }
}
