namespace Can.Core.Generator.Tests;

/// <summary>Geçici klasörde şablonun işaretli dosyalarını taklit eden küçük bir çözüm.</summary>
internal sealed class TestSolution : IDisposable
{
    public TestSolution()
    {
        Root = Path.Combine(Path.GetTempPath(), "can-gen-tests", Guid.NewGuid().ToString("N"));
        Write("Shop.slnx", "<Solution />");
        Write("src/Shop.Domain/Shop.Domain.csproj", "<Project />");
        Write("src/Shop.Application/Shop.Application.csproj", "<Project />");
        Write("src/Shop.Persistence/Shop.Persistence.csproj", "<Project />");
        Write("src/Shop.WebAPI/Shop.WebAPI.csproj", "<Project />");

        Write("src/Shop.Persistence/Contexts/BaseDbContext.cs", """
            using Can.Core.Persistence.Context;
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Persistence.Contexts;

            public sealed class BaseDbContext
            {
                public DbSet<AppUser> Users => Set<AppUser>();
                // can-gen:dbsets
            }
            """);

        Write("src/Shop.Application/Services/OperationClaims/AppOperationClaims.cs", """
            using Shop.Domain.Constants;

            namespace Shop.Application.Services.OperationClaims;

            public static class AppOperationClaims
            {
                public static IReadOnlyList<(string Name, string Description)> All { get; } =
                [
                    // can-gen:permissions
                ];

                public static IReadOnlyDictionary<string, string[]> RoleGrants { get; } = new Dictionary<string, string[]>
                {
                    [Roles.User] =
                    [
                        // can-gen:user-grants
                    ],
                };
            }
            """);

        Write("src/Shop.Application/ApplicationServiceRegistration.cs", """
            using Microsoft.Extensions.DependencyInjection;

            namespace Shop.Application;

            public static class ApplicationServiceRegistration
            {
                public static IServiceCollection AddApplicationServices(this IServiceCollection services)
                {
                    // can-gen:services
                    return services;
                }
            }
            """);

        Write("src/Shop.Persistence/PersistenceServiceRegistration.cs", """
            using Microsoft.Extensions.DependencyInjection;

            namespace Shop.Persistence;

            public static class PersistenceServiceRegistration
            {
                public static IServiceCollection AddPersistenceServices(this IServiceCollection services)
                {
                    // can-gen:repositories
                    return services;
                }
            }
            """);

        Write("src/Shop.WebAPI/Localization/tr.json", """
            {
              // yorum
              "invalid_credentials": "E-posta ya da şifre hatalı."
            }
            """);
        Write("src/Shop.WebAPI/Localization/en.json", "{\n}\n");
    }

    public string Root { get; }

    public string Path_(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Read(string relative) => File.ReadAllText(Path_(relative));

    public bool Exists(string relative) => File.Exists(Path_(relative));

    public void Write(string relative, string content)
    {
        string path = Path_(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
