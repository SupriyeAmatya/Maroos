// =====================================================================================================
// FILE: Maroos.Web/Data/DatabaseSetup.cs
// PURPOSE: Chooses and configures the database: SQLite in development, PostgreSQL in production.
//
// KEY IDEAS
//   * Database:Provider picks the database. If it isn't set: Development -> SQLite, anything else -> PostgreSQL.
//   * Two EF Core "context" classes share one data model:
//       ApplicationDbContext — owns the SQLite migrations      (Data/Migrations)
//       PostgresDbContext    — owns the PostgreSQL migrations  (Data/Migrations/Postgres)
//     EF generates SQL per database type, so each database needs its own migration set.
//   * Services always ask for IDbContextFactory<ApplicationDbContext>; on PostgreSQL that factory hands
//     out PostgresDbContext objects (a subclass), so no service needs to know which database is in use.
//   * Hosting providers give database URLs like postgres://user:pass@host/db; ToNpgsqlConnectionString
//     converts them. DATABASE_URL is also accepted (Fly.io, Railway, Heroku set it).
// =====================================================================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
// Npgsql: the PostgreSQL driver for .NET.
using Npgsql;

namespace Maroos.Web.Data;

public static class DatabaseSetup
{
    // The two accepted values of Database:Provider.
    public const string Sqlite = "Sqlite";
    public const string Postgres = "Postgres";

    /// <summary><c>Database:Provider</c> if set; otherwise SQLite in Development and PostgreSQL everywhere else.</summary>
    // ProviderFor: decides which database to use (case-insensitive; "PostgreSQL" also accepted).
    // An unknown value stops the app at startup with a clear message.
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
    // AddMaroosDatabase: an "extension method" (note `this IServiceCollection`), so Program.cs can write
    // builder.Services.AddMaroosDatabase(...). Three situations:
    //   1. design time (running `dotnet ef ...` commands) — register BOTH contexts;
    //   2. SQLite — register ApplicationDbContext;
    //   3. PostgreSQL — register PostgresDbContext and make ApplicationDbContext requests return it.
    public static IServiceCollection AddMaroosDatabase(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // The connection string from appsettings.json / environment (ConnectionStrings:DefaultConnection).
        var connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        // `dotnet ef` builds contexts through this app's services, so Identity's schema options apply to both
        // migration sets. Register both there: ApplicationDbContext on SQLite, PostgresDbContext on Npgsql
        // (the configured connection when Database:Provider is Postgres, so `database update` can reach production).
        // (A standalone "design-time factory" was tried and rejected: it skipped Identity's options and
        // generated migrations that would have dropped the passkeys table.)
        if (EF.IsDesignTime)
        {
            var isPostgres = ProviderFor(configuration, environment) == Postgres;
            services.AddDbContextFactory<ApplicationDbContext>(options =>
                options.UseSqlite(isPostgres ? "DataSource=Data/app.db" : connectionString));
            services.AddDbContextFactory<PostgresDbContext>(options =>
                options.UseNpgsql(isPostgres ? ToNpgsqlConnectionString(connectionString) : "Host=localhost;Database=maroos;Username=postgres"));
            return services;
        }

        // Development: SQLite file (Data/app.db). AddDbContextFactory also registers a scoped ApplicationDbContext.
        if (ProviderFor(configuration, environment) == Sqlite)
        {
            services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlite(connectionString));
            return services;
        }

        // Hosts like Fly.io, Railway and Heroku hand out DATABASE_URL; use it unless a connection string was set explicitly.
        // ("DataSource=..." is the SQLite default from appsettings.json, i.e. "not set for Postgres".)
        if (connectionString.StartsWith("DataSource=", StringComparison.OrdinalIgnoreCase) && configuration["DATABASE_URL"] is { Length: > 0 } databaseUrl)
            connectionString = databaseUrl;
        // Still the SQLite default? Then production has no database configured: stop with a clear message.
        if (connectionString.StartsWith("DataSource=", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Database:Provider is Postgres but ConnectionStrings:DefaultConnection is the SQLite default. " +
                "Set ConnectionStrings__DefaultConnection (or DATABASE_URL) to your PostgreSQL connection string or postgres:// URL.");

        // Production: PostgreSQL.
        var npgsql = WithoutKerberosProbe(ToNpgsqlConnectionString(connectionString));
        services.AddDbContextFactory<PostgresDbContext>(options => options.UseNpgsql(npgsql));
        // Services ask for IDbContextFactory<ApplicationDbContext>; give them one that creates PostgresDbContext.
        services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(sp =>
            new DerivedContextFactory<PostgresDbContext>(sp.GetRequiredService<IDbContextFactory<PostgresDbContext>>()));
        // Identity asks for ApplicationDbContext directly; give it the scoped PostgresDbContext.
        services.AddScoped<ApplicationDbContext>(sp => sp.GetRequiredService<PostgresDbContext>());
        return services;
    }

    /// <summary>
    /// Accepts either an Npgsql connection string or a <c>postgres://user:pass@host:port/db?sslmode=require</c> URL,
    /// the form Render, Fly.io, Neon and Heroku hand out.
    /// </summary>
    // ToNpgsqlConnectionString: "postgres://u:p%40ss@db:6543/maroos?sslmode=require"
    //   -> "Host=db;Port=6543;Database=maroos;Username=u;Password=p@ss;SSL Mode=Require".
    // A value that's already an Npgsql string is returned unchanged.
    public static string ToNpgsqlConnectionString(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return value;

        // Parse the URL. UserInfo is "user:password" (URL-encoded, so special characters are %xx).
        var uri = new Uri(value);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            // PostgreSQL's default port is 5432.
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            // The path "/maroos" is the database name.
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };

        // Read query parameters; only sslmode matters (e.g. ?sslmode=require for external connections).
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]).ToLowerInvariant();
            var val = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            // "verify-full" -> "VerifyFull" to match Npgsql's SslMode enum names.
            if (key == "sslmode" && Enum.TryParse<SslMode>(val.Replace("-", ""), ignoreCase: true, out var sslMode))
                builder.SslMode = sslMode;
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Npgsql tries GSS (Kerberos) encryption by default, which needs libgssapi — absent from the slim ASP.NET image,
    /// so every connection logs an error. Maroos doesn't use Kerberos: turn it off unless the connection string asks for it.
    /// </summary>
    // WithoutKerberosProbe: found during Docker testing (every connection logged "libgssapi_krb5.so.2 not found").
    public static string WithoutKerberosProbe(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!connectionString.Contains("GSS", StringComparison.OrdinalIgnoreCase))
            builder.GssEncryptionMode = GssEncryptionMode.Disable;
        return builder.ConnectionString;
    }

    /// <summary>Applies pending migrations at startup when <c>Database:MigrateOnStartup</c> is true (single-instance hosting).</summary>
    // MigrateIfConfiguredAsync: creates/updates the database tables when the app starts.
    // Fine for one app instance; with several instances, migrate once per release instead (see DEPLOY.md).
    public static async Task MigrateIfConfiguredAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
            return;

        // On an empty database EF reads the history table before creating it, which logs a harmless but alarming
        // "Failed executing DbCommand". Creating the table first keeps a first deploy's log clean.
        // A "scope" gives us scoped services (like the DbContext) outside of a web request.
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync();
        // Apply every migration not yet applied (EF logs each one it runs).
        await db.Database.MigrateAsync();
        app.Logger.LogInformation("Database is up to date ({Count} migrations applied)", (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    // DerivedContextFactory: adapts "a factory of PostgresDbContext" into "a factory of ApplicationDbContext".
    // Works because PostgresDbContext IS an ApplicationDbContext (it inherits from it).
    private sealed class DerivedContextFactory<TContext>(IDbContextFactory<TContext> inner) : IDbContextFactory<ApplicationDbContext>
        where TContext : ApplicationDbContext
    {
        public ApplicationDbContext CreateDbContext() => inner.CreateDbContext();
    }
}
