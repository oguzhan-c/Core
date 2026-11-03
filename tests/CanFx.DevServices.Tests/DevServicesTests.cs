using System.Net;
using System.Net.Sockets;
using CanFx.DevServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CanFx.DevServices.Tests;

public sealed class DevServicesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("canfx-devservices-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("localhost:5432", "localhost", 5432)]
    [InlineData("127.0.0.1:6379", "127.0.0.1", 6379)]
    [InlineData("db.local:1433", "db.local", 1433)]
    public void Parses_host_and_port(string endpoint, string host, int port)
    {
        Assert.Equal((host, port), DevServicesOptions.ParseEndpoint(endpoint));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData(":5432")]
    [InlineData("localhost:abc")]
    [InlineData("localhost:70000")]
    public void Rejects_invalid_endpoint(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => DevServicesOptions.ParseEndpoint(endpoint));
    }

    [Fact]
    public async Task Auto_uses_the_local_service_when_it_is_reachable()
    {
        using var listener = Listen(out int port);
        var commands = new FakeCommands();
        DevServicesRunner runner = CreateRunner(o => o.Add("Database", "postgres", $"127.0.0.1:{port}"), commands);

        await runner.StartAsync(CancellationToken.None);

        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task Docker_mode_starts_the_compose_service_even_if_a_local_one_runs()
    {
        using var listener = Listen(out int port);
        string compose = WriteComposeFile();
        var commands = new FakeCommands();
        DevServicesRunner runner = CreateRunner(o =>
        {
            o.Add("Database", "postgres", $"127.0.0.1:{port}");
            o.Services["Database"] = new DevServiceSettings { Mode = DevServiceMode.Docker };
        }, commands);

        await runner.StartAsync(CancellationToken.None);

        Assert.Equal(2, commands.Calls.Count);
        Assert.Equal("compose version", commands.Calls[0]);
        Assert.Equal($"compose -f {compose} up -d --wait postgres", commands.Calls[1]);
    }

    [Fact]
    public async Task Auto_starts_docker_when_nothing_listens_and_docker_is_missing_is_not_fatal()
    {
        int port = FreePort();
        WriteComposeFile();
        var commands = new FakeCommands { DockerAvailable = false };
        DevServicesRunner runner = CreateRunner(o => o.Add("Database", "postgres", $"127.0.0.1:{port}"), commands);

        await runner.StartAsync(CancellationToken.None);   // fırlatmaz

        Assert.Equal(new[] { "compose version" }, commands.Calls);
    }

    [Fact]
    public async Task Docker_availability_is_checked_once_for_many_services()
    {
        WriteComposeFile();
        var commands = new FakeCommands { DockerAvailable = false };
        DevServicesRunner runner = CreateRunner(o =>
        {
            o.Add("Database", "postgres", $"127.0.0.1:{FreePort()}");
            o.Add("Redis", "redis", $"127.0.0.1:{FreePort()}");
        }, commands);

        await runner.StartAsync(CancellationToken.None);

        Assert.Equal(new[] { "compose version" }, commands.Calls);
    }

    [Theory]
    [InlineData(DevServiceMode.Off)]
    [InlineData(DevServiceMode.Local)]
    public async Task Off_and_Local_never_touch_docker(DevServiceMode mode)
    {
        WriteComposeFile();
        var commands = new FakeCommands();
        DevServicesRunner runner = CreateRunner(o =>
        {
            o.Add("Database", "postgres", $"127.0.0.1:{FreePort()}");
            o.Services["Database"] = new DevServiceSettings { Mode = mode };
        }, commands);

        await runner.StartAsync(CancellationToken.None);

        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task Does_nothing_outside_development()
    {
        WriteComposeFile();
        var commands = new FakeCommands();
        DevServicesRunner runner = CreateRunner(o => o.Add("Database", "postgres", $"127.0.0.1:{FreePort()}"), commands, Environments.Production);

        await runner.StartAsync(CancellationToken.None);

        Assert.Empty(commands.Calls);
    }

    [Fact]
    public async Task Missing_compose_file_is_not_fatal()
    {
        var commands = new FakeCommands();
        DevServicesRunner runner = CreateRunner(o =>
        {
            o.ComposeFile = "yok.yml";
            o.Add("Database", "postgres", $"127.0.0.1:{FreePort()}");
        }, commands);

        await runner.StartAsync(CancellationToken.None);

        Assert.Empty(commands.Calls);
    }

    [Theory]
    [InlineData("Host=localhost;Port=5433;Database=app;Username=app;Password=app", "localhost", 5433)]
    [InlineData("Host=localhost;Database=app", "localhost", 5432)]
    [InlineData("Server=localhost,1434;Database=App;User Id=sa;Password=x;TrustServerCertificate=True", "localhost", 1434)]
    [InlineData("Server=tcp:127.0.0.1;Database=App", "127.0.0.1", 1433)]
    [InlineData("Data Source=.\\SQLEXPRESS;Database=App", "localhost", 1433)]
    [InlineData("localhost:6379,abortConnect=false", "localhost", 6379)]
    [InlineData("amqp://guest:guest@localhost:5672/", "localhost", 5672)]
    [InlineData("amqp://localhost", "localhost", 5672)]
    [InlineData("http://localhost:9200", "localhost", 9200)]
    [InlineData("db.example.com:5432", "db.example.com", 5432)]
    public void Reads_host_and_port_from_connection_strings_and_urls(string value, string host, int port)
    {
        Assert.True(DevServiceAddress.TryParse(value, out string parsedHost, out int parsedPort));
        Assert.Equal((host, port), (parsedHost, parsedPort));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Data Source=app.db")]
    [InlineData("Data Source=/var/data/app.sqlite")]
    [InlineData("Server=(localdb)\\MSSQLLocalDB;Database=App")]
    [InlineData("localhost")]
    public void Addresses_without_a_service_are_not_parsed(string value)
    {
        Assert.False(DevServiceAddress.TryParse(value, out _, out _));
    }

    [Fact]
    public async Task Services_are_read_from_settings_with_their_address_setting()
    {
        int port = FreePort();
        string compose = WriteComposeFile();
        var commands = new FakeCommands();
        IConfiguration configuration = Configuration(new()
        {
            ["Redis:ConnectionString"] = $"localhost:{port},abortConnect=false",
            ["Mail:Provider"] = "Pickup",
            ["Mail:Smtp:Host"] = "localhost",
            ["Mail:Smtp:Port"] = FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Search:Elasticsearch:Url"] = "",
            ["EventBus:RabbitMq:ConnectionString"] = "amqp://guest:guest@rabbit.example.com:5672/",
        });
        DevServicesRunner runner = CreateRunner(o =>
        {
            o.Configuration = configuration;
            o.Services["Redis"] = new DevServiceSettings { Compose = "redis", AddressFrom = "Redis:ConnectionString" };
            o.Services["Mail"] = new DevServiceSettings { Compose = "mailpit", AddressFrom = "Mail:Smtp", When = "Mail:Provider=Smtp" };
            o.Services["Search"] = new DevServiceSettings { Compose = "elasticsearch", AddressFrom = "Search:Elasticsearch:Url" };
            o.Services["EventBus"] = new DevServiceSettings { Compose = "rabbitmq", AddressFrom = "EventBus:RabbitMq:ConnectionString" };
        }, commands);

        await runner.StartAsync(CancellationToken.None);

        // Yalnızca Redis: Mail'in koşulu sağlanmadı, Search'ün adresi boş, RabbitMQ bu bilgisayarda değil.
        Assert.Equal(new[] { "compose version", $"compose -f {compose} up -d --wait redis" }, commands.Calls);
    }

    [Fact]
    public void Settings_bind_strictly_from_configuration()
    {
        IConfiguration configuration = Configuration(new()
        {
            ["DevServices:StartTimeout"] = "00:00:30",
            ["DevServices:Services:Database:Compose"] = "postgres",
            ["DevServices:Services:Database:AddressFrom"] = "ConnectionStrings:App",
            ["DevServices:Services:Mail:Mode"] = "Off",
        });
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddCanDevServices(configuration);
        DevServicesOptions options = (DevServicesOptions)services.Single(d => d.ServiceType == typeof(DevServicesOptions)).ImplementationInstance!;

        Assert.Equal(TimeSpan.FromSeconds(30), options.StartTimeout);
        Assert.Equal("postgres", options.Services["database"].Compose);
        Assert.Equal(DevServiceMode.Off, options.ModeOf("Mail"));

        IConfiguration unknown = Configuration(new() { ["DevServices:Services:Database:Port"] = "5432" });
        Assert.Throws<InvalidOperationException>(() => new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddCanDevServices(unknown));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private DevServicesRunner CreateRunner(Action<DevServicesOptions> configure, FakeCommands commands, string environment = "Development")
    {
        var options = new DevServicesOptions { StartTimeout = TimeSpan.FromSeconds(3) };
        configure(options);
        return new DevServicesRunner(options, new TestEnvironment(environment, _root), commands, NullLogger<DevServicesRunner>.Instance);
    }

    private string WriteComposeFile()
    {
        string path = Path.Combine(_root, "docker-compose.yml");
        File.WriteAllText(path, "services: {}\n");
        return path;
    }

    private static TcpListener Listen(out int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static int FreePort()
    {
        using TcpListener listener = Listen(out int port);
        listener.Stop();
        return port;
    }

    private sealed class FakeCommands : ICommandRunner
    {
        public bool DockerAvailable { get; init; } = true;

        public List<string> Calls { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal("docker", fileName);
            string call = string.Join(' ', arguments);
            Calls.Add(call);
            int exitCode = call == "compose version" && !DockerAvailable ? 1 : 0;
            return Task.FromResult((exitCode, string.Empty));
        }
    }

    private sealed class TestEnvironment(string name, string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
