namespace Can.Core.Security;

/// <summary>
/// Veritabanında şifreli saklanacak gizli değerler için (TOTP anahtarı). ASP.NET Data Protection uygulaması
/// <c>Can.Core.WebApi</c>'de (<c>AddCanSecretProtection()</c>); EF Core tarafı
/// <c>ApplyCanSecurityModel(o =&gt; o.SecretProtector = ...)</c>.
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] value);

    byte[] Unprotect(byte[] value);
}
