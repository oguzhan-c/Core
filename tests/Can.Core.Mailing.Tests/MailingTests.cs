using System.Text;
using Can.Core.Mailing.MailKit;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace Can.Core.Mailing.Tests;

public class MailingTests
{
    private static EmailMessage SampleMessage()
    {
        var message = new EmailMessage("Doğrulama kodun")
        {
            HtmlBody = "<p>Kodun: <b>123456</b></p>",
            TextBody = "Kodun: 123456",
        };
        message.To.Add(new EmailAddress("ada@test.local", "Ada Lovelace"));
        message.Bcc.Add(new EmailAddress("arsiv@test.local"));
        message.Attachments.Add(new EmailAttachment("not.txt", Encoding.UTF8.GetBytes("merhaba"), "text/plain"));
        return message;
    }

    [Fact]
    public void Mime_message_contains_addresses_subject_bodies_and_attachments()
    {
        using MimeMessage mime = MimeMessageFactory.Create(SampleMessage(), new EmailAddress("no-reply@test.local", "Can App"));

        MailboxAddress from = mime.From.Mailboxes.Single();
        Assert.Equal("Can App", from.Name);
        Assert.Equal("no-reply@test.local", from.Address);
        Assert.Equal("ada@test.local", mime.To.Mailboxes.Single().Address);
        Assert.Equal("arsiv@test.local", mime.Bcc.Mailboxes.Single().Address);
        Assert.Equal("Doğrulama kodun", mime.Subject);
        Assert.Contains("123456", mime.HtmlBody);
        Assert.Contains("123456", mime.TextBody);
        Assert.Equal("not.txt", Assert.Single(mime.Attachments).ContentDisposition?.FileName);
    }

    [Fact]
    public void Message_from_overrides_default_sender()
    {
        EmailMessage message = SampleMessage();
        message.From = new EmailAddress("destek@test.local");

        using MimeMessage mime = MimeMessageFactory.Create(message, new EmailAddress("no-reply@test.local"));

        Assert.Equal("destek@test.local", mime.From.Mailboxes.Single().Address);
    }

    [Theory]
    [InlineData("no-recipient")]
    [InlineData("no-body")]
    [InlineData("bad-address")]
    public void Invalid_messages_are_rejected(string problem)
    {
        var message = new EmailMessage("Konu") { TextBody = "gövde" };
        message.To.Add(new EmailAddress("ada@test.local"));

        switch (problem)
        {
            case "no-recipient":
                message.To.Clear();
                break;
            case "no-body":
                message.TextBody = null;
                break;
            case "bad-address":
                message.To.Add(new EmailAddress("adres-degil"));
                break;
        }

        Assert.Throws<ArgumentException>(message.Validate);
    }

    [Fact]
    public async Task Pickup_directory_sender_writes_eml_file()
    {
        string directory = Path.Combine(Path.GetTempPath(), "can-mail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sender = new PickupDirectoryEmailSender(directory, new EmailAddress("no-reply@test.local"));

            await sender.SendAsync(SampleMessage());

            string file = Assert.Single(Directory.GetFiles(directory, "*.eml"));
            using MimeMessage written = await MimeMessage.LoadAsync(file);
            Assert.Equal("Doğrulama kodun", written.Subject);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task In_memory_sender_records_messages()
    {
        await using ServiceProvider provider = new ServiceCollection().AddCanInMemoryEmail().BuildServiceProvider();

        await provider.GetRequiredService<IEmailSender>().SendAsync(SampleMessage());

        EmailMessage sent = Assert.Single(provider.GetRequiredService<InMemoryEmailSender>().Sent);
        Assert.Equal("ada@test.local", sent.To[0].Address);
    }

    [Fact]
    public void MailKit_registration_validates_options_and_replaces_previous_sender()
    {
        var services = new ServiceCollection().AddCanInMemoryEmail();

        Assert.Throws<InvalidOperationException>(() => services.AddCanMailKit(o => o.Host = "smtp.test.local"));

        services.AddCanMailKit(o =>
        {
            o.Host = "smtp.test.local";
            o.FromAddress = "no-reply@test.local";
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<MailKitEmailSender>(provider.GetRequiredService<IEmailSender>());
    }
}
