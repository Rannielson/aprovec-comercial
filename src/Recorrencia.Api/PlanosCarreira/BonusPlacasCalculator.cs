using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.PlanosCarreira;

public static class BonusPlacasCalculator
{
    public sealed record Faixa(int QuantidadeMin, int? QuantidadeMax, decimal ValorPorPlaca);
    public sealed record PlacaPaga(string Placa, string CodigoVeiculo, DateOnly DataContrato, DateOnly DataPagamento,
        string NossoNumero);
    public sealed record Resultado(int Quantidade, decimal? ValorPorPlaca, decimal Valor, IReadOnlyList<PlacaPaga> Placas);

    // O prazo comercial de "45 dias" fecha no dia 15 do segundo mês seguinte.
    // Assim julho termina em 15/09 e agosto em 15/10, conforme a regra aprovada.
    public static DateOnly LimitePagamento(DateOnly competencia, int janelaDias) =>
        janelaDias == 45
            ? competencia.AddMonths(2).AddDays(14)
            : competencia.AddMonths(1).AddDays(-1).AddDays(janelaDias);

    public static Resultado Calcular(DateOnly competencia, int janelaDias, string codigoVoluntario,
        IReadOnlyList<Faixa> faixas, IEnumerable<HinovaBoleto> boletos)
    {
        var ultimoDia = competencia.AddMonths(1).AddDays(-1);
        var limitePagamento = LimitePagamento(competencia, janelaDias);
        var porVeiculo = new Dictionary<string, PlacaPaga>(StringComparer.OrdinalIgnoreCase);

        foreach (var boleto in boletos)
        {
            // FECHAMENTO is Hinova's monthly payment type (5); ADESÃO (1) is a separate component.
            if (boleto.CodigoTipoBoleto != "5" || boleto.DataPagamento is not { } pagamento
                || boleto.ValorPagamento <= 0 || pagamento < competencia || pagamento > limitePagamento)
                continue;

            foreach (var veiculo in boleto.Veiculos)
            {
                if (veiculo.CodigoVoluntario != codigoVoluntario || veiculo.DataInicioContratoVeiculo is not { } contrato
                    || contrato < competencia || contrato > ultimoDia)
                    continue;

                var placa = new string((veiculo.Placa ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
                // A vehicle can have several paid boletos (and even a changed plate). Hinova's
                // vehicle code identifies the production unit; plate is only a fallback.
                var chave = !string.IsNullOrWhiteSpace(veiculo.CodigoVeiculo)
                    ? $"veiculo:{veiculo.CodigoVeiculo.Trim()}"
                    : placa.Length > 0 ? $"placa:{placa}" : "";
                if (chave.Length == 0)
                    continue;
                var candidata = new PlacaPaga(placa, veiculo.CodigoVeiculo, contrato, pagamento, boleto.NossoNumero);
                if (!porVeiculo.TryGetValue(chave, out var anterior) || pagamento < anterior.DataPagamento)
                    porVeiculo[chave] = candidata;
            }
        }

        var placas = porVeiculo.Values.OrderBy(p => p.DataPagamento).ThenBy(p => p.Placa).ToList();
        var faixa = faixas.Where(f => placas.Count >= f.QuantidadeMin &&
                (f.QuantidadeMax is null || placas.Count <= f.QuantidadeMax))
            .OrderByDescending(f => f.QuantidadeMin).FirstOrDefault();
        return new Resultado(placas.Count, faixa?.ValorPorPlaca,
            placas.Count * (faixa?.ValorPorPlaca ?? 0m), placas);
    }
}
