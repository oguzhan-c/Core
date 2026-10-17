namespace CanAppTemplate.Infrastructure;

/// <summary>Hangi dış servislerin açık olduğu (WebAPI sağlık kontrolleri ve panel için).</summary>
/// <param name="Redis">Redis bağlantı dizesi (kapalıysa <see langword="null"/>).</param>
/// <param name="BrokerEnabled">Event bus gerçek bir broker'a (RabbitMQ) bağlı mı.</param>
/// <param name="HangfireEnabled">Hangfire kalıcı kuyruğu açık mı.</param>
public sealed record InfrastructureStatus(string? Redis, bool BrokerEnabled, bool HangfireEnabled);
