using System.Security.Cryptography;
using System.Text;

namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class AuthFunctionTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    public sealed class SessionRow
    {
        public Guid SessionId { get; set; }
        public string Kind { get; set; } = "";
        public Guid? TenantId { get; set; }
        public Guid? UserId { get; set; }
        public Guid? PlatformAdminId { get; set; }
    }

    private static byte[] H(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private Task<Guid> CreateSessionAsync(Guid tenant, Guid user, string token, int idle = 3600, int absolute = 86400) =>
        db.AsAppUserAsync(null, null, (c, t) => c.ExecuteScalarAsync<Guid>(
            "select app.create_session(@hash, @tenant, @user, @idle, @absolute, @ip, @ua)",
            new { hash = H(token), tenant, user, idle, absolute, ip = "203.0.113.7", ua = "teste" }, t));

    private Task<SessionRow?> ResolveAsync(string token, int idle = 3600) =>
        db.AsAppUserAsync(null, null, (c, t) => c.QuerySingleOrDefaultAsync<SessionRow>(
            "select session_id, kind, tenant_id, user_id, platform_admin_id from app.resolve_session(@hash, @idle)",
            new { hash = H(token), idle }, t));

    private Task CreateInviteAsync(Guid tenant, Guid user, string token, string purpose = "convite", int ttl = 3600) =>
        db.AsAppUserAsync(tenant, null, (c, t) => c.ExecuteAsync(
            "select app.create_invite(@user, @hash, @purpose, @ttl)",
            new { user, hash = H(token), purpose, ttl }, t));

    private Task<Guid> ConsumeAsync(Guid tenant, string token, string passwordHash = "novo-hash") =>
        db.AsAppUserAsync(null, null, (c, t) => c.ExecuteScalarAsync<Guid>(
            "select app.consume_invite(@tenant, @hash, @passwordHash)",
            new { tenant, hash = H(token), passwordHash }, t));

    [Fact]
    public async Task App_user_cannot_read_auth_tables_directly()
    {
        foreach (var table in new[] { "tenants", "sessions", "invite_tokens", "platform_admins" })
        {
            var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(null, null, (c, t) =>
                c.ExecuteScalarAsync<long>($"select count(*) from {table}", transaction: t)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    [Fact]
    public async Task Resolve_tenant_ignores_case()
    {
        var slug = Seed.UniqueSlug();
        var tenant = await _seed.TenantAsync(slug);

        var row = await db.AsAppUserAsync(null, null, (c, t) => c.QuerySingleAsync<(Guid, string, string)>(
            "select id, name, status from app.resolve_tenant(@s)", new { s = slug.ToUpperInvariant() }, t));

        Assert.Equal((tenant, "Empresa de teste", "ativo"), row);
    }

    [Fact]
    public async Task Find_login_matches_email_ignoring_case()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João", email: "joao@empresa.com");

        var row = await db.AsAppUserAsync(null, null, (c, tx) => c.QuerySingleAsync<(Guid, string, string)>(
            "select user_id, password_hash, status from app.find_login(@t, @e)", new { t, e = "JOAO@empresa.com" }, tx));

        Assert.Equal((u, "hash-de-teste", "ativo"), row);
    }

    [Fact]
    public async Task Session_round_trip_returns_identity()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-1");

        var s = await ResolveAsync("tok-1");

        Assert.NotNull(s);
        Assert.Equal("tenant", s.Kind);
        Assert.Equal(t, s.TenantId);
        Assert.Equal(u, s.UserId);
        Assert.Null(s.PlatformAdminId);
    }

    [Fact]
    public async Task Idle_expired_session_is_rejected_and_deleted()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-idle");
        await _seed.ExecAsync("update sessions set expires_at = now() - interval '1 second' where token_hash = @h", new { h = H("tok-idle") });

        Assert.Null(await ResolveAsync("tok-idle"));
        Assert.Equal(0L, await _seed.ScalarAsync<long>("select count(*) from sessions where token_hash = @h", new { h = H("tok-idle") }));
    }

    [Fact]
    public async Task Sliding_never_passes_the_absolute_limit()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-abs", idle: 3600, absolute: 60);

        await ResolveAsync("tok-abs", idle: 3600);

        Assert.True(await _seed.ScalarAsync<bool>(
            "select expires_at = absolute_expires_at from sessions where token_hash = @h", new { h = H("tok-abs") }));
    }

    [Fact]
    public async Task Session_of_deactivated_user_is_rejected()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-off");
        await _seed.ExecAsync("update users set status = 'desligado' where id = @u", new { u });

        Assert.Null(await ResolveAsync("tok-off"));
    }

    [Fact]
    public async Task Session_of_suspended_tenant_is_rejected()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-susp");
        await _seed.ExecAsync("update tenants set status = 'suspenso' where id = @t", new { t });

        Assert.Null(await ResolveAsync("tok-susp"));
    }

    [Fact]
    public async Task Invited_user_cannot_get_a_session()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Convidado", status: "convidado");

        var ex = await DbExtensions.ThrowsPgAsync(() => CreateSessionAsync(t, u, "tok-inv"));
        Assert.Equal("auth.invalid_user", ex.MessageText);
    }

    [Fact]
    public async Task Revoked_session_no_longer_resolves()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-rev");

        await db.AsAppUserAsync(null, null, (c, tx) => c.ExecuteAsync("select app.revoke_session(@h)", new { h = H("tok-rev") }, tx));

        Assert.Null(await ResolveAsync("tok-rev"));
    }

    [Fact]
    public async Task Invite_activates_user_only_once()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Nova", status: "convidado");
        await CreateInviteAsync(t, u, "conv-1");

        Assert.Equal(u, await ConsumeAsync(t, "conv-1"));
        Assert.Equal("ativo|novo-hash", await _seed.ScalarAsync<string>(
            "select status || '|' || password_hash from users where id = @u", new { u }));

        var ex = await DbExtensions.ThrowsPgAsync(() => ConsumeAsync(t, "conv-1"));
        Assert.Equal("invite.invalid", ex.MessageText);
    }

    [Fact]
    public async Task Expired_invite_is_rejected()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Nova", status: "convidado");
        await CreateInviteAsync(t, u, "conv-exp");
        await _seed.ExecAsync("update invite_tokens set expires_at = now() - interval '1 second' where token_hash = @h", new { h = H("conv-exp") });

        var ex = await DbExtensions.ThrowsPgAsync(() => ConsumeAsync(t, "conv-exp"));
        Assert.Equal("invite.invalid", ex.MessageText);
    }

    [Fact]
    public async Task Invite_cannot_be_consumed_on_another_tenant()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t1, "Nova", status: "convidado");
        await CreateInviteAsync(t1, u, "conv-x");

        var ex = await DbExtensions.ThrowsPgAsync(() => ConsumeAsync(t2, "conv-x"));
        Assert.Equal("invite.invalid", ex.MessageText);
    }

    [Fact]
    public async Task New_invite_replaces_the_previous_unused_one()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Nova", status: "convidado");
        await CreateInviteAsync(t, u, "conv-a");
        await CreateInviteAsync(t, u, "conv-b");

        var ex = await DbExtensions.ThrowsPgAsync(() => ConsumeAsync(t, "conv-a"));
        Assert.Equal("invite.invalid", ex.MessageText);
        Assert.Equal(u, await ConsumeAsync(t, "conv-b"));
    }

    [Fact]
    public async Task Password_reset_requires_an_active_user()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Nova", status: "convidado");

        var ex = await DbExtensions.ThrowsPgAsync(() => CreateInviteAsync(t, u, "reset-1", "redefinicao"));
        Assert.Equal("invite.invalid_status", ex.MessageText);
    }

    [Fact]
    public async Task Creating_an_invite_requires_tenant_context()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Nova", status: "convidado");

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(null, null, (c, tx) => c.ExecuteAsync(
            "select app.create_invite(@u, @h, 'convite', 3600)", new { u, h = H("sem-contexto") }, tx)));
        Assert.Equal("invite.user_not_found", ex.MessageText);
    }

    [Fact]
    public async Task Password_reset_revokes_existing_sessions()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-before-reset");
        await CreateInviteAsync(t, u, "reset-ok", "redefinicao");

        await ConsumeAsync(t, "reset-ok");

        Assert.Null(await ResolveAsync("tok-before-reset"));
    }

    [Fact]
    public async Task Revoke_user_sessions_is_limited_to_the_current_tenant()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t1, "João");
        await CreateSessionAsync(t1, u, "tok-scope");

        var fromOther = await db.AsAppUserAsync(t2, null, (c, tx) => c.ExecuteScalarAsync<int>("select app.revoke_user_sessions(@u)", new { u }, tx));
        Assert.Equal(0, fromOther);
        Assert.NotNull(await ResolveAsync("tok-scope"));

        var fromOwn = await db.AsAppUserAsync(t1, null, (c, tx) => c.ExecuteScalarAsync<int>("select app.revoke_user_sessions(@u)", new { u }, tx));
        Assert.Equal(1, fromOwn);
    }

    [Fact]
    public async Task Rehash_changes_only_the_current_user()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        var other = await _seed.UserAsync(t, "Maria");

        await db.AsAppUserAsync(t, u, (c, tx) => c.ExecuteAsync("select app.rehash_own_password('hash-novo')", transaction: tx));

        Assert.Equal("hash-novo", await _seed.ScalarAsync<string>("select password_hash from users where id = @u", new { u }));
        Assert.Equal("hash-de-teste", await _seed.ScalarAsync<string>("select password_hash from users where id = @other", new { other }));
    }

    [Fact]
    public async Task Platform_session_round_trip()
    {
        var email = $"root-{Guid.NewGuid():N}@plataforma.local";
        var admin = await _seed.ScalarAsync<Guid>(
            "insert into platform_admins (email, password_hash) values (@email, 'hash-root') returning id", new { email });

        var login = await db.AsAppUserAsync(null, null, (c, t) => c.QuerySingleAsync<(Guid, string)>(
            "select admin_id, password_hash from app.find_platform_login(@e)", new { e = email.ToUpperInvariant() }, t));
        Assert.Equal((admin, "hash-root"), login);

        await db.AsAppUserAsync(null, null, (c, t) => c.ExecuteScalarAsync<Guid>(
            "select app.create_platform_session(@hash, @admin, 3600, 86400, null, null)",
            new { hash = H("tok-root"), admin }, t));

        var s = await ResolveAsync("tok-root");
        Assert.NotNull(s);
        Assert.Equal("platform", s.Kind);
        Assert.Equal(admin, s.PlatformAdminId);
        Assert.Null(s.TenantId);
        Assert.Null(s.UserId);
    }
}
