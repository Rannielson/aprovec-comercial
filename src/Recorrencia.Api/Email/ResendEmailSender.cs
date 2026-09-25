using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Email;

/// <summary>
/// Sends real email through the Resend API (https://resend.com/docs/api-reference/emails/send-email).
/// Chosen over LogEmailSender/FileEmailSender in Program.cs whenever Resend:ApiKey is configured.
/// </summary>
public sealed class ResendEmailSender(HttpClient http, IOptions<ResendOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(new SendRequest($"APROVEC <{options.Value.FromAddress}>", [to], subject, body)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiKey);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken: ct);
            throw new ResendException(error?.Message ?? $"Resend respondeu {(int)response.StatusCode}.");
        }
    }

    private sealed record SendRequest(string From, string[] To, string Subject, string Text);

    private sealed record ErrorResponse([property: JsonPropertyName("message")] string? Message);
}

public sealed class ResendException(string message) : Exception(message);
