using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Email;

public sealed class LinkBuilder(IOptions<WebOptions> options)
{
    public string SetPassword(string slug, string token) =>
        $"{options.Value.Scheme}://{slug}.{options.Value.RootDomain}/definir-senha/{Uri.EscapeDataString(token)}";

    /// <summary>
    /// Absolute URL to the same logo-aprovec.webp the web app itself serves from /public, for use
    /// in email HTML (which needs a real address, not a relative path). Only resolves to something
    /// mail clients can actually fetch once Web:RootDomain is a real public domain.
    /// </summary>
    public string Logo(string slug) =>
        $"{options.Value.Scheme}://{slug}.{options.Value.RootDomain}/logo-aprovec.webp";
}
