using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Configuration;
using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.Tests;

public class HinovaAccessTests
{
    private const string LimitBody = """
        {"mensagem":"Forbidden","error":{"0":"Token BLOQUEADO temporariamente por excesso de extracao de DADOS. A liberacao ocorrera AUTOMATICAMENTE apos alguns minutos"}}
        """;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(handle(request));
    }

    [Theory]
    [InlineData(401, "", "hinova.credenciais_invalidas")]
    [InlineData(403, "Forbidden", "hinova.acesso_recusado")]
    [InlineData(429, "", "hinova.limite_extracao")]
    [InlineData(503, "Unavailable", "hinova.autenticacao_indisponivel")]
    public void Vendor_failures_are_not_all_reported_as_wrong_credentials(int status, string body, string code)
    {
        var error = HinovaAuthException.FromResponse((HttpStatusCode)status, body);
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task Actual_vendor_403_extraction_block_is_shared_and_respected_until_cooldown_expires()
    {
        var clock = new FakeTimeProvider();
        var cache = new HinovaTokenCache(clock);
        var calls = 0;
        var handler = new Handler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(LimitBody) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { token_usuario = "allowed" }) });
        HinovaClient Client() => new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, cache);
        var first = await Assert.ThrowsAsync<HinovaAuthException>(() => Client().AutenticarAsync("u", "p", "k", CancellationToken.None));
        Assert.Equal("hinova.limite_extracao", first.Code);
        Assert.Equal(TimeSpan.FromMinutes(10), first.RetryAfter);
        await Assert.ThrowsAsync<HinovaAuthException>(() => Client().AutenticarAsync("u", "p", "k", CancellationToken.None));
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("allowed", await Client().AutenticarAsync("u", "p", "k", CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Vendor_retry_after_is_not_shortened_to_the_default_pause()
    {
        var clock=new FakeTimeProvider();var cache=new HinovaTokenCache(clock);var calls=0;
        var client=new HinovaClient(new HttpClient(new Handler(_=>
        {
            if(++calls>1)return new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new {token_usuario="allowed"})};
            var response=new HttpResponseMessage(HttpStatusCode.TooManyRequests){Content=new StringContent(LimitBody)};
            response.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
            return response;
        })){BaseAddress=new Uri("https://example.test/")},cache);
        var error=await Assert.ThrowsAsync<HinovaAuthException>(()=>client.AutenticarAsync("u","p","k",CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(20),error.RetryAfter);
        clock.Advance(TimeSpan.FromMinutes(10));
        await Assert.ThrowsAsync<HinovaAuthException>(()=>client.AutenticarAsync("u","p","k",CancellationToken.None));
        Assert.Equal(1,calls);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal("allowed",await client.AutenticarAsync("u","p","k",CancellationToken.None));
    }

    [Fact]
    public async Task Extraction_block_during_reads_stops_further_reads_and_authentication()
    {
        var calls = 0;
        var cache = new HinovaTokenCache();
        var client = new HinovaClient(new HttpClient(new Handler(request =>
        {
            calls++;
            return request.RequestUri!.AbsolutePath.EndsWith("autenticar")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { token_usuario = "token" }) }
                : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(LimitBody) };
        })) { BaseAddress = new Uri("https://example.test/") }, cache);
        var token = await client.AutenticarAsync("u", "p", "k", CancellationToken.None);
        var filter = new HinovaBoletoPeriodoFiltro(new DateOnly(2026,9,1), new DateOnly(2026,9,30), 500, 0);
        var error = await Assert.ThrowsAsync<HinovaAuthException>(() => client.ListarBoletosPeriodoAsync(token, filter, CancellationToken.None));
        Assert.Equal("hinova.limite_extracao", error.Code);
        await Assert.ThrowsAsync<HinovaAuthException>(() => client.ListarBoletosPeriodoAsync(token, filter, CancellationToken.None));
        await Assert.ThrowsAsync<HinovaAuthException>(() => client.AutenticarAsync("u", "p", "k", CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Shared_throttle_spaces_requests_and_honors_cancellation()
    {
        var clock = new FakeTimeProvider();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Hinova:MinRequestIntervalMs"] = "2000" }).Build();
        var throttle = new HinovaRequestThrottle(clock, config);
        await throttle.WaitAsync(CancellationToken.None);
        var second = throttle.WaitAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(2));
        await second;
        using var cancel = new CancellationTokenSource();
        var third = throttle.WaitAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
    }

    [Fact]
    public async Task Invalid_success_payload_does_not_claim_the_password_is_wrong()
    {
        var client = new HinovaClient(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("Unexpected upstream error") })) { BaseAddress = new Uri("https://example.test/") });
        var error = await Assert.ThrowsAsync<HinovaAuthException>(() => client.AutenticarAsync("u", "p", "k", CancellationToken.None));
        Assert.Equal("hinova.autenticacao_indisponivel", error.Code);
    }
}
