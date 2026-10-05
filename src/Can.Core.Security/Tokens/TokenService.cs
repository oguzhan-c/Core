using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Can.Core.Security.Tokens;

/// <summary>Token'a yazılacak kullanıcı bilgisi.</summary>
/// <param name="UserId">Kullanıcı kimliği (<c>sub</c>).</param>
/// <param name="UserName">Kullanıcı adı (<c>name</c>).</param>
/// <param name="Email">E-posta (<c>email</c>).</param>
/// <param name="Roles">Roller (<c>role</c>).</param>
/// <param name="TenantId">
/// Oturumun AKTİF tenant'ı (<c>tenant_id</c>). Tenant bilgisi yalnızca imzalı token'dan okunur; kullanıcı tenant
/// değiştirmek isterse sunucu üyeliği kontrol edip o tenant için yeni token üretir.
/// </param>
/// <param name="AdditionalClaims">Ek claim'ler.</param>
public sealed record TokenSubject(
    string UserId,
    string? UserName = null,
    string? Email = null,
    IReadOnlyCollection<string>? Roles = null,
    string? TenantId = null,
    IReadOnlyCollection<Claim>? AdditionalClaims = null);

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Yeni refresh token. <see cref="Token"/> yalnızca istemciye bir kez verilir; veritabanında
/// <see cref="TokenHash"/> saklanır (veritabanı sızsa bile token'lar kullanılamaz).
/// </summary>
public sealed record RefreshTokenValue(string Token, string TokenHash, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    AccessToken CreateAccessToken(TokenSubject subject);

    RefreshTokenValue CreateRefreshToken();

    /// <summary>İstemciden gelen refresh token'ı veritabanındaki hash ile karşılaştırmak için.</summary>
    string HashRefreshToken(string refreshToken);

    /// <summary>JwtBearer'ın token'ları doğrulaması için kullanılan parametreler (WebApi paketi kullanır).</summary>
    TokenValidationParameters CreateValidationParameters();
}

/// <summary>
/// HMAC-SHA256 imzalı JWT access token ve kriptografik olarak rastgele refresh token üretir.
/// </summary>
/// <remarks>
/// Claim adları standart JWT adlarıdır: <c>sub</c>, <c>name</c>, <c>email</c>, <c>role</c>,
/// <c>tenant_id</c>, <c>jti</c>. Can.Core.WebApi'deki <c>HttpCurrentUser</c> bunları doğrudan okur.
/// </remarks>
public sealed class TokenService : ITokenService
{
    public const string RoleClaimType = "role";
    public const string TenantClaimType = "tenant_id";

    private readonly JwtOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly JsonWebTokenHandler _handler = new();

    public TokenService(JwtOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _timeProvider = timeProvider;
        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
    }

    public AccessToken CreateAccessToken(TokenSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject.UserId);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset expiresAt = now.Add(_options.AccessTokenLifetime);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject.UserId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };

        if (!string.IsNullOrWhiteSpace(subject.UserName))
            claims.Add(new Claim(JwtRegisteredClaimNames.Name, subject.UserName));

        if (!string.IsNullOrWhiteSpace(subject.Email))
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, subject.Email));

        foreach (string role in subject.Roles ?? [])
            claims.Add(new Claim(RoleClaimType, role));

        if (!string.IsNullOrWhiteSpace(subject.TenantId))
            claims.Add(new Claim(TenantClaimType, subject.TenantId));

        if (subject.AdditionalClaims is not null)
            claims.AddRange(subject.AdditionalClaims);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256),
        };

        return new AccessToken(_handler.CreateToken(descriptor), expiresAt);
    }

    public RefreshTokenValue CreateRefreshToken()
    {
        // 256 bit rastgele değer; URL'de güvenle taşınabilsin diye Base64Url.
        string token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return new RefreshTokenValue(token, HashRefreshToken(token), _timeProvider.GetUtcNow().Add(_options.RefreshTokenLifetime));
    }

    public string HashRefreshToken(string refreshToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        // Refresh token zaten 256 bit rastgele olduğu için tek SHA-256 yeterlidir (şifrelerden farklı olarak).
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
    }

    public TokenValidationParameters CreateValidationParameters() =>
        new()
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _signingKey,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = _options.ClockSkew,
            NameClaimType = JwtRegisteredClaimNames.Name,
            RoleClaimType = RoleClaimType,
        };
}
