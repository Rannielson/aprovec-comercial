using Recorrencia.Api.Integracoes;
using Recorrencia.Api.PlanosCarreira;

namespace Recorrencia.Api.Tests;

public class BonusPlacasCalculatorTests
{
    private static readonly BonusPlacasCalculator.Faixa[] Faixas =
    [
        new(1, 14, 33m), new(15, 19, 40m), new(20, 29, 50m), new(30, null, 60m),
    ];

    private static HinovaBoleto Boleto(int id, DateOnly contrato, DateOnly pagamento, string vendedor = "242",
        string tipo = "5", decimal valor = 100m, string? placa = null, string? codigoVeiculo = null) =>
        new(id.ToString(), valor, valor, id.ToString(), "Associado", null, pagamento,
            [new HinovaVeiculoBoleto(codigoVeiculo ?? id.ToString(), vendedor, placa ?? $"ABC{id:D4}", contrato)], tipo, "FECHAMENTO");

    [Fact]
    public void Conta_primeira_mensalidade_por_veiculo_ate_45_dias_e_aplica_faixa_ao_total()
    {
        var inicio = new DateOnly(2026, 8, 1);
        var boletos = Enumerable.Range(1, 30)
            .Select(id => Boleto(id, inicio.AddDays(id - 1), new DateOnly(2026, 10, 15)))
            .Concat([
                Boleto(1, inicio, new DateOnly(2026, 9, 1)), // mesma placa: conta uma vez
                Boleto(31, inicio, new DateOnly(2026, 10, 16)), // após o limite
                Boleto(32, new DateOnly(2026, 7, 31), new DateOnly(2026, 8, 1)), // contrato antigo
                Boleto(33, inicio, inicio, vendedor: "184"),
                Boleto(34, inicio, inicio, tipo: "1"), // adesão não é mensalidade
                Boleto(35, inicio, inicio, valor: 0m),
            ]);

        var resultado = BonusPlacasCalculator.Calcular(inicio, 45, "242", Faixas, boletos);

        Assert.Equal(30, resultado.Quantidade);
        Assert.Equal(60m, resultado.ValorPorPlaca);
        Assert.Equal(1800m, resultado.Valor);
        Assert.Equal(new DateOnly(2026, 9, 1), resultado.Placas.Single(p => p.CodigoVeiculo == "1").DataPagamento);
    }

    [Fact]
    public void Conta_veiculos_unicos_pelo_codigo_mesmo_com_boletos_e_placas_repetidos()
    {
        var inicio = new DateOnly(2026, 7, 1);
        var boletos = new[]
        {
            Boleto(1, inicio, new DateOnly(2026, 7, 10), placa: "AAA1111", codigoVeiculo: "101"),
            Boleto(2, inicio, new DateOnly(2026, 8, 10), placa: "BBB2222", codigoVeiculo: "101"),
            Boleto(3, inicio, new DateOnly(2026, 8, 11), placa: "AAA1111", codigoVeiculo: "102"),
        };

        var resultado = BonusPlacasCalculator.Calcular(inicio, 45, "242", Faixas, boletos);

        Assert.Equal(2, resultado.Quantidade);
        Assert.Equal(66m, resultado.Valor);
        Assert.Equal(new DateOnly(2026, 7, 10), resultado.Placas.Single(p => p.CodigoVeiculo == "101").DataPagamento);
    }

    [Fact]
    public void Prazo_de_julho_inclui_quinze_de_setembro_mas_nao_dezesseis()
    {
        var inicio = new DateOnly(2026, 7, 1);
        var boletos = new[]
        {
            Boleto(1, inicio, new DateOnly(2026, 9, 15)),
            Boleto(2, inicio, new DateOnly(2026, 9, 16)),
        };

        var resultado = BonusPlacasCalculator.Calcular(inicio, 45, "242", Faixas, boletos);

        Assert.Equal(new DateOnly(2026, 9, 15), BonusPlacasCalculator.LimitePagamento(inicio, 45));
        Assert.Single(resultado.Placas);
        Assert.Equal("1", resultado.Placas[0].CodigoVeiculo);
    }

    [Fact]
    public void Inclui_fechamento_baixado_com_pendencia_quando_ha_pagamento()
    {
        var inicio = new DateOnly(2026, 7, 1);
        var boleto = Boleto(1, inicio, new DateOnly(2026, 8, 25), valor: 800m)
            with { ValorBoleto = 825m };

        var resultado = BonusPlacasCalculator.Calcular(inicio, 45, "242", Faixas, [boleto]);

        Assert.Equal(1, resultado.Quantidade);
        Assert.Equal(33m, resultado.Valor);
    }

    [Theory]
    [InlineData(14, 33, 462)]
    [InlineData(15, 40, 600)]
    [InlineData(19, 40, 760)]
    [InlineData(20, 50, 1000)]
    [InlineData(29, 50, 1450)]
    [InlineData(30, 60, 1800)]
    public void Faixas_aplicam_uma_taxa_sobre_todas_as_placas(int quantidade, int taxa, int total)
    {
        var inicio = new DateOnly(2026, 8, 1);
        var boletos = Enumerable.Range(1, quantidade).Select(id => Boleto(id, inicio, inicio));
        var resultado = BonusPlacasCalculator.Calcular(inicio, 45, "242", Faixas, boletos);

        Assert.Equal(quantidade, resultado.Quantidade);
        Assert.Equal(taxa, resultado.ValorPorPlaca);
        Assert.Equal(total, resultado.Valor);
    }
}
