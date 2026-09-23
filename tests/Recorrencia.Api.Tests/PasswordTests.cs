using Microsoft.Extensions.Options;
using Recorrencia.Api.Email;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class PasswordTests(ApiFixture api)
{
    [Fact]
    public async Task Reset_sends_a_link_that_sets_a_new_password()
    {
        var s = await api.SeedAsync();
        var email = $"joao@{s.Slug}.local";
        var client = api.Client(s.Slug);

        await ApiClient.ExpectAsync(await client.PostAsync("/auth/password-reset", new { email }), HttpStatusCode.Accepted);

        var sent = api.Emails.LastTo(email);
        Assert.NotNull(sent);
        Assert.Contains($"http://{s.Slug}.localhost:3000/definir-senha/", sent.Body);

        var token = CapturingEmailSender.ExtractToken(sent.Body);
        var response = await client.PostAsync("/auth/set-password", new { token, password = "nova-senha-segura" });
        await ApiClient.ExpectAsync(response, HttpStatusCode.OK);

        await api.Client(s.Slug).LoginAsync(email, "nova-senha-segura");
        var old = await api.Client(s.Slug).PostAsync("/auth/login", new { email, password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
    }

    [Fact]
    public async Task Reset_for_unknown_email_is_accepted_silently()
    {
        var s = await api.SeedAsync();
        var email = $"ninguem@{s.Slug}.local";

        await ApiClient.ExpectAsync(await api.Client(s.Slug).PostAsync("/auth/password-reset", new { email }), HttpStatusCode.Accepted);

        Assert.Null(api.Emails.LastTo(email));
    }

    [Fact]
    public async Task Weak_password_is_rejected()
    {
        var s = await api.SeedAsync();
        var response = await api.Client(s.Slug).PostAsync("/auth/set-password", new { token = "qualquer", password = "curta" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.weak_password", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Invalid_token_is_rejected()
    {
        var s = await api.SeedAsync();
        var response = await api.Client(s.Slug).PostAsync("/auth/set-password", new { token = "token-inexistente", password = "senha-valida-123" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invite.invalid", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task File_sender_writes_one_file_per_email()
    {
        var dir = Path.Combine(Path.GetTempPath(), "emails-" + Guid.NewGuid().ToString("N"));
        var sender = new FileEmailSender(
            Options.Create(new EmailOptions { OutboxDir = dir }),
            api.Service<Microsoft.Extensions.Hosting.IHostEnvironment>());

        await sender.SendAsync("a@b.c", "Assunto", "Corpo com link", CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(dir));
        var content = await File.ReadAllTextAsync(file);
        Assert.Contains("Para: a@b.c", content);
        Assert.Contains("Corpo com link", content);
    }
}
