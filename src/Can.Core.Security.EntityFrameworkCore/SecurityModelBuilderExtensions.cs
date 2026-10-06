using Can.Core.Security.Entities;
using Microsoft.EntityFrameworkCore;

namespace Can.Core.Security.EntityFrameworkCore;

public sealed class SecurityModelOptions
{
    /// <summary>Tabloların şeması (ör. <c>"identity"</c>). Boşsa veritabanının varsayılanı.</summary>
    public string? Schema { get; set; }

    /// <summary>Tablo adlarının ön eki (ör. <c>"Auth"</c> → <c>AuthUsers</c>).</summary>
    public string TablePrefix { get; set; } = string.Empty;
}

public static class SecurityModelBuilderExtensions
{
    /// <summary>
    /// Kimlik doğrulama entity'lerinin tablolarını, benzersiz index'lerini ve ilişkilerini yapılandırır.
    /// </summary>
    /// <typeparam name="TUser">Projedeki kullanıcı sınıfı (<see cref="User{TId}"/>'den türetilmiş).</typeparam>
    /// <typeparam name="TId">Tüm kimlik tablolarının anahtar tipi.</typeparam>
    /// <example>
    /// <code>
    /// protected override void ConfigureModel(ModelBuilder modelBuilder)
    /// {
    ///     modelBuilder.ApplyCanSecurityModel&lt;AppUser, Guid&gt;();
    ///     modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    /// }
    /// </code>
    /// </example>
    /// <remarks>
    /// <para>
    /// Yapılandırılan tablolar: Users, Roles, UserRoles, OperationClaims, RoleOperationClaims, UserOperationClaims,
    /// RefreshTokens, OtpAuthenticators, EmailAuthenticators, UserPasskeys.
    /// </para>
    /// <para>
    /// E-posta benzersizliği silinmiş (soft delete) kullanıcıları da kapsar; silinen kullanıcının e-postası tekrar
    /// kullanılacaksa silerken e-postayı değiştir ya da veritabanına özel filtreli index tanımla.
    /// </para>
    /// <para>Kullanıcıya bağlı tüm kayıtlar kullanıcı kalıcı olarak silinince silinir (cascade).</para>
    /// </remarks>
    public static ModelBuilder ApplyCanSecurityModel<TUser, TId>(this ModelBuilder modelBuilder, Action<SecurityModelOptions>? configure = null)
        where TUser : User<TId>
        where TId : notnull, IEquatable<TId>
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var options = new SecurityModelOptions();
        configure?.Invoke(options);
        string Table(string name) => options.TablePrefix + name;

        modelBuilder.Entity<TUser>(b =>
        {
            b.ToTable(Table("Users"), options.Schema);
            b.HasKey(u => u.Id);
            b.Property(u => u.Email).HasMaxLength(256).IsRequired();
            b.Property(u => u.NormalizedEmail).HasMaxLength(256).IsRequired();
            b.Property(u => u.UserName).HasMaxLength(256);
            b.Property(u => u.PasswordHash).HasMaxLength(512);
            b.Property(u => u.SecurityStamp).HasMaxLength(64).IsRequired();
            b.Property(u => u.PasskeyUserHandle).HasMaxLength(64).IsRequired();
            b.Property(u => u.AuthenticatorType).HasConversion<string>().HasMaxLength(16);
            b.Property(u => u.CreatedBy).HasMaxLength(128);
            b.Property(u => u.UpdatedBy).HasMaxLength(128);
            b.Property(u => u.DeletedBy).HasMaxLength(128);

            b.HasIndex(u => u.NormalizedEmail).IsUnique();
            b.HasIndex(u => u.PasskeyUserHandle).IsUnique();

            b.HasMany(u => u.UserRoles).WithOne().HasForeignKey(ur => ur.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(u => u.OperationClaims).WithOne().HasForeignKey(uc => uc.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Role<TId>>(b =>
        {
            b.ToTable(Table("Roles"), options.Schema);
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).HasMaxLength(256).IsRequired();
            b.Property(r => r.NormalizedName).HasMaxLength(256).IsRequired();
            b.Property(r => r.CreatedBy).HasMaxLength(128);
            b.Property(r => r.UpdatedBy).HasMaxLength(128);
            b.HasIndex(r => r.NormalizedName).IsUnique();
            b.HasMany(r => r.OperationClaims).WithOne().HasForeignKey(rc => rc.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OperationClaim<TId>>(b =>
        {
            b.ToTable(Table("OperationClaims"), options.Schema);
            b.HasKey(c => c.Id);
            b.Property(c => c.Name).HasMaxLength(OperationClaim<TId>.NameMaxLength).IsRequired();
            b.Property(c => c.Description).HasMaxLength(512);
            b.Property(c => c.CreatedBy).HasMaxLength(128);
            b.Property(c => c.UpdatedBy).HasMaxLength(128);
            b.HasIndex(c => c.Name).IsUnique();
        });

        modelBuilder.Entity<RoleOperationClaim<TId>>(b =>
        {
            b.ToTable(Table("RoleOperationClaims"), options.Schema);
            b.HasKey(rc => rc.Id);
            b.HasIndex(rc => new { rc.RoleId, rc.OperationClaimId }).IsUnique();
            b.HasOne(rc => rc.OperationClaim).WithMany().HasForeignKey(rc => rc.OperationClaimId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserOperationClaim<TId>>(b =>
        {
            b.ToTable(Table("UserOperationClaims"), options.Schema);
            b.HasKey(uc => uc.Id);
            b.HasIndex(uc => new { uc.UserId, uc.OperationClaimId }).IsUnique();
            b.HasOne(uc => uc.OperationClaim).WithMany().HasForeignKey(uc => uc.OperationClaimId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserRole<TId>>(b =>
        {
            b.ToTable(Table("UserRoles"), options.Schema);
            b.HasKey(ur => ur.Id);
            b.HasIndex(ur => new { ur.UserId, ur.RoleId }).IsUnique();
            b.HasOne(ur => ur.Role).WithMany().HasForeignKey(ur => ur.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshToken<TId>>(b =>
        {
            b.ToTable(Table("RefreshTokens"), options.Schema);
            b.HasKey(t => t.Id);
            b.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
            b.Property(t => t.ReplacedByTokenHash).HasMaxLength(128);
            b.Property(t => t.CreatedByIp).HasMaxLength(64);
            b.Property(t => t.RevokedByIp).HasMaxLength(64);
            b.Property(t => t.RevokedReason).HasMaxLength(256);
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.HasIndex(t => t.UserId);
            b.HasOne<TUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OtpAuthenticator<TId>>(b =>
        {
            b.ToTable(Table("OtpAuthenticators"), options.Schema);
            b.HasKey(a => a.Id);
            b.Property(a => a.SecretKey).HasMaxLength(256).IsRequired();
            b.HasIndex(a => a.UserId).IsUnique();
            b.HasOne<TUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmailAuthenticator<TId>>(b =>
        {
            b.ToTable(Table("EmailAuthenticators"), options.Schema);
            b.HasKey(a => a.Id);
            b.Property(a => a.CodeHash).HasMaxLength(256);
            b.HasIndex(a => a.UserId).IsUnique();
            b.HasOne<TUser>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserPasskey<TId>>(b =>
        {
            b.ToTable(Table("UserPasskeys"), options.Schema);
            b.HasKey(p => p.Id);
            b.Property(p => p.CredentialId).HasMaxLength(1024).IsRequired();
            b.Property(p => p.PublicKey).IsRequired();
            b.Property(p => p.UserHandle).HasMaxLength(64).IsRequired();
            b.Property(p => p.Name).HasMaxLength(128).IsRequired();
            b.Property(p => p.Transports).HasMaxLength(256);
            b.Property(p => p.SignCount).HasConversion<long>(); // uint her veritabanında yok
            b.HasIndex(p => p.CredentialId).IsUnique();
            b.HasIndex(p => p.UserId);
            b.HasOne<TUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        return modelBuilder;
    }
}
