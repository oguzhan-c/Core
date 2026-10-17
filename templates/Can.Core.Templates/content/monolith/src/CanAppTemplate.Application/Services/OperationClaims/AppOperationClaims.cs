using Can.Core.Security.Authorization;
using CanAppTemplate.Domain.Constants;

namespace CanAppTemplate.Application.Services.OperationClaims;

/// <summary>
/// Uygulamanın tüm yetkileri (operation claim) ve rollere dağılımı. Her özellik kendi sabitlerini
/// <c>Features/{Çoğul}/Constants/{Çoğul}OperationClaims.cs</c> içinde tanımlar; seed'in veritabanına yazması için buraya da
/// eklenir. can-gen yeni özellik üretirken satırları işaretli yerlere kendisi ekler.
/// </summary>
/// <remarks>
/// Yetkiler KODLA tanımlanır: her açılışta veritabanı bu listeye eşitlenir (eksikler eklenir, listeden çıkarılanlar
/// "kullanımdan kalktı" olur). Rollere ve kullanıcılara dağıtım ise çalışırken yönetim uçlarından yapılır
/// (<c>/api/identity/...</c>); <see cref="RoleGrants"/> yalnızca başlangıç dağıtımıdır.
/// </remarks>
public static class AppOperationClaims
{
    /// <summary>Seed'de veritabanına yazılan yetkiler.</summary>
    public static IReadOnlyList<(string Name, string Description)> All { get; } =
    [
        // Rol / yetki yönetimi ("yetki verme yetkisi"): Admin bunları başka rollere verebilir.
        ..IdentityPermissions.All,
        // can-gen:permissions
    ];

    /// <summary>
    /// Seed'de rollere verilen BAŞLANGIÇ yetkileri (Admin her şeyi geçer, listeye gerek yok). Yalnızca rol ilk oluşturulurken
    /// ya da yetki kataloğa ilk eklendiğinde verilir; sonrasında yönetim uçlarından yapılan değişikliklere dokunulmaz.
    /// </summary>
    /// <remarks>TODO(can-gen): Yeni özelliklerin yetkilerini ilgili rollere ver (ör. okuma yetkisini User rolüne).</remarks>
    public static IReadOnlyDictionary<string, string[]> RoleGrants { get; } = new Dictionary<string, string[]>
    {
        [Roles.User] =
        [
            // can-gen:user-grants
        ],
    };
}
