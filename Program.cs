using Medicount.Chatbot.Configuration;
using Medicount.Chatbot.Data;
using Medicount.Chatbot.Domain;
using Medicount.Chatbot.Services;
using Medicount.Chatbot.Diagnostics;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;


var builder = WebApplication.CreateBuilder(args);
var diagnostics = new AppDiagnostics(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(diagnostics);
diagnostics.Write("startup", "starting");
builder.Services.AddControllers();
builder.Services.AddHostedService<Maintenance>();
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http", LogLevel.None);
builder.Logging.AddFilter("Microsoft.Data.SqlClient", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore.HttpLogging", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 16384; });
builder.Services.AddSingleton(sp => ChatOptions.Load(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<FlowCatalog>();
builder.Services.AddSingleton<SessionMutex>();
builder.Services.AddSingleton<RequestLimits>();

builder.Services.AddSingleton<IStore>(sp =>
{
    var options = sp.GetRequiredService<ChatOptions>();

    return options.Store == "sql"
        ? new SqlStore(options, sp.GetRequiredService<AppDiagnostics>())
        : new MemoryStore();
});

builder.Services.AddScoped<PatientVerificationRepository>();

builder.Services.AddScoped<ConversationEngine>();
builder.Services.AddSingleton(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) });
builder.Services.AddSingleton<IBillingGateway, BillingGateway>();
builder.Services.AddSingleton<IMailTransport, MailTransport>();
builder.Services.AddSingleton<Outbox>();
builder.Services.AddSingleton<ChatFormRepository>();
builder.Services.AddDistributedMemoryCache();

builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(15);
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.IsEssential = true;
});
builder.Services.AddScoped<BillingApiService>();

var app = builder.Build();
ChatOptions options;
var startupPhase = "startup.configuration";
try
{
    StartupChecks.ValidatePlaceholders(builder.Configuration, diagnostics);
    options = app.Services.GetRequiredService<ChatOptions>();
    diagnostics.Write(startupPhase, "valid");
    diagnostics.Write("configuration.sql_source", string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("ChatDb")) ? "legacy_settings" : "chatdb");
    diagnostics.Write("configuration.store", options.Store == "sql" ? "sql" : "memory");
    diagnostics.Write("configuration.billing", options.BillingMode == "live" ? "live" : "disabled");
    diagnostics.Write("configuration.email", options.Email == "disabled" ? "disabled" : "configured");
    startupPhase = "startup.flow_catalog";
    _ = app.Services.GetRequiredService<FlowCatalog>();
    diagnostics.Write(startupPhase, "loaded");
}
catch (Exception error)
{
    diagnostics.Write(startupPhase, "failed", exception: error);
    // Do not let an unhandled configuration exception print a secret-containing
    // connection-string parsing message to IIS stdout.
    throw new InvalidOperationException("Chatbot startup failed. Check application diagnostics for the failed startup stage and replace required configuration values.");
}
app.Lifetime.ApplicationStarted.Register(() => diagnostics.Write("startup", "ready"));
app.Lifetime.ApplicationStopping.Register(() => diagnostics.Write("shutdown", "stopping"));
if (options.Mode == "staging")
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1
    });
app.Use(async (context, next) =>
{
    var traceId = Guid.NewGuid().ToString("N");
    context.TraceIdentifier = traceId;
    using var traceScope = diagnostics.PushTrace(traceId);
    var timer = Stopwatch.StartNew();
    var route = AppDiagnostics.Route(context.Request.Path.Value);
    var method = context.Request.Method is "GET" or "POST" or "OPTIONS" or "HEAD" ? context.Request.Method.ToLowerInvariant() : "other";
    var operation = "http." + method + "." + route;
    var cancelled = false;
    diagnostics.Write(operation, "started");
    var h = context.Response.Headers;
    h["X-Trace-Id"] = traceId;
    h.CacheControl = "no-store";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    h["X-Content-Type-Options"] = "nosniff";
    h["Cross-Origin-Resource-Policy"] = "cross-origin";
    h.ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; font-src 'self'; frame-src 'self'; " +
        $"frame-ancestors 'self' {string.Join(' ', options.Parents)}; " +
        "form-action 'none'; base-uri 'none'; object-src 'none'" +
        (options.Mode == "staging" ? "; upgrade-insecure-requests" : "");
    if (options.Mode == "staging")
        h.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
    var origin = context.Request.Headers.Origin.ToString();
    var allowed = options.Parents.Append(options.Origin).Contains(origin, StringComparer.Ordinal);
    if (allowed)
    {
        h.AccessControlAllowOrigin = origin;
        h.Vary = "Origin";
        h.AccessControlAllowMethods = "POST, GET";
        h.AccessControlAllowHeaders = "Content-Type, Authorization";
        h.AccessControlExposeHeaders = "X-Trace-Id";
    }
    try
    {
        if (context.Request.Method == "OPTIONS")
        {
            context.Response.StatusCode = allowed ? 204 : 403; return;
        }
        if (context.Request.Method == "POST" && !allowed)
        {
            diagnostics.Write("http.origin", "rejected", statusCode: 403);
            throw new RequestError(403, "REQUEST_REJECTED");
        }
        app.Services.GetRequiredService<RequestLimits>().Check("ip:" + context.Connection.RemoteIpAddress, options.RateLimit);
        await next();
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        cancelled = true;
        diagnostics.Write(operation, "client_cancelled", timer.ElapsedMilliseconds, statusCode: 499);
        return;
    }
    catch (Exception ex)
    {
        var status = ex is RequestError re ? re.Status : ex is BadHttpRequestException br ? br.StatusCode : ex is JsonException ? 400 : 503;
        diagnostics.Write(operation, "failed", timer.ElapsedMilliseconds, ex, status);
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new
        {
            error = ex is RequestError e ? e.Code : status < 500 ? "INVALID_REQUEST" : "SERVICE_UNAVAILABLE",
            traceId = context.TraceIdentifier
        },
            JsonDefaults.Options);
    }
    finally
    {
        diagnostics.Write(operation, "completed", timer.ElapsedMilliseconds, statusCode: cancelled ? 499 : context.Response.StatusCode);
    }
});
app.UseRouting();

app.UseSession();



app.UseStaticFiles();
app.MapControllers();
try { app.Run(); }
catch (Exception error)
{
    diagnostics.Write("host", "failed", exception: error);
    throw new InvalidOperationException("Chatbot host failed. Check application diagnostics and IIS startup logs.");
}

public partial class Program { }

public static class ApiBody
{
    //public static string TokenHash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static string TokenHash(string token) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
           .ToLowerInvariant();


    public static async Task<JsonElement> Read(HttpContext c, bool empty = false)
    {
        const int maxBodySize = 16384;

        if (c.Request.ContentLength > maxBodySize)
            throw new RequestError(413);

        if (c.Request.ContentLength == 0 && empty)
            return JsonSerializer.SerializeToElement(new { });

        if (!string.Equals(
            c.Request.ContentType?.Split(';')[0].Trim(),
            "application/json",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new RequestError(415);
        }

        using var stream = new MemoryStream();

        byte[] bytes = new byte[4096];
        int count;

        while ((count = await c.Request.Body.ReadAsync(
            bytes,
            c.RequestAborted)) > 0)
        {
            if (stream.Length + count > maxBodySize)
                throw new RequestError(413);

            stream.Write(bytes, 0, count);
        }

        // Reject empty JSON requests.
        if (stream.Length == 0)
        {
            if (empty)
                return JsonSerializer.SerializeToElement(new { });

            throw new RequestError(400);
        }

        try
        {
            using var doc = JsonDocument.Parse(
                stream.ToArray(),
                new JsonDocumentOptions
                {
                    MaxDepth = 8
                });

            // Clone before JsonDocument is disposed.
            JsonElement result = doc.RootElement.Clone();

            return result;
        }
        catch (JsonException)
        {
            throw new RequestError(400);
        }
    }

    public static void Shape(JsonElement value, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new RequestError();
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Distinct().Count() != names.Length || required.Any(k => !names.Contains(k)) || names.Any(k => !required.Contains(k) && !optional.Contains(k)))
            throw new RequestError();
    }
    private static void Str(JsonElement value, string key, int max, int min = 1) { var p = value.GetProperty(key); if (p.ValueKind != JsonValueKind.String || p.GetString()!.Length < min || p.GetString()!.Length > max) throw new RequestError(); }
    public static void Validate(string path, JsonElement value)
    {
        switch (path)
        {
            case "choose": Shape(value, ["buttonId"]); Str(value, "buttonId", 36); break;
            case "message": Shape(value, ["text"]); Str(value, "text", 2000); break;
            case "form":
            case "prechat":
                Shape(value, ["formId", "values", "confirmed"], "reviewToken");
                Str(value, "formId", 36);
                if (value.TryGetProperty("reviewToken", out _)) Str(value, "reviewToken", 64);
                if (value.GetProperty("confirmed").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new RequestError();
                var values = value.GetProperty("values");
                if (values.ValueKind != JsonValueKind.Object) throw new RequestError();
                var properties = values.EnumerateObject().ToArray();
                if (properties.Length > 15 || properties.Select(p => p.Name).Distinct().Count() != properties.Length)
                    throw new RequestError();
                foreach (var p in properties)
                    Str(values, p.Name, 2000, 0);
                break;
            case "event":
                Shape(value, ["name"], "data"); Str(value, "name", 50);
                if (value.GetProperty("name").GetString()
                    is not ("hosted_payment_opened" or "entries_review_shown" or "entries_edited")
                    )
                    throw new RequestError();
                if (value.TryGetProperty("data", out var data))
                    Shape(data, []);
                break;
            default:
                Shape(value, []);
                break;
        }
    }
}
public sealed class RequestLimits
{
    private readonly object gate = new();
    private readonly Dictionary<string, (long At, int Count)> counters = [];
    public void Check(string key, int limit)
    {
        lock (gate)
        {
            var now = Environment.TickCount64;
            if (counters.Count > 10000)
                foreach (var pair in counters.Where(p => now - p.Value.At >= 60000).ToArray()) counters.Remove(pair.Key);
            var old = counters.GetValueOrDefault(key);
            var row = now - old.At < 60000 ? (old.At, old.Count + 1) : (now, 1);
            counters[key] = row;
            if (row.Item2 > limit)
                throw new RequestError(429, "RATE_LIMITED");
        }
    }
}


