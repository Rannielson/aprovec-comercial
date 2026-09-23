using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Platform;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class PlatformTests(ApiFixture api)
{
    public sealed record TenantDto(Guid Id, string Slug, string Name, string Status, DateTimeOffset CreatedAt);
    public sealed record PermissionDto(string Key, string? Scope);
    public sealed record MeDto(List<PermissionDto> Permissions);

    private async Task<ApiClient> PlatformClientAsync()
    {
        var email = $"root-{Guid.NewGuid():N}@plataforma.local";
        await PlatformAdmins.CreateAsync(api.Service<DataSources>(), api.Service<PasswordHasher>(), email, ApiFixture.Password);
        var client = api.Client("admin");
        var response = await client.PostAsync("/platform/auth/login", new { email, password = ApiFixture.Password });
        await ApiClient.ExpectAsync(response, HttpStatusCode.OK);
        client.Token = (await response.Content.ReadFromJsonAsync<SessionDto>(ApiClient.Json))!.Token;
        return client;
    }

    [Fact]
    public async Task Platform_admin_lists_tenants()
    {
        var s = await api.SeedAsync();
        var tenants = await (await PlatformClientAsync()).GetJsonAsync<List<TenantDto>>("/platform/tenants");
        Assert.Contains(tenants, t => t.Slug == s.Slug && t.Status == "ativo");
    }

    [Fact]
    public async Task Sessions_do_not_cross_between_platform_and_tenants()
    {
        var s = await api.SeedAsync();
        var platform = await PlatformClientAsync();
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        var tenantWithPlatformToken = api.Client(s.Slug);
        tenantWithPlatformToken.Token = platform.Token;
        Assert.Equal("auth.tenant_mismatch", await ApiClient.CodeAsync(await tenantWithPlatformToken.GetAsync("/me")));

        var platformWithTenantToken = api.Client("admin");
        platformWithTenantToken.Token = joao.Token;
        Assert.Equal("auth.tenant_mismatch", await ApiClient.CodeAsync(await platformWithTenantToken.GetAsync("/platform/tenants")));

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client("admin").GetAsync("/platform/tenants")).StatusCode);
    }

    [Fact]
    public async Task New_tenant_admin_receives_an_invite_and_gets_full_permissions()
    {
        var platform = await PlatformClientAsync();
        var slug = Seed.UniqueSlug();
        var adminEmail = $"dona@{slug}.local";

        var created = await platform.PostAsync("/platform/tenants", new
        {
            slug,
            name = "Nova Associação",
            adminName = "Dona da Empresa",
            adminEmail,
            planTemplate = "aprovec",
            planEffectiveFrom = "2026-10-01",
        });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);

        var sent = api.Emails.LastTo(adminEmail);
        Assert.NotNull(sent);
        Assert.Contains($"http://{slug}.localhost:3000/definir-senha/", sent.Body);

        var admin = api.Client(slug);
        var session = await admin.PostAsync("/auth/set-password", new { token = CapturingEmailSender.ExtractToken(sent.Body), password = "senha-da-dona-1" });
        await ApiClient.ExpectAsync(session, HttpStatusCode.OK);
        admin.Token = (await session.Content.ReadFromJsonAsync<SessionDto>(ApiClient.Json))!.Token;

        var me = await admin.GetJsonAsync<MeDto>("/me");
        Assert.Equal(14, me.Permissions.Count);
    }

    [Theory]
    [InlineData("Maiuscula", HttpStatusCode.BadRequest, "tenant.invalid_slug")]
    [InlineData("admin", HttpStatusCode.BadRequest, "tenant.invalid_slug")]
    public async Task Invalid_slugs_are_rejected(string slug, HttpStatusCode status, string code)
    {
        var response = await (await PlatformClientAsync()).PostAsync("/platform/tenants",
            new { slug, name = "X", adminName = "X", adminEmail = "x@x.local" });
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Duplicate_slug_is_rejected()
    {
        var s = await api.SeedAsync();
        var response = await (await PlatformClientAsync()).PostAsync("/platform/tenants",
            new { slug = s.Slug, name = "X", adminName = "X", adminEmail = "x@x.local" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("tenant.slug_taken", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Suspended_tenant_disappears_until_reactivated()
    {
        var s = await api.SeedAsync();
        var platform = await PlatformClientAsync();

        await ApiClient.ExpectAsync(await platform.PostAsync($"/platform/tenants/{s.TenantId}/status", new { status = "suspenso" }), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client(s.Slug).GetAsync("/tenant")).StatusCode);

        await ApiClient.ExpectAsync(await platform.PostAsync($"/platform/tenants/{s.TenantId}/status", new { status = "ativo" }), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.OK, (await api.Client(s.Slug).GetAsync("/tenant")).StatusCode);
    }
}
