using Recorrencia.Api.Commissions;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Integracoes;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.PlanosCarreira;

/// <summary>Apura a produção que vence antes do mês de pagamento. A produção de
/// agosto, por exemplo, compõe o pagamento de novembro.</summary>
public static class BonusPayoutService
{
    public sealed record BonusPayout(Guid BeneficiarioId, Guid PlanoId, string PlanoNome,
        DateOnly CompetenciaProducao, DateOnly DataLimitePagamento, int Quantidade,
        decimal? ValorUnitario, decimal Valor,
        IReadOnlyList<BonusPlacasCalculator.PlacaPaga> Placas);

    private sealed class AssignmentRow
    {
        public Guid BeneficiarioId { get; set; }
        public Guid PlanoId { get; set; }
        public string PlanoNome { get; set; } = "";
        public int JanelaApuracaoDias { get; set; }
        public string? CodigoVoluntario { get; set; }
    }

    private sealed class FaixaRow
    {
        public Guid PlanoId { get; set; }
        public int QuantidadeMin { get; set; }
        public int? QuantidadeMax { get; set; }
        public decimal ValorPorPlaca { get; set; }
    }

    public static DateOnly CompetenciaProducao(DateOnly competenciaPagamento) => competenciaPagamento.AddMonths(-3);

    public static async Task<IReadOnlyList<BonusPayout>> CalculateAsync(
        DateOnly competenciaPagamento, Guid tenant, Guid actor, Database db, IHinovaClient hinova,
        AesGcmCipher cipher, TimeProvider time, CancellationToken ct)
    {
        var producao = CompetenciaProducao(competenciaPagamento);
        var (assignments, faixas) = await db.InTenantAsync(tenant, actor, async tx =>
        {
            var assignments = (await tx.QueryAsync<AssignmentRow>(
                """
                select u.id as beneficiario_id, p.id as plano_id, p.name as plano_nome,
                       p.janela_apuracao_dias, m.codigo_voluntario
                  from users u
                  join planos_carreira p on p.id = u.plano_carreira_id
                  left join hinova_voluntario_mapping m on m.user_id = u.id
                 where u.tenant_id = @tenant and u.status = 'ativo' and p.status = 'ativo'
                   and exists (select 1 from planos_carreira_faixas_bonus f where f.plano_carreira_id = p.id)
                """, new { tenant })).ToList();
            var faixas = (await tx.QueryAsync<FaixaRow>(
                """
                select f.plano_carreira_id as plano_id, f.quantidade_min, f.quantidade_max, f.valor_por_placa
                  from planos_carreira_faixas_bonus f
                 where f.plano_carreira_id = any(@ids)
                """, new { ids = assignments.Select(a => a.PlanoId).Distinct().ToArray() })).ToList();
            return (assignments, faixas);
        }, ct);

        if (assignments.Count == 0) return [];
        if (assignments.Any(a => string.IsNullOrWhiteSpace(a.CodigoVoluntario)))
            throw new ApiProblem(StatusCodes.Status409Conflict, "hinova.vinculo_not_found");

        var hoje = DateOnly.FromDateTime(time.GetUtcNow().ToOffset(TimeSpan.FromHours(-3)).DateTime);
        var maiorLimite = assignments.Max(a => BonusPlacasCalculator.LimitePagamento(producao, a.JanelaApuracaoDias));
        // Never freeze a partially elapsed production window as a final payment.
        if (maiorLimite >= competenciaPagamento || hoje < maiorLimite)
            throw new ApiProblem(StatusCodes.Status409Conflict, "bonus_placas.janela_aberta");

        var token = await HinovaAuth.GetTokenUsuarioAsync(db, tenant, actor, cipher, hinova, ct);
        var boletos = new List<HinovaBoleto>();
        for (var blocoInicio = producao; blocoInicio <= maiorLimite; blocoInicio = blocoInicio.AddDays(31))
        {
            var blocoFim = blocoInicio.AddDays(30);
            if (blocoFim > maiorLimite) blocoFim = maiorLimite;
            for (var pagina = 0; ; pagina++)
            {
                var resultado = await hinova.ListarBoletosPeriodoAsync(token,
                    new HinovaBoletoPeriodoFiltro(blocoInicio, blocoFim, 1000, pagina), ct);
                boletos.AddRange(resultado.Boletos);
                if (resultado.Boletos.Count == 0 || pagina + 1 >= resultado.NumeroPaginas) break;
            }
        }

        return assignments.Select(a =>
        {
            var resultado = BonusPlacasCalculator.Calcular(producao, a.JanelaApuracaoDias,
                a.CodigoVoluntario!, faixas.Where(f => f.PlanoId == a.PlanoId)
                    .Select(f => new BonusPlacasCalculator.Faixa(f.QuantidadeMin, f.QuantidadeMax, f.ValorPorPlaca))
                    .ToList(), boletos);
            return new BonusPayout(a.BeneficiarioId, a.PlanoId, a.PlanoNome, producao,
                BonusPlacasCalculator.LimitePagamento(producao, a.JanelaApuracaoDias), resultado.Quantidade,
                resultado.ValorPorPlaca, resultado.Valor, resultado.Placas);
        }).ToList();
    }
}
