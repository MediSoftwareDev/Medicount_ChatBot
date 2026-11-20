using Microsoft.Extensions.Configuration;
using System;
using System.Linq;
using System.Security.Cryptography;
namespace Medicount.Chatbot.Configuration;

public sealed class ChatOptions
{
    public string Mode { get; init; } = "development";
    public string Origin { get; init; } = "https://localhost";
    public string[] Parents { get; init; } = ["https://localhost"];
    public string Store { get; init; } = "memory";
    public string HashSecret { get; init; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string BillingMode { get; init; } = "disabled";
    public string BillingBaseUrl { get; init; } = "";
    public string BillingToken { get; init; } = "";
    public string Email { get; init; } = "disabled";
    public bool VisitorEmailEnabled { get; init; }
    public string Queue { get; init; } = "";
    public string DisputeQueue { get; init; } = "";
    public int RateLimit { get; init; } = 80;
    public long IdleMs { get; init; } = 30 * 60_000;
    public long AbsoluteMs { get; init; } = 8 * 60 * 60_000;
    public string DataRoot { get; init; } = AppContext.BaseDirectory;
    public string? SqlConnection { get; init; }
    public string SmtpUser { get; init; } = "";
    public string SmtpPassword { get; init; } = "";
    public string SmtpFrom { get; init; } = "";
    public string GraphTenant { get; init; } = "";
    public string GraphClient { get; init; } = "";
    public string GraphSecret { get; init; } = "";
    public string GraphMailbox { get; init; } = "";
    public string[] StaffEmailCc { get; init; } = [];
    public string[] StaffEmailBcc { get; init; } = [];
    public static ChatOptions Load(IConfiguration c)
    {
        string Get(string key, string fallback = "") => c[key] ?? fallback;
        var mode = Get("APP_ENV", "development");
        if (mode == "production")
            throw new InvalidOperationException("Production blocked. Complete billing integration, SQL, WordPress, business and compliance release gates first.");
        
        if (mode is not ("development" or "test" or "staging")) 
            throw new InvalidOperationException("Invalid APP_ENV");

        if (Get("LLM_INTENT", "off") != "off") 
            throw new InvalidOperationException("LLM intent is not supported.");

        var origin = Get("PUBLIC_ORIGIN", "https://localhost");

        var parents = Get("PARENT_ORIGINS", origin).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var item in parents.Append(origin))
            if (!Uri.TryCreate(item, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != item || uri.UserInfo.Length > 0 || uri.Scheme is not ("https" or "http") || (mode == "staging" && uri.Scheme != "https"))
                throw new InvalidOperationException("Exact HTTPS origins are required for staging.");

        var store = Get("STORE", "memory");
        var secret = Get("ACCOUNT_HASH_SECRET");
        
        if (store is not ("sql" or "memory") || (mode == "staging" && (store != "sql" || secret.Length < 32))) throw new InvalidOperationException("Staging requires SQL and an approved hash secret.");
        var billing = Get("BILLING_MODE", "disabled");
        
        if (billing is not ("disabled" or "live" or "fixture") || (billing == "fixture" && mode != "test")) throw new InvalidOperationException("Invalid billing mode; fixtures are test-only.");
        var baseUrl = Get("BILLING_API_BASE_URL").TrimEnd('/'); 
        var token = Get("BILLING_API_TOKEN");
        
        if (billing == "live" && (Get("BILLING_API_CONTRACT") != "medicount-chat-v1" || string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0))
            throw new InvalidOperationException("Live billing requires the approved HTTPS bridge contract and protected token.");
        var email = Get("EMAIL_TRANSPORT", "smtp");
        if (!bool.TryParse(Get("VISITOR_EMAIL_ENABLED", "false"), out var visitorEmailEnabled))
            throw new InvalidOperationException("Invalid VISITOR_EMAIL_ENABLED setting.");
        //if (visitorEmailEnabled && email == "disabled")
        //    //await QueueVisitorConfirmation(s, current.Id);
        //throw new InvalidOperationException("Visitor email requires SMTP or Graph.");
        var queue = Get("ESCALATION_EMAIL");//, "ptrelations@medicount.com");
        var dispute = Get("ESCALATION_DISPUTE_EMAIL", queue);
        if (string.IsNullOrWhiteSpace(dispute)) dispute = queue;
        
        if (email is not ("disabled" or "smtp" or "graph"))
            throw new InvalidOperationException("Invalid email transport.");

        if (email != "disabled" && (!Mailbox(queue) || !Mailbox(dispute)))
            throw new InvalidOperationException("Approved request destinations required.");
        var user = Get("SMTP_USERNAME");
        var from = Get("SMTP_FROM_EMAIL");
        var password = Get("SMTP_PASSWORD");
        if (email == "smtp" && (Get("SMTP_HOST", "smtp.office365.com") != "smtp.office365.com" || Get("SMTP_PORT", "587") != "587" || Get("SMTP_SECURITY", "starttls") != "starttls" || !Mailbox(user) || !Mailbox(from) || !user.Equals(from, StringComparison.OrdinalIgnoreCase) || password.Length == 0 || password.Any(ch => ch is '\r' or '\n' or '\0')))
            throw new InvalidOperationException("SMTP requires the approved mailbox and Office 365 STARTTLS configuration.");
        if (email == "graph" && (!System.Text.RegularExpressions.Regex.IsMatch(Get("GRAPH_TENANT_ID"), "^[a-zA-Z0-9-]+$") || Get("GRAPH_CLIENT_ID").Length == 0 || Get("GRAPH_CLIENT_SECRET").Length == 0 || !Mailbox(Get("GRAPH_MAILBOX"))))
            throw new InvalidOperationException("Incomplete Graph configuration.");
        var limit = mode == "test" ? Get("TEST_RATE_LIMIT", "80") : "80";
        if (!int.TryParse(limit, out var rate) || rate is < 1 or > 10000)
            throw new InvalidOperationException("Invalid test rate limit.");
        string[] ReadStaffRecipients(string key)
        {
        var addresses = c.GetSection(key).GetChildren()
            .Select(item => item.Value?.Trim() ?? "")
            .ToArray();

        if (addresses.Any(address => !Mailbox(address) ||
            address.Contains('\r') || address.Contains('\n')))
        {
            throw new InvalidOperationException(
                $"{key} must contain valid mailbox addresses.");
        }

            return addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return new ChatOptions
        {
            Mode = mode,
            Origin = origin,
            Parents = parents,
            Store = store,
            HashSecret = secret.Length > 0 ? secret : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            BillingMode = billing,
            BillingBaseUrl = baseUrl,
            BillingToken = token,
            Email = email,
            VisitorEmailEnabled = visitorEmailEnabled,
            Queue = queue,
            DisputeQueue = dispute,
            RateLimit = rate,
            DataRoot = Get("CHAT_DATA_ROOT", AppContext.BaseDirectory),
            SqlConnection = store == "sql" ? SqlSettings.ConnectionString(c) : null,
            SmtpUser = user,
            SmtpFrom = from,
            SmtpPassword = password,
            GraphTenant = Get("GRAPH_TENANT_ID"),
            GraphClient = Get("GRAPH_CLIENT_ID"),
            GraphSecret = Get("GRAPH_CLIENT_SECRET"),
            GraphMailbox = Get("GRAPH_MAILBOX"),
            StaffEmailCc = ReadStaffRecipients("STAFF_EMAIL_CC"),
            StaffEmailBcc = ReadStaffRecipients("STAFF_EMAIL_BCC"),

        };

    }
    
    public static bool Mailbox(string value) => System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,63}$");
}
public static class SqlSettings
{
    public static string ConnectionString(IConfiguration c)
    {
        // Every SQL consumer must resolve the same configuration. Environment variable
        // ConnectionStrings__ChatDb overrides the JSON value under ASP.NET Core.
        var configured = c.GetConnectionString("ChatDb");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured);
            if (string.IsNullOrWhiteSpace(connection.DataSource) || string.IsNullOrWhiteSpace(connection.InitialCatalog))
                throw new InvalidOperationException("ChatDb requires a SQL server and database.");
            connection.ConnectTimeout = Math.Clamp(connection.ConnectTimeout, 1, 15);
            connection.PersistSecurityInfo = false;
            connection.ApplicationName = "Medicount.Chatbot";
            return connection.ConnectionString;
        }
        var host = c["SQL_SERVER"]; var database = c["SQL_DATABASE"]; var user = c["SQL_USER"]; var password = c["SQL_PASSWORD"];
        if (new[] { host, database, user, password }.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("Set ConnectionStrings__ChatDb, or provide all legacy SQL_SERVER, SQL_DATABASE, SQL_USER and SQL_PASSWORD settings.");
        if (host!.Any(ch => ch is '\\' or ',' or ';' || char.IsControl(ch))) throw new InvalidOperationException("Use a certificate-matching SQL hostname and separate SQL_PORT.");
        if (!int.TryParse(c["SQL_PORT"] ?? "1433", out var port) || port is < 1 or > 65535) throw new InvalidOperationException("Invalid SQL_PORT.");
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{host},{port}",
            InitialCatalog = database,
            UserID = user,
            Password = password,
            Encrypt = Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = false,
            ConnectTimeout = 10,
            MaxPoolSize = 10,
            ApplicationName = "Medicount.Chatbot",
            PersistSecurityInfo = false
        };
        return builder.ConnectionString;
    }
}
