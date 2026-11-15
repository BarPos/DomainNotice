using DomainNotice.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Tests.Integration;

/// <summary>Plan 10: the schema comes from migrations, and the model has no changes missing from them.</summary>
public class MigrationTests(PostgresFixture pg)
{
    [Fact]
    public async Task Migrations_apply_to_an_empty_database_and_match_the_model()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = await pg.CreateDatabaseAsync(createSchema: false);
        await using var ctx = db.CreateContext();

        Assert.False(ctx.Database.HasPendingModelChanges(),
            "The EF model has changes that are not in a migration. Run: dotnet ef migrations add <Name> --project DomainNotice.Server --output-dir Data/Migrations");

        await ctx.Database.MigrateAsync(ct);
        Assert.Empty(await ctx.Database.GetPendingMigrationsAsync(ct));
        Assert.Contains("20261115181506_Initial", await ctx.Database.GetAppliedMigrationsAsync(ct));

        // Re-running is a no-op (install.sh / update.sh call --migrate-only repeatedly).
        await ctx.Database.MigrateAsync(ct);
        Assert.Equal(0, await ctx.Computers.CountAsync(ct));
    }
}
