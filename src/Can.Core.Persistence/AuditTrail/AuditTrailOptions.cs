namespace Can.Core.Persistence.AuditTrail;

public sealed class AuditTrailOptions
{
    /// <summary>
    /// <see langword="false"/> (varsayılan): yalnızca <c>[Audited]</c> işaretli entity'lerin geçmişi tutulur.
    /// <see langword="true"/>: <c>[DisableAuditing]</c> işaretliler hariç tüm entity'lerin geçmişi tutulur.
    /// </summary>
    public bool AuditAllEntities { get; set; }

    /// <summary>
    /// Geçmişe yazılmayan alan adları. Varsayılan: audit alanları (zaten kaydın kendisinde ve audit log'da var).
    /// Hassas alanlar için property'ye <c>[DisableAuditing]</c> koy.
    /// </summary>
    public HashSet<string> IgnoredProperties { get; } =
        new(StringComparer.Ordinal) { "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy", "DeletedAt", "DeletedBy" };
}
