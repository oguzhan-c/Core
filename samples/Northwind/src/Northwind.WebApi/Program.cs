using System.Text.Json.Serialization;
using Can.Core.BackgroundJobs;
using Can.Core.Logging.Serilog;
using Can.Core.Mailing.MailKit;
using Can.Core.Security.DependencyInjection;
using Can.Core.WebApi.DependencyInjection;
using Northwind.Application;
using Northwind.Application.Features.Products;
using Northwind.Infrastructure;
using Northwind.WebApi.Endpoints;
using Scalar.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
IConfiguration config = builder.Configuration;

// ---------------------------------------------------------------- servisler

builder.AddCanSerilog(o => o.ApplicationName = "Northwind");

builder.Services.AddNorthwindInfrastructure(config);
builder.Services.AddNorthwindApplication(o => config.GetSection("Notifications").Bind(o));

builder.Services.AddCanSecurity(o =>
{
    config.GetSection("Security:Jwt").Bind(o.Jwt);
    o.VerificationCodeKey = config["Security:VerificationCodeKey"]; // e-posta doğrulama kodları
});
builder.Services.AddCanWebApi();
builder.Services.AddCanJwtAuthentication(o => config.GetSection("Security:Cookies").Bind(o));
builder.Services.AddAuthorization();

// Geliştirmede e-postalar klasöre .eml olarak yazılır; diğer ortamlarda SMTP.
if (config["Mail:PickupDirectory"] is { Length: > 0 } pickupDirectory)
    builder.Services.AddCanEmailPickupDirectory(pickupDirectory, config["Mail:From"] ?? "no-reply@northwind.local", "Northwind");
else
    builder.Services.AddCanMailKit(o => config.GetSection("Mail:Smtp").Bind(o));

// Her mağaza için günlük "yeniden sipariş" raporu.
builder.Services.AddCanRecurringJob<ReorderReportJob>(o =>
{
    o.Interval = TimeSpan.FromHours(24);
    o.RunOnStartup = false;
    o.PerTenant = true;
});

builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// ---------------------------------------------------------------- uygulama

WebApplication app = builder.Build();

if (config.GetValue("Database:InitializeOnStartup", true))
    await app.Services.InitializeNorthwindDatabaseAsync();

// İstek özeti en dışta: hata işleyicinin verdiği gerçek durum kodunu (401, 404 ...) loglar.
app.UseCanRequestLogging();
app.UseCanExceptionHandler();

// React uygulaması (ClientApp → npm run build → wwwroot).
app.UseDefaultFiles();
app.UseStaticFiles();

// Geliştirmede http ile çalışılabilsin; diğer ortamlarda her istek https'e yönlendirilir.
if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.UseAuthentication();
app.UseCanTenantResolution();
app.UseCanLogEnrichment(); // loglara UserId / TenantId
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // /openapi/v1.json
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "Northwind API")); // /swagger
    app.MapScalarApiReference(); // /scalar

    if (config["Mail:PickupDirectory"] is { Length: > 0 } mailbox)
        app.MapDevMailbox(mailbox); // /api/dev/mailbox
}

app.MapNorthwindEndpoints(); // /api/...

// API dışındaki adresler (/, /products, /admin ...) React uygulamasının yönlendiricisine bırakılır.
app.MapFallbackToFile("index.html");

await app.RunAsync();

/// <summary>Entegrasyon testleri için (<c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
public partial class Program;
