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
        Write("src/Shop.WebApi/Shop.WebApi.csproj", "<Project />");

        Write("src/Shop.Persistence/AppDbContext.cs", """
            using Can.Core.Persistence.Context;
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Persistence;

            public sealed class AppDbContext
            {
                public DbSet<AppUser> Users => Set<AppUser>();
                // can-gen:dbsets
            }
            """);

        Write("src/Shop.Application/Common/AppPermissions.cs", """
            using Shop.Domain.Identity;

            namespace Shop.Application.Common;

            public static class AppPermissions
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

        Write("src/Shop.Application/DependencyInjection.cs", """
            using Microsoft.Extensions.DependencyInjection;

            namespace Shop.Application;

            public static class DependencyInjection
            {
                public static IServiceCollection AddShopApplication(this IServiceCollection services)
                {
                    // can-gen:services
                    return services;
                }
            }
            """);

        Write("src/Shop.WebApi/Endpoints/EndpointRegistration.cs", """
            namespace Shop.WebApi.Endpoints;

            internal static class EndpointRegistration
            {
                public static IEndpointRouteBuilder MapShopEndpoints(this IEndpointRouteBuilder app)
                {
                    // Uygulama uçları (can-gen her entity için bir satır ekler).
                    // can-gen:endpoints
                    return app;
                }
            }
            """);

        Write("src/Shop.WebApi/Localization/tr.json", """
            {
              // yorum
              "invalid_credentials": "E-posta ya da şifre hatalı."
            }
            """);
        Write("src/Shop.WebApi/Localization/en.json", "{\n}\n");
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
