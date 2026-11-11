using System.Net;
using System.Net.Http.Json;
using Academy.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Academy.SecurityTests.Infrastructure;

/// <summary>
/// One Postgres container for the whole run. Every test class gets its own
/// database so seeded users, lessons and in-memory rate-limit counters cannot
/// leak between classes.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("academy")
        .WithUsername("academy")
        .WithPassword("academy")
        .Build();

    public string MaintenanceConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        MaintenanceConnectionString = _container.GetConnectionString();
    }

    /// <summary>A fresh database on the shared container, ready for the API to create its schema in.</summary>
    public async Task<string> CreateDatabaseConnectionStringAsync()
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(MaintenanceConnectionString);
        var name = $"test_{Guid.NewGuid():N}".ToLowerInvariant();
        // CREATE DATABASE cannot run inside a transaction, so it gets its own connection.
        await using (var connection = new Npgsql.NpgsqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync();
        }
        builder.Database = name;
        return builder.ConnectionString;
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class SecurityCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// Shares the container with the rest of the suite and gives one test class its
/// own API host, so a rate-limit window or a seeded login cannot bleed into the
/// next class.
/// </summary>
public abstract class ApiTestBase(PostgresFixture postgres) : IAsyncLifetime
{
    protected ApiFactory Factory { get; } = new(postgres);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Gives every request in the suite a source address, because TestServer leaves
/// the connection's remote address null and the audit trail records it.
/// </summary>
public sealed class RemoteAddressFilter(IPAddress remoteAddress) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(branch => async context =>
        {
            context.Connection.RemoteIpAddress ??= remoteAddress;
            await branch(context);
        });
        next(app);
    };
}

/// <summary>
/// The API as it runs when hosted: a non-Development environment, so Swagger and
/// the fixed developer codes stay off, plus an SMTP endpoint that refuses
/// connections so email fails the way it would during a provider outage.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string IngestKey = "test-transcript-ingest-key";
    public const string TestPassword = "Str0ng!Passw0rd";
    public const string AllowedOrigin = "https://localhost:3000";

    private readonly PostgresFixture _postgres;
    private string _connectionString = "";

    public ApiFactory(PostgresFixture postgres) => _postgres = postgres;

    /// <summary>TestServer always presents the loopback address as the caller.</summary>
    public const string TestRemoteAddress = "127.0.0.1";

    public HttpClient Client => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = true,
        AllowAutoRedirect = false,
    });

    /// <summary>A client with no cookie jar, so a Set-Cookie header can still be read from the response.</summary>
    public HttpClient RawClient => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = false,
        AllowAutoRedirect = false,
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connectionString = _postgres.CreateDatabaseConnectionStringAsync().GetAwaiter().GetResult();
        builder.UseEnvironment("Testing");
        // The entry point captures the connection string and the CORS origins before
        // builder.Build(), so a host-level configuration source is too late for them.
        // They arrive as environment variables, which the default configuration reads
        // at startup. Nothing here points at a developer database.
        Set("DATABASE_URL", "");
        Set("ConnectionStrings__Academy", _connectionString);
        Set("AllowedOrigins", AllowedOrigin);
        // No telemetry, no seeded admin account, and no provider credentials.
        Set("SENTRY_DSN", "");
        Set("OTEL_EXPORTER_OTLP_ENDPOINT", "");
        Set("BOOTSTRAP_ADMIN_EMAIL", "");
        Set("BOOTSTRAP_ADMIN_PASSWORD", "");
        // The TextBee branch is chosen from configuration before the host is built.
        Set("Otp__Sender", "");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Database:EnsureCreated"] = "true",
                ["AllowedOrigins"] = AllowedOrigin,
                // No bootstrap admin: the suite seeds its own accounts.
                ["BootstrapAdmin:Email"] = "",
                ["BootstrapAdmin:Password"] = "",
                // Port 1 refuses instantly, so sends fail without leaving the machine.
                ["Email:Enabled"] = "true",
                ["Email:SmtpHost"] = "127.0.0.1",
                ["Email:SmtpPort"] = "1",
                ["Email:Username"] = "test",
                ["Email:Password"] = "test",
                ["Email:From"] = "test@example.test",
                ["Email:To"] = "ops@example.test",
                ["Otp:DevMode"] = "false",
                ["PasswordReset:DevMode"] = "false",
                ["Transcripts:IngestKey"] = IngestKey,
            }));
        // TestServer leaves the connection's remote address unset, so the audit
        // trail's source IP could never be observed without putting one on the request.
        builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(
            new RemoteAddressFilter(IPAddress.Parse(TestRemoteAddress))));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_connectionString.Length == 0) return;
        // A test database's pool must not outlive its host.
        using var connection = new Npgsql.NpgsqlConnection(_connectionString);
        Npgsql.NpgsqlConnection.ClearPool(connection);
    }

    private static void Set(string name, string value) => Environment.SetEnvironmentVariable(name, value);

    public async Task<AcademyUser> CreateUserAsync(string role, string? phone = null, bool phoneConfirmed = false)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AcademyUser>>();
        var email = $"{role}.{Guid.NewGuid():N}@example.test";
        var user = new AcademyUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            FullName = $"Test {role}",
            EmailConfirmed = true,
            PhoneNumber = phone,
            PhoneNumberConfirmed = phoneConfirmed,
        };
        var created = await users.CreateAsync(user, TestPassword);
        if (!created.Succeeded)
            throw new InvalidOperationException(string.Join("; ", created.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(user, role);
        return user;
    }

    /// <summary>Signs in through the real endpoint, so the client holds the cookie a browser would.</summary>
    public async Task<HttpClient> LoginAsync(string role, string? phone = null, bool phoneConfirmed = false)
    {
        var (client, _) = await LoginAsAsync(role, phone, phoneConfirmed);
        return client;
    }

    /// <summary>Same as <see cref="LoginAsync"/> but also hands back the account, so a test can be the owner or the stranger.</summary>
    public async Task<(HttpClient Client, AcademyUser User)> LoginAsAsync(string role, string? phone = null, bool phoneConfirmed = false)
    {
        var user = await CreateUserAsync(role, phone, phoneConfirmed);
        return (await SignInAsync(user.Email!), user);
    }

    /// <summary>A separate cookie jar for an existing account: another device signing in with the same password.</summary>
    public async Task<HttpClient> SignInAsync(string? email)
    {
        var client = Client;
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    public async Task<Lesson> CreateLessonAsync(Guid teacherId, Guid studentId, string status = "scheduled")
    {
        var starts = DateTimeOffset.UtcNow.AddDays(1);
        var lesson = new Lesson
        {
            TeacherId = teacherId,
            StudentId = studentId,
            StartsAt = starts,
            EndsAt = starts.AddMinutes(30),
            Status = status,
        };
        await WithDbAsync(async db =>
        {
            db.Lessons.Add(lesson);
            await db.SaveChangesAsync();
        });
        return lesson;
    }

    public async Task<T> WithDbAsync<T>(Func<AcademyDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AcademyDbContext>());
    }

    public Task WithDbAsync(Func<AcademyDbContext, Task> mutate) => WithDbAsync<int>(async db =>
    {
        await mutate(db);
        return 0;
    });

    public PhoneOtpStore Otp => Services.GetRequiredService<PhoneOtpStore>();
    public PasswordResetStore Resets => Services.GetRequiredService<PasswordResetStore>();
}
