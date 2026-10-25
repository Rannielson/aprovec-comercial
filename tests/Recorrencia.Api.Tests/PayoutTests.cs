using System.Net;
using Npgsql;
using Recorrencia.Api.Cli;
using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class PayoutTests(ApiFixture api)
{
    public sealed record PlanDto(Guid Id);
    public sealed record PlacaDto(string CodigoVeiculo);
    public sealed record BonusDto(string CompetenciaProducao, int Quantidade, decimal Valor,
        List<PlacaDto> Placas);
    public sealed record BeneficiarioDto(Guid UserId, decimal Arvore, decimal Placas,
        decimal Extra, decimal Total, BonusDto? BonusPlacas);
    public sealed record PagamentoDto(string Status, bool Previa, decimal Arvore,
        decimal Placas, decimal Extra, decimal Total, List<BeneficiarioDto> Beneficiarios);
    public sealed record ConfirmDto(decimal Total);

    [Fact]
    public async Task Fechamento_soma_arvore_placas_unicas_e_extra_aprovado_sem_duplicar_percentuais()
    {
        var s = await api.SeedAsync();
        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);

        var planResponse = await admin.PostAsync("/planos-carreira", new
        {
            name = "Plano externo de teste", classificacao = "so_externo", janelaApuracaoDias = 45,
            bonusExtraValor = 100m, recorrenciaAtiva = true,
            faixas = new[] { new { quantidadeMin = 1, quantidadeMax = (int?)null, valorPorPlaca = 33m } },
            regrasRecorrencia = new object[]
            {
                new { tipo = "propria", nivel = (int?)null, taxa = 0.07m },
                new { tipo = "upline", nivel = (int?)1, taxa = 0.02m },
            },
        });
        await ApiClient.ExpectAsync(planResponse, HttpStatusCode.Created);
        var plan = (await planResponse.Content.ReadFromJsonAsync<PlanDto>(ApiClient.Json))!;
        await ApiClient.ExpectAsync(await admin.PutAsync($"/users/{s.Joao}/plano-carreira",
            new { planoCarreiraId = plan.Id }), HttpStatusCode.NoContent);
        await ApiClient.ExpectAsync(await admin.PutAsync("/integracoes/hinova/credenciais",
            new { usuario = "usuario", senha = "senha", tokenSga = "token" }), HttpStatusCode.NoContent);
        await ApiClient.ExpectAsync(await admin.PostAsync("/integracoes/hinova/mapeamentos",
            new { userId = s.Joao, codigoVoluntario = "242", nomeHinova = "João", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);

        api.Hinova.BoletosPaginas.Clear();
        var contrato = new DateOnly(2026, 5, 10);
        api.Hinova.BoletosPaginas.Add(new HinovaBoletoPagina(1, 3, 0,
        [
            new HinovaBoleto("1", 100m, 100m, "1", "Associado A", null, new DateOnly(2026, 5, 15),
                [new HinovaVeiculoBoleto("veiculo-1", "242", "ABC1234", contrato)], "5", "FECHAMENTO"),
            new HinovaBoleto("2", 100m, 100m, "1", "Associado A", null, new DateOnly(2026, 6, 15),
                [new HinovaVeiculoBoleto("veiculo-1", "242", "ABC1234", contrato)], "5", "FECHAMENTO"),
            new HinovaBoleto("3", 100m, 100m, "2", "Associado B", null, new DateOnly(2026, 6, 10),
                [new HinovaVeiculoBoleto("veiculo-2", "242", "DEF5678", contrato)], "5", "FECHAMENTO"),
        ]));

        try
        {
            var preview = await admin.GetJsonAsync<PagamentoDto>("/payouts/2026-08/preview");
            Assert.True(preview.Previa);
            Assert.Equal((2740m, 66m, 0m, 2806m),
                (preview.Arvore, preview.Placas, preview.Extra, preview.Total));
            var joaoPreview = preview.Beneficiarios.Single(b => b.UserId == s.Joao);
            Assert.Equal((1440m, 66m, 0m, 1506m),
                (joaoPreview.Arvore, joaoPreview.Placas, joaoPreview.Extra, joaoPreview.Total));
            Assert.Equal("2026-05", joaoPreview.BonusPlacas!.CompetenciaProducao);
            Assert.Equal(2, joaoPreview.BonusPlacas.Quantidade);
            Assert.Equal(2, joaoPreview.BonusPlacas.Placas.Count);

            await ApiClient.ExpectAsync(await admin.PutAsync($"/fechamentos/2026-08/bonus-extra/{s.Joao}",
                new { aprovado = true }), HttpStatusCode.NoContent);
            var approved = await admin.GetJsonAsync<PagamentoDto>("/payouts/2026-08/preview");
            Assert.Equal((2740m, 66m, 100m, 2906m),
                (approved.Arvore, approved.Placas, approved.Extra, approved.Total));

            var confirm = await admin.PostAsync("/fechamentos/2026-08/confirm");
            await ApiClient.ExpectAsync(confirm, HttpStatusCode.OK);
            Assert.Equal(2906m, (await confirm.Content.ReadFromJsonAsync<ConfirmDto>(ApiClient.Json))!.Total);

            api.Hinova.BoletosPaginas.Clear();
            var consultant = api.Client(s.Slug);
            await consultant.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);
            var statement = await consultant.GetJsonAsync<PagamentoDto>("/payouts/2026-08");
            var own = Assert.Single(statement.Beneficiarios);
            Assert.False(statement.Previa);
            Assert.Equal("confirmado", statement.Status);
            Assert.Equal((1440m, 66m, 100m, 1606m),
                (own.Arvore, own.Placas, own.Extra, own.Total));
            Assert.Equal(2, own.BonusPlacas!.Placas.Count);

            var immutable = await Assert.ThrowsAsync<PostgresException>(() => api.SqlAsync(
                "update fechamento_remuneracao set valor = 999 where tenant_id = @tenant and beneficiario_id = @joao and tipo = 'placas'",
                new { tenant = s.TenantId, joao = s.Joao }));
            Assert.Equal("fechamento.immutable", immutable.MessageText);

            var forbidden = await consultant.PutAsync($"/fechamentos/2026-08/bonus-extra/{s.Joao}",
                new { aprovado = false });
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        finally
        {
            api.Hinova.BoletosPaginas.Clear();
        }
    }
}
