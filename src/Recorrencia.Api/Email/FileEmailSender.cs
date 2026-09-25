using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Email;

public sealed class FileEmailSender(IOptions<EmailOptions> options, IHostEnvironment environment) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var directory = Path.GetFullPath(options.Value.OutboxDir, environment.ContentRootPath);
        Directory.CreateDirectory(directory);
        // .html, not .txt: body is now the branded HTML from EmailTemplates, so the file opens
        // straight into a browser for a real preview of what would actually be sent.
        var file = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(file, $"<!-- Para: {to} | Assunto: {subject} -->\n{body}\n", ct);
    }
}
