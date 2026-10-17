using Can.Core.Security.Entities;

namespace CanAppTemplate.Application.Services.AuthService;

/// <summary>
/// İsteği yapan istemcinin bilgisi (oturum kaydı ve güvenlik olayları için). WebApi doldurur: IP bağlantıdan,
/// User-Agent başlıktan; cihaz kimliği ve adı istemcinin gönderdiği gösterim bilgisidir (güvenlik kararı verilmez).
/// </summary>
public sealed record ClientInfo(string? IpAddress = null, string? UserAgent = null, string? DeviceId = null, string? DeviceName = null)
{
    public static readonly ClientInfo Unknown = new();

    public SessionDevice ToSessionDevice() => new(DeviceId, DeviceName, UserAgent);
}
