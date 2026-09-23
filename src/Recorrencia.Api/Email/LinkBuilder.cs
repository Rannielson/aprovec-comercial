using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Email;

public sealed class LinkBuilder(IOptions<WebOptions> options)
{
    public string SetPassword(string slug, string token) =>
        $"{options.Value.Scheme}://{slug}.{options.Value.RootDomain}/definir-senha/{Uri.EscapeDataString(token)}";
}
