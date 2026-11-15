using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DomainNotice.Server.Data;

/// <summary>For `dotnet ef migrations add X --project DomainNotice.Server --output-dir Data/Migrations`.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("DOMAINNOTICE_DESIGN_DB")
                 ?? "Host=localhost;Port=5432;Database=domainnotice_design;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<AppDbContext>();
        ServiceCollectionExtensions.ConfigureDb(options, cs);
        return new AppDbContext(options.Options);
    }
}
