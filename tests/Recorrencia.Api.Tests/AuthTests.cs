using Microsoft.Extensions.Options;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class AuthTests(ApiFixture api)
{
    public sealed record PermissionDto(string Key, string? Scope);
    public sealed record TenantDto(Guid Id, string Slug, string Name);
    public sealed record MeDto(Guid Id, string Name, string Email, TenantDto Tenant, List<PermissionDto> Permissions, List<string> Modules);

    [Fact]
    public async Task Requests_without_the_internal_key_look_like_not_found()
    {
        var s = await api.SeedAsync();
        var client = api.Client(s.Slug);

        var withoutKey = await client.SendAsync(HttpMethod.Get, "/me", withKey: false);
        Assert.Equal(HttpStatusCode.NotFound, withoutKey.StatusCode);

        var withKey = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.Unauthorized, withKey.StatusCode);
        Assert.Equal("auth.unauthenticated", await ApiClient.CodeAsync(withKey));
    }

    [Fact]
    public async Task Login_returns_a_session_and_me_describes_the_user()
    {
        var s = await api.SeedAsync();
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"JOAO@{s.Slug}.local", ApiFixture.Password);

        var me = await joao.GetJsonAsync<MeDto>("/me");

        Assert.Equal(("João Silva", s.Slug), (me.Name, me.Tenant.Slug));
        Assert.Contains(new PermissionDto("carteira.visualizar", "own"), me.Permissions);
        Assert.Contains(new PermissionDto("estrutura.visualizar", "direct"), me.Permissions);
        Assert.Equal(6, me.Modules.Count);
    }

    [Theory]
    [InlineData("joao", "senha-errada-123")]
    [InlineData("ninguem", ApiFixture.Password)]
    public async Task Wrong_credentials_get_the_same_answer(string login, string password)
    {
        var s = await api.SeedAsync();
        var response = await api.Client(s.Slug).PostAsync("/auth/login", new { email = $"{login}@{s.Slug}.local", password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.invalid_credentials", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Invited_user_cannot_log_in()
    {
        var s = await api.SeedAsync();
        await api.SqlAsync("update users set status = 'convidado' where id = @id", new { id = s.Maria });

        var response = await api.Client(s.Slug).PostAsync("/auth/login", new { email = $"maria@{s.Slug}.local", password = ApiFixture.Password });
        Assert.Equal("auth.invalid_credentials", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Too_many_failures_block_even_the_right_password()
    {
        var s = await api.SeedAsync();
        var client = api.Client(s.Slug);
        for (var i = 0; i < 5; i++)
            await client.PostAsync("/auth/login", new { email = $"joao@{s.Slug}.local", password = "senha-errada-123" });

        var response = await client.PostAsync("/auth/login", new { email = $"joao@{s.Slug}.local", password = ApiFixture.Password });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("auth.too_many_attempts", await ApiClient.CodeAsync(response));
    }

    // A missing/unparseable X-Client-Ip must not collapse into one shared "ip:" throttle
    // bucket -- otherwise failed attempts against one tenant (here simulated by an
    // unparseable IP, since ApiClient always sends the header but HostContextMiddleware nulls
    // out ClientIp when it can't be parsed as an address) would lock out a completely
    // unrelated tenant's legitimate login.
    [Fact]
    public async Task Missing_client_ip_does_not_share_a_throttle_bucket_across_tenants()
    {
        var a = await api.SeedAsync();
        var b = await api.SeedAsync();
        var attacker = api.Client(a.Slug, ip: "not-an-ip");
        for (var i = 0; i < 5; i++)
            await attacker.PostAsync("/auth/login", new { email = $"joao@{a.Slug}.local", password = "senha-errada-123" });

        // Sanity check: tenant A's own throttle really did engage.
        var blockedOnA = await attacker.PostAsync("/auth/login", new { email = $"joao@{a.Slug}.local", password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, blockedOnA.StatusCode);

        var victim = api.Client(b.Slug, ip: "not-an-ip");
        var response = await victim.PostAsync("/auth/login", new { email = $"joao@{b.Slug}.local", password = ApiFixture.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Session_cannot_be_used_on_another_tenant()
    {
        var a = await api.SeedAsync();
        var b = await api.SeedAsync();
        var joao = api.Client(a.Slug);
        await joao.LoginAsync($"joao@{a.Slug}.local", ApiFixture.Password);

        var other = api.Client(b.Slug);
        other.Token = joao.Token;
        var response = await other.GetAsync("/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.tenant_mismatch", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Logout_revokes_the_session()
    {
        var s = await api.SeedAsync();
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        await ApiClient.ExpectAsync(await joao.PostAsync("/auth/logout"), HttpStatusCode.NoContent);

        Assert.Equal(HttpStatusCode.Unauthorized, (await joao.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Login_upgrades_hashes_made_with_old_parameters()
    {
        var s = await api.SeedAsync();
        var oldHasher = new PasswordHasher(Options.Create(new Argon2Options { MemoryKb = 2048, Iterations = 1, Parallelism = 1 }));
        await api.SqlAsync("update users set password_hash = @hash where id = @id", new { hash = oldHasher.Hash(ApiFixture.Password), id = s.Joao });

        await api.Client(s.Slug).LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        var stored = await api.SqlScalarAsync<string>("select password_hash from users where id = @id", new { id = s.Joao });
        Assert.StartsWith("$argon2id$v=19$m=1024,t=1,p=1$", stored);
    }
}

public class PermissionSetTests
{
    private static readonly PermissionSet Set = new(new Dictionary<string, string?>
    {
        ["carteira.visualizar"] = "direct",
        ["usuarios.convidar"] = null,
    });

    [Theory]
    [InlineData("carteira.visualizar", "own", true)]
    [InlineData("carteira.visualizar", "direct", true)]
    [InlineData("carteira.visualizar", "subtree", false)]
    [InlineData("carteira.visualizar", "tenant", false)]
    [InlineData("usuarios.convidar", null, true)]
    [InlineData("usuarios.desligar", null, false)]
    public void Can_grant_only_what_it_has(string key, string? scope, bool expected)
    {
        Assert.Equal(expected, Set.CanGrant(key, scope));
    }

    // Scopes.Rank returns -1 both for "held with no scope" and for "not a
    // real scope at all", so a naive rank comparison treats garbage scope
    // strings as equivalent to an unscoped grant and wrongly allows them.
    // CanGrant must reject a scope string that isn't own/direct/subtree/
    // tenant/null outright, before any rank comparison, regardless of
    // whether the permission it's checked against is held scoped or
    // unscoped.
    [Theory]
    [InlineData("carteira.visualizar", "bogus")]
    [InlineData("usuarios.convidar", "bogus")]
    public void Rejects_scope_strings_that_are_not_real_scopes(string key, string? scope)
    {
        Assert.False(Set.CanGrant(key, scope));
    }

    [Fact]
    public void Exposes_grants_in_key_order()
    {
        Assert.Equal(
            new[] { new PermissionGrant("carteira.visualizar", "direct"), new PermissionGrant("usuarios.convidar", null) },
            Set.All);
    }
}
