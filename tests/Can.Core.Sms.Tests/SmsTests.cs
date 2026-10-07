using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Sms.Tests;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("0532 123 45 67", "+905321234567")]
    [InlineData("(0532) 123-45-67", "+905321234567")]
    [InlineData("532 123 45 67", "+905321234567")]
    [InlineData("+90 532 123 45 67", "+905321234567")]
    [InlineData("0090 532 123 45 67", "+905321234567")]
    [InlineData("905321234567", "+905321234567")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    [InlineData("0212 555 00 00", "+902125550000")]
    public void Normalizes_to_e164(string input, string expected)
    {
        Assert.True(PhoneNumbers.TryNormalize(input, "90", out string e164));
        Assert.Equal(expected, e164);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0532 12A 45 67")]
    [InlineData("123")]
    [InlineData("+1234567890123456")]
    [InlineData("+0532123456")]
    public void Rejects_invalid_numbers(string? input) => Assert.False(PhoneNumbers.TryNormalize(input, "90", out _));

    [Fact]
    public void Masks_numbers_for_logs() => Assert.Equal("+90532*****67", PhoneNumbers.Mask("+905321234567"));
}

public class SmsTextTests
{
    [Theory]
    [InlineData(160, 1)]
    [InlineData(161, 2)]
    [InlineData(306, 2)]
    [InlineData(307, 3)]
    public void Gsm7_segments(int length, int segments)
    {
        SmsTextInfo info = SmsText.Analyze(new string('a', length));
        Assert.Equal(SmsEncoding.Gsm7, info.Encoding);
        Assert.Equal(segments, info.Segments);
    }

    [Fact]
    public void Extension_characters_count_double()
    {
        SmsTextInfo info = SmsText.Analyze("Fiyat: 10€ [indirim]");
        Assert.Equal(SmsEncoding.Gsm7, info.Encoding);
        Assert.Equal(20 + 3, info.Length); // €, [ ve ] ikişer
    }

    [Theory]
    [InlineData(70, 1)]
    [InlineData(71, 2)]
    [InlineData(134, 2)]
    [InlineData(135, 3)]
    public void Turkish_letters_switch_to_ucs2(int length, int segments)
    {
        SmsTextInfo info = SmsText.Analyze("ğ" + new string('a', length - 1));
        Assert.Equal(SmsEncoding.Ucs2, info.Encoding);
        Assert.Equal(segments, info.Segments);
    }

    [Fact]
    public void Ö_ü_and_Ç_are_gsm_but_ç_ğ_ı_ş_are_not()
    {
        Assert.True(SmsText.IsGsm7("Ödeme Ünvanı Çarşı".Replace("ı", "i").Replace("ş", "s")));
        Assert.False(SmsText.IsGsm7("çay"));
        Assert.False(SmsText.IsGsm7("ağ"));
        Assert.False(SmsText.IsGsm7("kırmızı"));
        Assert.False(SmsText.IsGsm7("şeker"));
    }

    [Fact]
    public void Transliteration_makes_turkish_text_gsm()
    {
        string text = SmsText.ToGsm("Doğrulama kodun: 123456. Şifreni kimseyle paylaşma – İyi günler…");

        Assert.Equal("Dogrulama kodun: 123456. Sifreni kimseyle paylasma - Iyi günler...", text);
        Assert.True(SmsText.IsGsm7(text));
    }

    [Fact]
    public void Emoji_stays_and_counts_as_two_units()
    {
        SmsTextInfo info = SmsText.Analyze(SmsText.ToGsm("Siparişin yolda 🚚"));
        Assert.Equal(SmsEncoding.Ucs2, info.Encoding);
        Assert.Equal(18, info.Length); // "Siparisin yolda " 16 + emoji 2
    }
}

public class SmsSenderTests
{
    private static (ServiceProvider Provider, ISmsSender Sender, InMemorySmsProvider Sent) Build(Action<SmsOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddCanSms(configure).UseInMemory();
        ServiceProvider provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<ISmsSender>(), provider.GetRequiredService<InMemorySmsProvider>());
    }

    [Fact]
    public async Task Sends_normalized_request_with_default_sender()
    {
        (ServiceProvider provider, ISmsSender sender, InMemorySmsProvider sent) = Build(o => o.DefaultSender = "NORTHWIND");
        using (provider)
        {
            SmsSendResult result = await sender.SendAsync(new SmsMessage("0532 123 45 67", "  Kodun: 123456  ") { Reference = "otp-1" }, TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.Segments);
            Assert.NotNull(result.MessageId);

            SmsRequest request = Assert.Single(sent.Messages);
            Assert.Equal("+905321234567", request.To);
            Assert.Equal("Kodun: 123456", request.Text);
            Assert.Equal("NORTHWIND", request.From);
            Assert.Equal("otp-1", request.Reference);
            Assert.Equal(SmsEncoding.Gsm7, request.Encoding);
            Assert.Same(request, sent.LastTo("+905321234567"));
        }
    }

    [Fact]
    public async Task Validation_failures_do_not_reach_the_provider()
    {
        (ServiceProvider provider, ISmsSender sender, InMemorySmsProvider sent) = Build(o => o.MaxSegments = 2);
        using (provider)
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            Assert.Equal(SmsErrorCodes.InvalidNumber, (await sender.SendAsync(new SmsMessage("12", "x"), ct)).ErrorCode);
            Assert.Equal(SmsErrorCodes.EmptyText, (await sender.SendAsync(new SmsMessage("05321234567", "   "), ct)).ErrorCode);

            SmsSendResult tooLong = await sender.SendAsync(new SmsMessage("05321234567", new string('ş', 135)), ct);
            Assert.Equal(SmsErrorCodes.TooLong, tooLong.ErrorCode);
            Assert.Equal(3, tooLong.Segments);

            Assert.Empty(sent.Messages);
        }
    }

    [Fact]
    public async Task Transliteration_option_fits_turkish_text_into_one_sms()
    {
        string text = "Doğrulama kodun: 123456. " + new string('ş', 60); // 85 karakter: UCS-2'de 2 SMS

        (ServiceProvider provider, ISmsSender sender, InMemorySmsProvider sent) = Build(o => o.TransliterateToGsm = true);
        using (provider)
        {
            SmsSendResult result = await sender.SendAsync(new SmsMessage("05321234567", text), TestContext.Current.CancellationToken);

            Assert.Equal(1, result.Segments);
            Assert.Equal(SmsEncoding.Gsm7, Assert.Single(sent.Messages).Encoding);
        }
    }

    [Fact]
    public async Task Pickup_directory_writes_a_json_file()
    {
        string directory = Path.Combine(Path.GetTempPath(), "can-sms-" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddCanSms().UsePickupDirectory(directory);
        using ServiceProvider provider = services.BuildServiceProvider();

        try
        {
            SmsSendResult result = await provider.GetRequiredService<ISmsSender>().SendAsync(new SmsMessage("05321234567", "Kodun: 1234"), TestContext.Current.CancellationToken);

            string file = Assert.Single(Directory.GetFiles(directory, "*.sms.json"));
            JsonElement json = JsonDocument.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).RootElement;
            Assert.Equal("+905321234567", json.GetProperty("to").GetString());
            Assert.Equal("Kodun: 1234", json.GetProperty("text").GetString());
            Assert.Equal(result.MessageId, json.GetProperty("id").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}

public class HttpSmsProviderTests
{
    /// <summary>Örnek sağlayıcı: JSON gönderir, <c>{"status":"ok","id":"..."}</c> ya da <c>{"status":"error","code":"...","message":"..."}</c> okur.</summary>
    private sealed class AcmeSmsProvider(HttpClient http) : HttpSmsProvider(http)
    {
        public override string Name => "acme";

        protected override HttpRequestMessage CreateRequest(SmsRequest request) =>
            new(HttpMethod.Post, "https://api.acme.test/v1/sms")
            {
                Content = JsonContent.Create(new { to = request.To, text = request.Text, sender = request.From, reference = request.Reference }),
            };

        protected override async Task<SmsSendResult> ReadResponseAsync(HttpResponseMessage response, SmsRequest request, CancellationToken cancellationToken)
        {
            JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return body.GetProperty("status").GetString() == "ok"
                ? SmsSendResult.Sent(body.GetProperty("id").GetString(), request.Segments)
                : SmsSendResult.Failed(body.GetProperty("code").GetString()!, body.GetProperty("message").GetString()!);
        }
    }

    private sealed class FakeApi(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private static ServiceProvider Build(FakeApi api)
    {
        var services = new ServiceCollection();
        services.AddCanSms(o => o.DefaultSender = "ACME").UseHttpProvider<AcmeSmsProvider>().ConfigurePrimaryHttpMessageHandler(() => api);
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Successful_send_returns_provider_message_id()
    {
        var api = new FakeApi(_ => Json(HttpStatusCode.OK, """{"status":"ok","id":"msg-42"}"""));
        using ServiceProvider provider = Build(api);

        SmsSendResult result = await provider.GetRequiredService<ISmsSender>().SendAsync(new SmsMessage("05321234567", "Merhaba"), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("msg-42", result.MessageId);
        JsonElement sent = JsonDocument.Parse(Assert.Single(api.Bodies)).RootElement;
        Assert.Equal("+905321234567", sent.GetProperty("to").GetString());
        Assert.Equal("ACME", sent.GetProperty("sender").GetString());
    }

    [Fact]
    public async Task Provider_rejection_is_a_failed_result()
    {
        var api = new FakeApi(_ => Json(HttpStatusCode.BadRequest, """{"status":"error","code":"insufficient_credit","message":"Yetersiz kredi"}"""));
        using ServiceProvider provider = Build(api);

        SmsSendResult result = await provider.GetRequiredService<ISmsSender>().SendAsync(new SmsMessage("05321234567", "Merhaba"), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("insufficient_credit", result.ErrorCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Server_errors_throw_retryable_exception(HttpStatusCode status)
    {
        using ServiceProvider provider = Build(new FakeApi(_ => new HttpResponseMessage(status)));

        await Assert.ThrowsAsync<SmsException>(() =>
            provider.GetRequiredService<ISmsSender>().SendAsync(new SmsMessage("05321234567", "Merhaba"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Connection_errors_throw_retryable_exception()
    {
        using ServiceProvider provider = Build(new FakeApi(_ => throw new HttpRequestException("Connection refused")));

        SmsException ex = await Assert.ThrowsAsync<SmsException>(() =>
            provider.GetRequiredService<ISmsSender>().SendAsync(new SmsMessage("05321234567", "Merhaba"), TestContext.Current.CancellationToken));
        Assert.Contains("acme", ex.Message, StringComparison.Ordinal);
    }
}
