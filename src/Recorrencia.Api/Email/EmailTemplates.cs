using System.Net;

namespace Recorrencia.Api.Email;

/// <summary>
/// Branded HTML for the transactional emails sent by PasswordEndpoints, UserEndpoints and
/// PlatformEndpoints.
/// </summary>
public static class EmailTemplates
{
    /// <param name="heading">Short H1, e.g. "Convite de acesso".</param>
    /// <param name="intro">One or two sentences of context above the button.</param>
    /// <param name="ctaLabel">Button text, e.g. "Definir minha senha".</param>
    /// <param name="url">The set-password link (already built by LinkBuilder).</param>
    /// <param name="note">Small print under the button -- TTL and/or a safety note.</param>
    /// <param name="logoUrl">
    /// Absolute URL to logo-aprovec.webp (LinkBuilder.Logo) -- must be a real publicly-reachable
    /// address, since mail clients fetch it from the open internet; a *.localhost URL renders as
    /// a broken image (the alt text still shows). Some older clients (notably Outlook desktop)
    /// don't render WEBP at all and will also fall back to the alt text.
    /// </param>
    public static string AccessLink(string heading, string intro, string ctaLabel, string url, string note, string logoUrl)
    {
        var safeHeading = WebUtility.HtmlEncode(heading);
        var safeIntro = WebUtility.HtmlEncode(intro);
        var safeCta = WebUtility.HtmlEncode(ctaLabel);
        var safeUrl = WebUtility.HtmlEncode(url);
        var safeNote = WebUtility.HtmlEncode(note);
        var safeLogoUrl = WebUtility.HtmlEncode(logoUrl);

        return $$"""
            <!DOCTYPE html>
            <html lang="pt-BR">
              <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{safeHeading}}</title>
              </head>
              <body style="margin:0; padding:32px 16px; background:#f7f7f7; font-family:Arial,Helvetica,sans-serif;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">
                  <tr>
                    <td align="center">
                      <table role="presentation" width="480" cellpadding="0" cellspacing="0" border="0"
                             style="max-width:480px; width:100%; background:#ffffff; border-radius:12px; overflow:hidden;">
                        <tr>
                          <td style="background:#ab090a; padding:28px 32px;">
                            <img src="{{safeLogoUrl}}" alt="APROVEC Brasil" width="168" height="44" style="display:block; border:0;">
                            <div style="font-family:Arial,Helvetica,sans-serif; font-size:10px; letter-spacing:0.12em; color:#f2c4c2; text-transform:uppercase; margin-top:10px;">Recorrência comercial</div>
                          </td>
                        </tr>
                        <tr>
                          <td style="padding:36px 32px 8px;">
                            <h1 style="margin:0 0 16px; font-family:Arial,Helvetica,sans-serif; font-size:20px; color:#0c0c0e;">{{safeHeading}}</h1>
                            <p style="margin:0 0 28px; font-family:Arial,Helvetica,sans-serif; font-size:14px; line-height:1.6; color:#3f3f42;">{{safeIntro}}</p>
                          </td>
                        </tr>
                        <tr>
                          <td style="padding:0 32px;">
                            <table role="presentation" cellpadding="0" cellspacing="0" border="0">
                              <tr>
                                <td style="border-radius:8px; background:#ab090a;">
                                  <a href="{{safeUrl}}" style="display:inline-block; padding:13px 28px; font-family:Arial,Helvetica,sans-serif; font-size:14px; font-weight:bold; color:#ffffff; text-decoration:none;">{{safeCta}}</a>
                                </td>
                              </tr>
                            </table>
                          </td>
                        </tr>
                        <tr>
                          <td style="padding:24px 32px 8px;">
                            <p style="margin:0; font-family:Arial,Helvetica,sans-serif; font-size:12px; line-height:1.6; color:#8d7e83;">Se o botão não funcionar, copie e cole este link no navegador:<br><a href="{{safeUrl}}" style="color:#ab090a; word-break:break-all;">{{safeUrl}}</a></p>
                          </td>
                        </tr>
                        <tr>
                          <td style="padding:8px 32px 32px;">
                            <p style="margin:0; font-family:Arial,Helvetica,sans-serif; font-size:12px; line-height:1.6; color:#8d7e83;">{{safeNote}}</p>
                          </td>
                        </tr>
                        <tr>
                          <td style="padding:20px 32px; border-top:1px solid #ece5e5;">
                            <p style="margin:0; font-family:Arial,Helvetica,sans-serif; font-size:11px; color:#a89ba0;">APROVEC Brasil · Recorrência comercial</p>
                          </td>
                        </tr>
                      </table>
                    </td>
                  </tr>
                </table>
              </body>
            </html>
            """;
    }
}
