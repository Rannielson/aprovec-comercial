using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recorrencia.Api.Tests;

public sealed record SessionDto(string Token, DateTimeOffset AbsoluteExpiresAt);

public sealed record ProblemDto(int Status, string Code, string? CorrelationId);

public sealed class ApiClient(HttpClient http, string host, string? ip = null)
{
    private static int _nextIp;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public string Ip { get; } = ip ?? NextIp();
    public string? Token { get; set; }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, bool withKey = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (withKey)
            request.Headers.Add("X-Internal-Key", ApiFixture.InternalKey);
        request.Headers.Add("X-Tenant-Host", host);
        request.Headers.Add("X-Client-Ip", Ip);
        request.Headers.Add("X-Client-User-Agent", "testes");
        if (Token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        return await http.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);
    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body ?? new { });
    public Task<HttpResponseMessage> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, body);
    public Task<HttpResponseMessage> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path);

    public async Task<T> GetJsonAsync<T>(string path)
    {
        var response = await GetAsync(path);
        await ExpectAsync(response, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public async Task LoginAsync(string email, string password)
    {
        var response = await PostAsync("/auth/login", new { email, password });
        await ExpectAsync(response, HttpStatusCode.OK);
        Token = (await response.Content.ReadFromJsonAsync<SessionDto>(Json))!.Token;
    }

    public static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        if (response.StatusCode != status)
            Assert.Fail($"Esperado {(int)status}, recebido {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    public static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))?.Code;

    private static string NextIp()
    {
        var n = Interlocked.Increment(ref _nextIp);
        return $"10.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}";
    }
}
