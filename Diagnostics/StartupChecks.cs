using System;
using Microsoft.Extensions.Configuration;

namespace Medicount.Chatbot.Diagnostics;

public static class StartupChecks
{
    public static void ValidatePlaceholders(IConfiguration configuration, AppDiagnostics diagnostics)
    {
        var mode = configuration["APP_ENV"] ?? "development";
        diagnostics.Write("configuration.environment", mode is "development" or "test" or "staging" ? "supported" : "unsupported");
        if (mode == "staging")
        {
            diagnostics.Write("configuration.hash_secret", (configuration["ACCOUNT_HASH_SECRET"]?.Length ?? 0) >= 32 ? "length_valid" : "missing_or_short");
            diagnostics.Write("configuration.store", configuration["STORE"] == "sql" ? "staging_requirement_met" : "staging_requires_sql");
        }
        var invalid = false;
        var hasChatDb = !string.IsNullOrWhiteSpace(configuration.GetConnectionString("ChatDb"));
        // Ignore unused legacy values when the canonical connection string wins.
        var keys = hasChatDb ? new[] { "ConnectionStrings:ChatDb", "ACCOUNT_HASH_SECRET" }
            : new[] { "ACCOUNT_HASH_SECRET", "SQL_SERVER", "SQL_DATABASE", "SQL_USER", "SQL_PASSWORD" };
        foreach (var key in keys)
        {
            if (configuration[key]?.Contains("REPLACE_", StringComparison.OrdinalIgnoreCase) != true) continue;
            diagnostics.Write(key == "ACCOUNT_HASH_SECRET" ? "configuration.hash_secret" : "configuration.sql", "placeholder_not_replaced");
            invalid = true;
        }
        if (invalid) throw new InvalidOperationException("Replace the marked deployment placeholders before starting the application. See the application diagnostic log.");
    }
}
