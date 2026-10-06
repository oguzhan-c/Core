using System.Net;
using System.Text;
using System.Text.Json;
using Can.Core.Mailing.SendGrid;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Mailing.Tests;

public class SendGridTests
{
    private static SendGridOptions Options(bool sandbox = false) =>
        new()
        {
            ApiKey = "SG.test-key",
            FromAddress = "no-reply@test.local",
            FromName = "Can App",
            SandboxMode = sandbox,
            BaseAddress = new Uri("https://sendgrid.test/"),
        };

    private static EmailMessage SampleMessage()
    {
        var message = new EmailMessage("Doğrulama kodun") { HtmlBody = "<p>123456</p>", TextBody = "123456" };
        message.To.Add(new EmailAddress("ada@test.local", "Ada Lovelace"));
        message.Cc.Add(new EmailAddress("cc@test.local"));
        message.Bcc.Add(new EmailAddress("arsiv@test.local"));
        message.ReplyTo.Add(new EmailAddress("destek@test.local", "Destek"));
        message.Attachments.Add(new EmailAttachment("not.txt", Encoding.UTF8.GetBytes("merhaba"), "text/plain"));
        return message;
    }

    [Fact]
    public async Task Posts_snake_case_payload_with_bearer_token()
    {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        var sender = new SendGridEmailSender(new HttpClient(handler), Options());

        await sender.SendAsync(SampleMessage());

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://sendgrid.test/v3/mail/send", handler.Uri?.ToString());
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("SG.test-key", handler.Authorization?.Parameter);

        using JsonDocument doc = JsonDocument.Parse(handler.Body!);
        JsonElement root = doc.RootElement;

        JsonElement personalization = root.GetProperty("personalizations")[0];
        Assert.Equal("ada@test.local", personalization.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal("Ada Lovelace", personalization.GetProperty("to")[0].GetProperty("name").GetString());
        Assert.Equal("cc@test.local", personalization.GetProperty("cc")[0].GetProperty("email").GetString());
        Assert.False(personalization.GetProperty("cc")[0].TryGetProperty("name", out _));
        Assert.Equal("arsiv@test.local", personalization.GetProperty("bcc")[0].GetProperty("email").GetString());

        Assert.Equal("no-reply@test.local", root.GetProperty("from").GetProperty("email").GetString());
        Assert.Equal("Can App", root.GetProperty("from").GetProperty("name").GetString());
        Assert.Equal("destek@test.local", root.GetProperty("reply_to_list")[0].GetProperty("email").GetString());
        Assert.Equal("Doğrulama kodun", root.GetProperty("subject").GetString());

        JsonElement content = root.GetProperty("content");
        Assert.Equal("text/plain", content[0].GetProperty("type").GetString());
        Assert.Equal("text/html", content[1].GetProperty("type").GetString());

        JsonElement attachment = root.GetProperty("attachments")[0];
        Assert.Equal("not.txt", attachment.GetProperty("filename").GetString());
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("merhaba")), attachment.GetProperty("content").GetString());

        Assert.False(root.TryGetProperty("mail_settings", out _));
    }

    [Fact]
    public async Task Sandbox_mode_adds_mail_settings_and_message_from_overrides_default()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var sender = new SendGridEmailSender(new HttpClient(handler), Options(sandbox: true));
        EmailMessage message = SampleMessage();
        message.From = new EmailAddress("satis@test.local");

        await sender.SendAsync(message);

        using JsonDocument doc = JsonDocument.Parse(handler.Body!);
        Assert.True(doc.RootElement.GetProperty("mail_settings").GetProperty("sandbox_mode").GetProperty("enable").GetBoolean());
        Assert.Equal("satis@test.local", doc.RootElement.GetProperty("from").GetProperty("email").GetString());
    }

    [Fact]
    public async Task Non_success_response_throws_with_status_and_body()
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden, """{"errors":[{"message":"sender not verified"}]}""");
        var sender = new SendGridEmailSender(new HttpClient(handler), Options());

        SendGridException exception = await Assert.ThrowsAsync<SendGridException>(() => sender.SendAsync(SampleMessage()));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("sender not verified", exception.ResponseBody);
    }

    [Fact]
    public async Task Requires_at_least_one_to_recipient()
    {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        var sender = new SendGridEmailSender(new HttpClient(handler), Options());
        var message = new EmailMessage("Konu") { TextBody = "x" };
        message.Bcc.Add(new EmailAddress("arsiv@test.local"));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => sender.SendAsync(message));
        Assert.Null(handler.Uri);
    }

    [Fact]
    public void Missing_api_key_fails_at_registration()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddCanSendGrid(o => o.FromAddress = "no-reply@test.local"));
    }

    [Fact]
    public void Registers_sendgrid_as_email_sender()
    {
        var services = new ServiceCollection();
        services.AddCanInMemoryEmail();
        services.AddCanSendGrid(o =>
        {
            o.ApiKey = "SG.test-key";
            o.FromAddress = "no-reply@test.local";
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<SendGridEmailSender>(provider.GetRequiredService<IEmailSender>());
    }

    private sealed class RecordingHandler(HttpStatusCode status, string responseBody = "") : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }
}
