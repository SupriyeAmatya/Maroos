using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Maroos.Web.Data;

public static class DatabaseSetup
{
    public const string Sqlite = "Sqlite";
    public const string Postgres = "Postgres";

    /// <summary><c>Database:Provider</c> if set; otherwise SQLite in Development and PostgreSQL everywhere else.</summary>
    public static string ProviderFor(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Database:Provider"];
        if (string.IsNullOrWhiteSpace(configured))
            return environment.IsDevelopment() ? Sqlite : Postgres;
        if (configured.Equals(Sqlite, StringComparison.OrdinalIgnoreCase))
            return Sqlite;
        if (configured.Equals(Postgres, StringComparison.OrdinalIgnoreCase) || configured.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
            return Postgres;
        throw new InvalidOperationException($"Unknown Database:Provider '{configured}'. Use '{Sqlite}' or '{Postgres}'.");
    }

    /// <summary>
    /// Registers <see cref="ApplicationDbContext"/> (scoped, for Identity) and <c>IDbContextFactory&lt;ApplicationDbContext&gt;</c>
    /// (for services) on the configured provider. On PostgreSQL both hand out <see cref="PostgresDbContext"/>.
    /// </summary>
    public static IServiceCollection AddMaroosDatabase(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        // `dotnet ef` builds contexts through this app's services, so Identity's schema options apply to both
        // migration sets. Register both there: ApplicationDbContext on SQLite, PostgresDbContext on Npgsql
        // (the configured connection when Database:Provider is Postgres, so `database update` can reach production).
        if (EF.IsDesignTime)
        {
            var isPostgres = ProviderFor(configuration, environment) == Postgres;
            services.AddDbContextFactory<ApplicationDbContext>(options =>
                options.UseSqlite(isPostgres ? "DataSource=Data/app.db" : connectionString));
            services.AddDbContextFactory<PostgresDbContext>(options =>
                options.UseNpgsql(isPostgres ? ToNpgsqlConnectionString(connectionString) : "Host=localhost;Database=maroos;Username=postgres"));
            return services;
        }

        if (ProviderFor(configuration, environment) == Sqlite)
        {
            services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite(connectionString));
            return services;
        }

        // Hosts like Fly.io, Railway and Heroku hand out DATABASE_URL; use it unless a connection string was set explicitly.
        if (connectionString.StartsWith("DataSource=", StringComparison.OrdinalIgnoreCase) && configuration["DATABASE_URL"] is { Length: > 0 } databaseUrl)
            connectionString = databaseUrl;
        if (connectionString.StartsWith("DataSource=", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Database:Provider is Postgres but ConnectionStrings:DefaultConnection is the SQLite default. " +
                "Set ConnectionStrings__DefaultConnection (or DATABASE_URL) to your PostgreSQL connection string or postgres:// URL.");

        var npgsql = WithoutKerberosProbe(ToNpgsqlConnectionString(connectionString));
        services.AddDbContextFactory<PostgresDbContext>(options => options.UseNpgsql(npgsql));
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(sp =>
            new DerivedContextFactory<PostgresDbContext>(sp.GetRequiredService<IDbContextFactory<PostgresDbContext>>()));
        services.AddScoped<ApplicationDbContext>(sp => sp.GetRequiredService<PostgresDbContext>());
        return services;
    }

    /// <summary>
    /// Accepts either an Npgsql connection string or a <c>postgres://user:pass@host:port/db?sslmode=require</c> URL,
    /// the form Render, Fly.io, Neon and Heroku hand out.
    /// </summary>
    public static string ToNpgsqlConnectionString(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return value;

        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]).ToLowerInvariant();
            var val = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            if (key == "sslmode" && Enum.TryParse<SslMode>(val.Replace("-", ""), ignoreCase: true, out var sslMode))
                builder.SslMode = sslMode;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Npgsql tries GSS (Kerberos) encryption by default, which needs libgssapi — absent from the slim ASP.NET image,
    /// so every connection logs an error. Maroos doesn't use Kerberos: turn it off unless the connection string asks for it.
    /// </summary>
    public static string WithoutKerberosProbe(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("GSS", StringComparison.OrdinalIgnoreCase))
            builder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder.ConnectionString;
    }

    /// <summary>Applies pending migrations at startup when <c>Database:MigrateOnStartup</c> is true (single-instance hosting).</summary>
    public static async Task MigrateIfConfiguredAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
            return;

        // On an empty database EF reads the history table before creating it, which logs a harmless but alarming
        // "Failed executing DbCommand". Creating the table first keeps a first deploy's log clean.
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync();
        await db.Database.MigrateAsync();
        app.Logger.LogInformation("Database is up to date ({Count} migrations applied)", (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    private sealed class DerivedContextFactory<TContext>(IDbContextFactory<TContext> inner) : IDbContextFactory<ApplicationDbContext>
        where TContext : ApplicationDbContext
    {
        public ApplicationDbContext CreateDbContext() => inner.CreateDbContext();
    }
}
