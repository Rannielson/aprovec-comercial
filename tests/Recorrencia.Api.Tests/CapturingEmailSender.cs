using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Recorrencia.Api.Email;

namespace Recorrencia.Api.Tests;

public sealed record SentEmail(string To, string Subject, string Body);

public sealed partial class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<SentEmail> _sent = new();

    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        _sent.Enqueue(new SentEmail(to, subject, body));
        return Task.CompletedTask;
    }

    public SentEmail? LastTo(string to) => _sent.LastOrDefault(e => e.To == to);

    public static string ExtractToken(string body) => TokenPattern().Match(body).Groups[1].Value;

    [GeneratedRegex(@"/definir-senha/([A-Za-z0-9_-]+)")]
    private static partial Regex TokenPattern();
}
