using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Npgsql;
using Recorrencia.Api;
using Recorrencia.Api.Auth;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Carteira;
using Recorrencia.Api.Cli;
using Recorrencia.Api.Commissions;
using Recorrencia.Api.Email;
using Recorrencia.Api.Fechamentos;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Integracoes;
using Recorrencia.Api.Platform;
using Recorrencia.Api.Roles;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;
using Recorrencia.Api.Users;
using Recorrencia.Db;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<AuthOptions>().BindConfiguration("Auth");
builder.Services.AddOptions<Argon2Options>().BindConfiguration("Argon2");
builder.Services.AddOptions<WebOptions>().BindConfiguration("Web");
builder.Services.AddOptions<EmailOptions>().BindConfiguration("Email");
builder.Services.AddOptions<InternalOptions>().BindConfiguration("Internal")
    .Validate(o => o.Key.Length >= 32, "Internal:Key precisa ter pelo menos 32 caracteres.")
    .ValidateOnStart();

builder.Services.AddOptions<HinovaOptions>().BindConfiguration("Hinova")
    .Validate(o =>
    {
        try { return Convert.FromBase64String(o.EncryptionKey).Length == 32; }
        catch (FormatException) { return false; }
    }, "Hinova:EncryptionKey precisa ser uma chave base64 de 32 bytes.")
    .ValidateOnStart();

DapperSetup.Configure();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new DataSources(
        NpgsqlDataSource.Create(RequiredSetting(config, "ConnectionStrings:App")),
        NpgsqlDataSource.Create(RequiredSetting(config, "ConnectionStrings:Superadmin")));
});
builder.Services.AddSingleton<Database>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));

// Unconditional (the framework default only sets this in Development): a malformed body or
// a missing required parameter must always throw BadHttpRequestException so it's caught by
// ErrorHandlingMiddleware and shaped as Problem Details (400 request.invalid), not fall
// through to a bare, content-type-less 400 outside Development.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<LoginThrottle>();

builder.Services.AddSingleton<AesGcmCipher>();
if (builder.Configuration.GetValue<bool>("Hinova:UseFake"))
{
    builder.Services.AddSingleton<IHinovaClient, DevFakeHinovaClient>();
}
else
{
    builder.Services.AddHttpClient<IHinovaClient, HinovaClient>(c =>
        c.BaseAddress = new Uri("https://api.hinova.com.br/api/sga/v2/"));
}

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<TenantResolver>();
builder.Services.AddScoped<RequestContext>();
builder.Services.AddScoped<CurrentPermissions>();

builder.Services.AddSingleton<LinkBuilder>();
builder.Services.AddSingleton<IEmailSender>(sp =>
    string.IsNullOrEmpty(sp.GetRequiredService<IOptions<EmailOptions>>().Value.OutboxDir)
        ? ActivatorUtilities.CreateInstance<LogEmailSender>(sp)
        : ActivatorUtilities.CreateInstance<FileEmailSender>(sp));

var app = builder.Build();

if (args is ["seed-dev"])
{
    await DevSeed.RunAsync(app.Services);
    return;
}

if (args is ["create-platform-admin", var platformAdminEmail])
{
    await PlatformAdmins.RunFromCommandLineAsync(app.Services, platformAdminEmail);
    return;
}

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<InternalKeyMiddleware>();
app.UseMiddleware<HostContextMiddleware>();
app.UseMiddleware<SessionMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapTenantEndpoints();
app.MapAuthEndpoints();
app.MapMeEndpoints();
app.MapPasswordEndpoints();
app.MapUserEndpoints();
app.MapRoleEndpoints();
app.MapCommissionPlanEndpoints();
app.MapCommissionEndpoints();
app.MapFechamentoEndpoints();
app.MapCarteiraEndpoints();
app.MapPlatformEndpoints();
app.MapHinovaEndpoints();

app.Run();

static string RequiredSetting(IConfiguration config, string key) =>
    config[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Configuração {key} ausente.");

public partial class Program { }
