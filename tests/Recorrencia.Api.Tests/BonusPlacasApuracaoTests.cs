using System.Net;
using Recorrencia.Api.Cli;
using Recorrencia.Api.Integracoes;
using Recorrencia.Api.PlanosCarreira;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class BonusPlacasApuracaoTests(ApiFixture api)
{
    public sealed record ApuracaoDto(string Competencia, bool PlanoAtribuido, DateOnly DataLimitePagamento,
        int PlacasPagas, decimal? ValorPorPlaca, decimal ValorBonus);

    [Fact]
    public async Task Apuracao_consulta_primeira_mensalidade_e_aplica_faixa_do_plano()
    {
        var s = await api.SeedAsync();
        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        var today = api.Time.GetUtcNow().Date;
        var inicio = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var competencia = inicio.ToString("yyyy-MM");

        var created = await admin.PostAsync("/planos-carreira", new
        {
            name = "Bônus 45 dias", classificacao = "so_externo", janelaApuracaoDias = 45,
            recorrenciaAtiva = false,
            faixas = new[]
            {
                new { quantidadeMin = 1, quantidadeMax = (int?)14, valorPorPlaca = 33m },
                new { quantidadeMin = 15, quantidadeMax = (int?)null, valorPorPlaca = 40m },
            },
        });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var plano = (await created.Content.ReadFromJsonAsync<PlanoCarreiraTests.PlanoDto>(ApiClient.Json))!;
        await ApiClient.ExpectAsync(await admin.PutAsync("/integracoes/hinova/credenciais",
            new { usuario = "usuario", senha = "senha", tokenSga = "token" }), HttpStatusCode.NoContent);
        await ApiClient.ExpectAsync(await admin.PostAsync("/integracoes/hinova/mapeamentos",
            new { userId = s.Joao, codigoVoluntario = "242", nomeHinova = "Consultor", cpfHinova = "11122233344" }), HttpStatusCode.Created);

        api.Hinova.BoletosPaginas.Clear();
        var boletos = Enumerable.Range(1, 15).Select(i => new HinovaBoleto(i.ToString(), 100m, 100m,
            i.ToString(), "Associado", null, inicio,
            [new HinovaVeiculoBoleto(i.ToString(), "242", $"ABC{i:D4}", inicio)], "5", "FECHAMENTO")).ToList();
        api.Hinova.BoletosPaginas.Add(new HinovaBoletoPagina(1, boletos.Count, 0, boletos));

        var resultado = await admin.GetJsonAsync<ApuracaoDto>(
            $"/planos-carreira/{plano.Id}/apuracao?competencia={competencia}&participanteId={s.Joao}");

        Assert.Equal(15, resultado.PlacasPagas);
        Assert.Equal(40m, resultado.ValorPorPlaca);
        Assert.Equal(600m, resultado.ValorBonus);
        Assert.False(resultado.PlanoAtribuido);
        Assert.Equal(BonusPlacasCalculator.LimitePagamento(inicio, 45), resultado.DataLimitePagamento);
        Assert.Equal("242", api.Hinova.UltimoFiltroBoletos?.CodigoVoluntario);
    }
}
