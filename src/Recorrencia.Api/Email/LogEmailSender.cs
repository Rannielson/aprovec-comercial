namespace Recorrencia.Api.Email;

public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        logger.LogInformation("E-mail para {To} — {Subject}\n{Body}", to, subject, body);
        return Task.CompletedTask;
    }
}
