using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Application.Common;

/// <summary>
/// Uygulamanın tüm yetkileri (operation claim) ve rollere dağılımı. Her özellik kendi sabitlerini
/// <c>Features/{Çoğul}/Constants/{Entity}Permissions.cs</c> içinde tanımlar; seed'in veritabanına yazması için buraya da
/// eklenir. can-gen yeni özellik üretirken satırları işaretli yerlere kendisi ekler.
/// </summary>
public static class AppPermissions
{
    /// <summary>Seed'de veritabanına yazılan yetkiler.</summary>
    public static IReadOnlyList<(string Name, string Description)> All { get; } =
    [
        // can-gen:permissions
    ];

    /// <summary>Seed'de rollere verilen yetkiler (Admin her şeyi geçer, listeye gerek yok).</summary>
    /// <remarks>TODO(can-gen): Yeni özelliklerin yetkilerini ilgili rollere ver (ör. okuma yetkisini User rolüne).</remarks>
    public static IReadOnlyDictionary<string, string[]> RoleGrants { get; } = new Dictionary<string, string[]>
    {
        [Roles.User] =
        [
            // can-gen:user-grants
        ],
    };
}
