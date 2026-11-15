using DomainNotice.Server.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(DomainNotice.Server.Tests.Infrastructure.PostgresFixture))]

namespace DomainNotice.Server.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL 16 container (Docker, dev/test only) per test run. Each test gets its own fresh database via
/// <see cref="CreateDatabaseAsync"/>. Set DOMAINNOTICE_TEST_PG to use an existing server instead of Docker.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _adminConnectionString = "";

    public async ValueTask InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("DOMAINNOTICE_TEST_PG");
        if (!string.IsNullOrWhiteSpace(external))
        {
            _adminConnectionString = external;
            return;
        }
        _container = new PostgreSqlBuilder("postgres:16").Build();
        await _container.StartAsync();
        _adminConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>Creates an empty database with the current model (EnsureCreated; the migration itself is tested separately).</summary>
    public async Task<TestDatabase> CreateDatabaseAsync(bool createSchema = true)
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using (var conn = new NpgsqlConnection(_adminConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var cs = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = name }.ConnectionString;
        var db = new TestDatabase(cs);
        if (createSchema)
        {
            await using var ctx = db.CreateContext();
            await ctx.Database.EnsureCreatedAsync();
        }
        return db;
    }
}

public sealed class TestDatabase(string connectionString) : IDbContextFactory<AppDbContext>
{
    public string ConnectionString { get; } = connectionString;

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>();
        ServiceCollectionExtensions.ConfigureDb(options, ConnectionString);
        return new AppDbContext(options.Options);
    }

    public AppDbContext CreateDbContext() => CreateContext();
}
