using System.Text.Json;
using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.Tests;
public class HinovaTarifaClientTests
{
    private sealed class Handler(string json):HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Body=await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)};
        }
    }
    [Theory]
    [InlineData("0",0d)]
    [InlineData("1.75",1.75)]
    [InlineData("",null)]
    [InlineData("-1",null)]
    public async Task Parses_real_history_without_converting_missing_fee_to_zero(string fee,double? expected)
    {
        var handler=new Handler(JsonSerializer.Serialize(new{total="1",resultado=new[]{new{nosso_numero=123,codigo_alteracao="456",nome_campo_tabela="tarifa_cobranca_banco",valor_posterior=fee,data_alteracao="2026-10-01T00:00:00-0300"}}}));
        var client=new HinovaClient(new HttpClient(handler){BaseAddress=new Uri("https://example.com/")});
        var page=await client.ListarAlteracoesTarifaAsync("token",new(2026,10,1),2,1000,CancellationToken.None);
        Assert.Equal(expected is null?null:(decimal?)expected,page.Alteracoes.Single().Valor);
        Assert.Equal(TimeSpan.FromHours(-3),page.Alteracoes.Single().DataAlteracao.Offset);
        using var body=JsonDocument.Parse(handler.Body!);
        Assert.Equal(1000,body.RootElement.GetProperty("quantidade_por_pagina").GetInt32());
        Assert.Equal(2,body.RootElement.GetProperty("inicio_paginacao").GetInt32());
        Assert.Equal("N",body.RootElement.GetProperty("ultima_alteracao").GetString());
        Assert.Equal("01/10/2026",body.RootElement.GetProperty("data_final").GetString());
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"total\":\"0\"}")]
    public async Task Invalid_history_page_is_a_failure_not_a_completed_empty_day(string json)
    {
        var client=new HinovaClient(new HttpClient(new Handler(json)){BaseAddress=new Uri("https://example.com/")});
        await Assert.ThrowsAsync<HttpRequestException>(()=>client.ListarAlteracoesTarifaAsync("token",new(2026,10,1),0,1000,CancellationToken.None));
    }
}
