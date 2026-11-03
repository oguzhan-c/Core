using System.Net;
using System.Net.Sockets;
using CanFx.DevServices;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        Assert.Equal(new[] { "compose version", $"compose -f {compose} config --format json", $"compose -f {compose} ps --format json",
            $"compose -f {compose} up -d --wait postgres" }, commands.Calls);
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
            ["Mail:Smtp:Port"] = FreePort().ToString(CultureInfo.InvariantCulture),
            ["Search:Elasticsearch:Url"] = "",
            ["EventBus:RabbitMq:ConnectionString"] = "amqp://guest:guest@rabbit.example.com:5672/",
        });
        DevServicesRunner runner = CreateRunner(o =>
        {
            o.Services["Redis"] = new DevServiceSettings { Compose = "redis", AddressFrom = "Redis:ConnectionString" };
            o.Services["Mail"] = new DevServiceSettings { Compose = "mailpit", AddressFrom = "Mail:Smtp", When = "Mail:Provider=Smtp" };
            o.Services["Search"] = new DevServiceSettings { Compose = "elasticsearch", AddressFrom = "Search:Elasticsearch:Url" };
            o.Services["EventBus"] = new DevServiceSettings { Compose = "rabbitmq", AddressFrom = "EventBus:RabbitMq:ConnectionString" };
        }, commands, configuration: configuration);

        await runner.StartAsync(CancellationToken.None);

        // Yalnızca Redis: Mail'in koşulu sağlanmadı, Search'ün adresi boş, RabbitMQ bu bilgisayarda değil.
        Assert.Equal(new[] { "compose version", $"compose -f {compose} config --format json", $"compose -f {compose} ps --format json",
            $"compose -f {compose} up -d --wait redis" }, commands.Calls);
        Assert.Empty(_plan!.Overrides);
    }

    [Fact]
    public async Task Busy_port_moves_the_service_to_the_next_free_port_and_the_address_follows()
    {
        using TcpListener busy = Listen(out int port);
        string compose = WriteComposeFile();
        var commands = new FakeCommands
        {
            ConfigOutput = $$$$"""{"services":{"redis":{"ports":[{"target":6379,"published":"{{{{port}}}}","protocol":"tcp"}]}}}""",
        };
        IConfiguration configuration = Configuration(new() { ["Redis:ConnectionString"] = $"localhost:{port},abortConnect=false" });
        DevServicesRunner runner = CreateRunner(
            o => o.Services["Redis"] = new DevServiceSettings { Compose = "redis", AddressFrom = "Redis:ConnectionString" },
            commands, configuration: configuration);

        await runner.StartAsync(CancellationToken.None);

        DevServicePlanItem item = Assert.Single(_plan!.Items);
        Assert.NotEqual(port, item.Port);
        Assert.Equal($"localhost:{item.Port},abortConnect=false", _plan.Overrides["Redis:ConnectionString"]);
        Assert.NotNull(item.OverrideFile);
        Assert.Contains($"\"{item.Port}:6379\"", File.ReadAllText(item.OverrideFile));
        Assert.Equal($"compose -f {compose} -f {item.OverrideFile} up -d --wait redis", commands.Calls[^1]);
    }

    [Fact]
    public async Task Running_container_of_the_project_is_reused_with_its_port()
    {
        int published = FreePort();
        WriteComposeFile();
        var commands = new FakeCommands
        {
            ConfigOutput = """{"services":{"redis":{"ports":[{"target":6379,"published":"6379"}]}}}""",
            PsOutput = $$$"""{"Service":"redis","State":"running","Publishers":[{"URL":"0.0.0.0","TargetPort":6379,"PublishedPort":{{{published}}},"Protocol":"tcp"}]}""",
        };
        IConfiguration configuration = Configuration(new() { ["Redis:ConnectionString"] = "localhost:6379" });
        DevServicesRunner runner = CreateRunner(
            o => o.Services["Redis"] = new DevServiceSettings { Compose = "redis", AddressFrom = "Redis:ConnectionString" },
            commands, configuration: configuration);

        await runner.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(commands.Calls, c => c.Contains(" up ", StringComparison.Ordinal));
        Assert.Equal($"localhost:{published}", _plan!.Overrides["Redis:ConnectionString"]);
    }

    [Fact]
    public async Task Service_missing_in_compose_file_is_not_started()
    {
        WriteComposeFile();
        var commands = new FakeCommands { ConfigOutput = """{"services":{"mailpit":{}}}""" };
        IConfiguration configuration = Configuration(new() { ["ConnectionStrings:App"] = $"Host=localhost;Port={FreePort()};Database=app" });
        DevServicesRunner runner = CreateRunner(
            o => o.Services["Database"] = new DevServiceSettings { Compose = "postgres", AddressFrom = "ConnectionStrings:App" },
            commands, configuration: configuration);

        await runner.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(commands.Calls, c => c.Contains(" up ", StringComparison.Ordinal));
        Assert.Contains(_plan!.Messages, m => m.Message.Contains("'postgres' servisi yok", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=app;Username=app", 5433)]
    [InlineData("Host=localhost;Database=app", 5433)]
    [InlineData("Server=localhost,1433;Database=App;User Id=sa", 1434)]
    [InlineData("localhost:6379,abortConnect=false", 6380)]
    [InlineData("amqp://guest:guest@localhost:5672/", 5673)]
    [InlineData("http://localhost:9200", 9201)]
    public void Address_is_rewritten_with_the_new_port(string value, int port)
    {
        string rewritten = DevServiceAddress.WithPort(value, port);

        Assert.True(DevServiceAddress.TryParse(rewritten, out string host, out int parsed));
        Assert.Equal(("localhost", port), (host, parsed));
        if (value.Contains("guest:guest@", StringComparison.Ordinal))
            Assert.Contains("guest:guest@", rewritten);
    }

    [Theory]
    [InlineData("""{"Service":"redis","State":"running","Publishers":[{"TargetPort":6379,"PublishedPort":6390}]}""")]
    [InlineData("""[{"Service":"redis","State":"running","Publishers":[{"TargetPort":6379,"PublishedPort":6390}]}]""")]
    public void Running_ports_are_read_from_both_ps_formats(string output)
    {
        PortMapping mapping = Assert.Single(DevServicesPlanner.ParseRunningPorts(output)["redis"]);
        Assert.Equal((6379, 6390), (mapping.Target, mapping.Published));
    }

    [Fact]
    public void Settings_bind_strictly_from_configuration()
    {
        IConfiguration configuration = Configuration(new()
        {
            ["DevServices:StartTimeout"] = "00:00:30",
            ["DevServices:PortRange"] = "20",
            ["DevServices:Services:Database:Compose"] = "postgres",
            ["DevServices:Services:Database:AddressFrom"] = "ConnectionStrings:App",
            ["DevServices:Services:Mail:Mode"] = "Off",
        });
        var production = new TestEnvironment(Environments.Production, _root);
        var services = new ServiceCollection();
        services.AddCanDevServices(configuration, production);
        DevServicesOptions options = (DevServicesOptions)services.Single(d => d.ServiceType == typeof(DevServicesOptions)).ImplementationInstance!;

        Assert.Equal(TimeSpan.FromSeconds(30), options.StartTimeout);
        Assert.Equal(20, options.PortRange);
        Assert.Equal("postgres", options.Services["database"].Compose);
        Assert.Equal(DevServiceMode.Off, options.ModeOf("Mail"));

        IConfiguration unknown = Configuration(new() { ["DevServices:Services:Database:Port"] = "5432" });
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCanDevServices(unknown, production));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private DevServicesPlan? _plan;

    private DevServicesRunner CreateRunner(
        Action<DevServicesOptions> configure, FakeCommands commands, string environment = "Development", IConfiguration? configuration = null)
    {
        var options = new DevServicesOptions { StartTimeout = TimeSpan.FromSeconds(3) };
        configure(options);
        _plan = environment == Environments.Development ? new DevServicesPlanner(options, configuration, _root, commands).Plan() : null;
        return new DevServicesRunner(options, new TestEnvironment(environment, _root), commands, NullLogger<DevServicesRunner>.Instance, _plan);
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

        public string ConfigOutput { get; init; } = string.Empty;

        public string PsOutput { get; init; } = string.Empty;

        public List<string> Calls { get; } = [];

        public Task<(int ExitCode, string Output)> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal("docker", fileName);
            string call = string.Join(' ', arguments);
            Calls.Add(call);
            if (call == "compose version")
                return Task.FromResult((DockerAvailable ? 0 : 1, string.Empty));
            if (call.EndsWith("config --format json", StringComparison.Ordinal))
                return Task.FromResult((0, ConfigOutput));
            if (call.EndsWith("ps --format json", StringComparison.Ordinal))
                return Task.FromResult((0, PsOutput));
            return Task.FromResult((0, string.Empty));
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
