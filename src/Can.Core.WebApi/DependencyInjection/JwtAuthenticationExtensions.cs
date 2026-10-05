using Can.Core.Security.Tokens;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.DependencyInjection;

public static class JwtAuthenticationExtensions
{
    /// <summary>
    /// <c>Can.Core.Security</c>'nin ürettiği token'ları doğrulayan JWT Bearer authentication'ı ekler
    /// (aynı issuer, audience, imza anahtarı ve algoritma). Önce <c>AddCanSecurity(...)</c> çağrılmalı.
    /// </summary>
    /// <remarks>
    /// Claim adları dönüştürülmez (<c>MapInboundClaims = false</c>): <c>sub</c>, <c>role</c>, <c>tenant_id</c>
    /// olduğu gibi kalır; <c>User.IsInRole</c> ve <c>[Authorize(Roles = ...)]</c> <c>role</c> claim'ini kullanır.
    /// </remarks>
    public static AuthenticationBuilder AddCanJwtAuthentication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AuthenticationBuilder builder = services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<ITokenService>(
                (bearer, tokens) =>
                {
                    bearer.MapInboundClaims = false;
                    bearer.TokenValidationParameters = tokens.CreateValidationParameters();
                }
            );

        return builder;
    }
}
