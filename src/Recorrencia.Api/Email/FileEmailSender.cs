using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Email;

public sealed class FileEmailSender(IOptions<EmailOptions> options, IHostEnvironment environment) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var directory = Path.GetFullPath(options.Value.OutboxDir, environment.ContentRootPath);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, $"Para: {to}\nAssunto: {subject}\n\n{body}\n", ct);
    }
}
