using Microsoft.EntityFrameworkCore;

namespace MediaInsights.Api;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(AppDbContext db, string contentRoot)
    {
        if (db.Database.IsNpgsql())
            await db.Database.MigrateAsync();
        else
        {
            // Preserve the pre-migrations local SQLite databases and their Identity accounts.
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS ContentInitialization (Id INTEGER PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS Programmes (
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, De TEXT NOT NULL, En TEXT NOT NULL,
                    Medium TEXT NOT NULL, CategoryDe TEXT NOT NULL, CategoryEn TEXT NOT NULL, Daily TEXT NOT NULL);
                """);
        }
        // Serialize initialization on PostgreSQL when more than one backend starts at once.
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(72419021)");
        if (!await db.ContentInitialization.AnyAsync())
        {
            if (!await db.Programmes.AnyAsync())
            {
                var seed = await File.ReadAllTextAsync(Path.Combine(contentRoot, "programmes.seed.json"));
                var programmes = System.Text.Json.JsonSerializer.Deserialize<List<MediaProgramme>>(seed,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                // Use database-generated keys, including PostgreSQL's identity sequence.
                foreach (var programme in programmes) programme.Id = 0;
                db.Programmes.AddRange(programmes);
            }
            db.ContentInitialization.Add(new ContentInitialization { Id = 1 });
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync();
    }
}
