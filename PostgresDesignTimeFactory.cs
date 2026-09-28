using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MediaInsights.Api;

// Migrations in this project are PostgreSQL-only. No real credentials or connection
// are needed to scaffold migrations; SQLite retains its legacy initialization path.
public class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=design_only;Username=design_only")
            .Options);
}
