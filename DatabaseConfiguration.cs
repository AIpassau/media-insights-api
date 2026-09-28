using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MediaInsights.Api;

public static class DatabaseConfiguration
{
    public static void Configure(DbContextOptionsBuilder options, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            options.UseSqlite(configuration.GetConnectionString("AppDb") ?? "Data Source=media-insights.db");
        else if (provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            // Deliberately do not fall back to SQLite when cloud configuration is missing.
            var connection = configuration["DATABASE_URL"];
            if (string.IsNullOrWhiteSpace(connection)) connection = configuration.GetConnectionString("AppDb");
            if (string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("PostgreSQL requires DATABASE_URL or ConnectionStrings:AppDb in backend private configuration.");
            options.UseNpgsql(ParsePostgresConnection(connection));
        }
        else throw new InvalidOperationException("Database:Provider must be Sqlite or PostgreSQL.");
    }

    public static string ParsePostgresConnection(string value)
    {
        // Npgsql accepts ADO.NET connection strings; Neon also supplies postgres:// URLs.
        try
        {
            NpgsqlConnectionStringBuilder result;
            if (value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(value, UriKind.Absolute);
                var credentials = uri.UserInfo.Split(':', 2);
                if (credentials.Length != 2 || string.IsNullOrEmpty(uri.Host) || uri.AbsolutePath.Length <= 1 || !string.IsNullOrEmpty(uri.Fragment))
                    throw new FormatException();
                result = new NpgsqlConnectionStringBuilder
                {
                    Host = uri.Host, Port = uri.IsDefaultPort ? 5432 : uri.Port,
                    Username = Uri.UnescapeDataString(credentials[0]),
                    Password = Uri.UnescapeDataString(credentials[1]),
                    Database = Uri.UnescapeDataString(uri.AbsolutePath[1..]),
                    SslMode = SslMode.VerifyFull,
                    MaxPoolSize = 10, Timeout = 30,
                };
                foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = part.Split('=', 2);
                    if (pair.Length != 2) throw new FormatException();
                    var key = Uri.UnescapeDataString(pair[0]).ToLowerInvariant();
                    var setting = Uri.UnescapeDataString(pair[1]);
                    switch (key)
                    {
                        // Upgrade Require to VerifyFull so Neon certificates are also validated.
                        case "sslmode" when setting is "require" or "verify-full": break;
                        case "channel_binding":
                            result.ChannelBinding = setting switch
                            {
                                "require" => ChannelBinding.Require, "prefer" => ChannelBinding.Prefer,
                                "disable" => ChannelBinding.Disable, _ => throw new FormatException()
                            };
                            break;
                        case "connect_timeout": result.Timeout = int.Parse(setting); break;
                        case "application_name": result.ApplicationName = setting; break;
                        case "options": result.Options = setting; break;
                        default: throw new FormatException();
                    }
                }
            }
            else
            {
                result = new NpgsqlConnectionStringBuilder(value);
                if (!result.ContainsKey("SSL Mode")) result.SslMode = SslMode.VerifyFull;
                if (!result.ContainsKey("Maximum Pool Size")) result.MaxPoolSize = 10;
                if (!result.ContainsKey("Timeout")) result.Timeout = 30;
            }
            if (string.IsNullOrWhiteSpace(result.Host) || string.IsNullOrWhiteSpace(result.Database) ||
                string.IsNullOrWhiteSpace(result.Username)) throw new FormatException();
            return result.ConnectionString;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            // Do not include the supplied value or inner exception: they can expose credentials.
            throw new InvalidOperationException("Invalid PostgreSQL configuration. Use a postgres:// URL with sslmode=require/verify-full or an Npgsql connection string. See POSTGRESQL_SETUP.md.");
        }
    }
}
