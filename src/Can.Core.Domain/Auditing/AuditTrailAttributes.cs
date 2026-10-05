namespace Can.Core.Domain.Auditing;

/// <summary>
/// Entity'nin değişiklik geçmişi (kim, ne zaman, hangi alanı neyden neye değiştirdi) tutulur.
/// Persistence katmanında audit trail etkinse (<c>modelBuilder.AddCanAuditTrail()</c>) çalışır.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class AuditedAttribute : Attribute;

/// <summary>
/// Sınıfa konursa entity'nin geçmişi tutulmaz (tüm entity'ler denetlenirken hariç tutmak için);
/// property'ye konursa o alanın değeri geçmişe yazılmaz (şifre hash'i, gizli anahtar ...).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class DisableAuditingAttribute : Attribute;
