using System.Text.Json;
using Microsoft.Extensions.Options;
using Recorrencia.Api;
using Recorrencia.Api.Email;

namespace Recorrencia.Api.Tests;

public class ResendEmailSenderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastRequestBody = request.Content?.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            return Task.FromResult(respond(request));
        }
    }

    private static (ResendEmailSender Sender, StubHandler Handler) NewSender(
        Func<HttpRequestMessage, HttpResponseMessage> respond, string apiKey = "re_test123", string from = "onboarding@resend.dev")
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.resend.com/") };
        var options = Options.Create(new ResendOptions { ApiKey = apiKey, FromAddress = from });
        return (new ResendEmailSender(http, options), handler);
    }

    [Fact]
    public async Task SendAsync_posts_to_emails_with_bearer_auth_and_the_expected_body()
    {
        var (sender, handler) = NewSender(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id = "abc123" }),
        });

        await sender.SendAsync("joao@aprovec.local", "Convite de acesso", "Defina sua senha em: https://...", CancellationToken.None);

        Assert.Equal("https://api.resend.com/emails", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("re_test123", handler.LastRequest.Headers.Authorization!.Parameter);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal("APROVEC <onboarding@resend.dev>", root.GetProperty("from").GetString());
        Assert.Equal("joao@aprovec.local", Assert.Single(root.GetProperty("to").EnumerateArray()).GetString());
        Assert.Equal("Convite de acesso", root.GetProperty("subject").GetString());
        Assert.Equal("Defina sua senha em: https://...", root.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendAsync_throws_ResendException_with_the_provider_message_on_failure()
    {
        var (sender, _) = NewSender(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = JsonContent.Create(new { message = "The aprovec.com domain is not verified." }),
        });

        var ex = await Assert.ThrowsAsync<ResendException>(() =>
            sender.SendAsync("joao@aprovec.local", "Assunto", "Corpo", CancellationToken.None));

        Assert.Equal("The aprovec.com domain is not verified.", ex.Message);
    }
}
