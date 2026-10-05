namespace Can.Core.Domain.Auditing;

// Bu alanları uygulama kodu değil, Persistence katmanındaki audit interceptor'ı doldurur.
// Bir entity yalnızca ihtiyaç duyduğu arayüzü uygular; base class kullanmak zorunlu değildir.

/// <summary>Oluşturulma zamanı ve oluşturan kullanıcı.</summary>
public interface ICreationAudited
{
    DateTimeOffset CreatedAt { get; set; }

    string? CreatedBy { get; set; }
}

/// <summary>Son güncellenme zamanı ve güncelleyen kullanıcı.</summary>
public interface IModificationAudited
{
    DateTimeOffset? UpdatedAt { get; set; }

    string? UpdatedBy { get; set; }
}

/// <summary>Oluşturma + güncelleme bilgisi.</summary>
public interface IAudited : ICreationAudited, IModificationAudited { }

/// <summary>
/// Soft delete: kayıt fiziksel olarak silinmez, işaretlenir. Global query filter
/// <see cref="IsDeleted"/> = <see langword="false"/> olanları getirir.
/// Bu arayüzü uygulamayan entity'ler normal (hard) silinir.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; set; }

    DateTimeOffset? DeletedAt { get; set; }

    string? DeletedBy { get; set; }
}

/// <summary>Oluşturma + güncelleme + soft delete bilgisi.</summary>
public interface IFullAudited : IAudited, ISoftDeletable { }
