using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Recorrencia.Api;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Db;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<AuthOptions>().BindConfiguration("Auth");
builder.Services.AddOptions<Argon2Options>().BindConfiguration("Argon2");
builder.Services.AddOptions<WebOptions>().BindConfiguration("Web");
builder.Services.AddOptions<EmailOptions>().BindConfiguration("Email");
builder.Services.AddOptions<InternalOptions>().BindConfiguration("Internal")
    .Validate(o => o.Key.Length >= 32, "Internal:Key precisa ter pelo menos 32 caracteres.")
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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<LoginThrottle>();

var app = builder.Build();

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<InternalKeyMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

static string RequiredSetting(IConfiguration config, string key) =>
    config[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Configuração {key} ausente.");

public partial class Program { }
