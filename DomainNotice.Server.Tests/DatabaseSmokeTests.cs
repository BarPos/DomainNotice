using DomainNotice.Server.Data.Entities;
using DomainNotice.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DomainNotice.Server.Tests;

public class DatabaseSmokeTests(PostgresFixture pg)
{
    [Fact]
    public async Task Computer_round_trips_with_ip_array()
    {
        var db = await pg.CreateDatabaseAsync();
        await using (var ctx = db.CreateContext())
        {
            ctx.Computers.Add(new Computer
            {
                Id = Guid.NewGuid(), Name = "PC-1", AgentKeyFingerprint = new string('a', 64),
                EnrolledAtUtc = DateTime.UtcNow, LocalIps = ["10.0.0.1", "fe80::1"],
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using (var ctx = db.CreateContext())
        {
            var c = await ctx.Computers.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(["10.0.0.1", "fe80::1"], c.LocalIps);
        }
    }
}
