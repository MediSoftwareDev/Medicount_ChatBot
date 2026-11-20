using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Medicount.Chatbot.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Medicount.Chatbot.Diagnostics;

// Deliberately accepts codes and numeric metadata, never arbitrary structured
// objects, exception messages, SQL text, URLs, headers, or patient values.
public sealed class AppDiagnostics
{
    private readonly object gate = new();
    private readonly AsyncLocal<string?> trace = new();
    private readonly string directory;
    private readonly bool enabled;
    private readonly long maxBytes;
    private readonly int maxFiles;
    private readonly TextWriter fallback;
    private readonly string instance = Guid.NewGuid().ToString("N")[..8];
    private string? currentFile;
    private int sequence;
    private long lastSinkFailure;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    public string? TraceId => trace.Value;

    public AppDiagnostics(IConfiguration configuration, string contentRoot, TextWriter? fallback = null)
    {
        this.fallback = fallback ?? Console.Error;
        enabled = !string.Equals(configuration["DIAGNOSTICS_ENABLED"], "false", StringComparison.OrdinalIgnoreCase);
        directory = Path.GetFullPath(Path.Combine(contentRoot, configuration["DIAGNOSTICS_LOG_DIRECTORY"] ?? "logs/application"));
        var webroot = Path.GetFullPath(Path.Combine(contentRoot, "wwwroot"));
        if (directory.Equals(webroot, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(webroot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Diagnostic logs must be outside wwwroot.");
        maxBytes = long.TryParse(configuration["DIAGNOSTICS_MAX_FILE_BYTES"], out var size) ? Math.Clamp(size, 1024, 100 * 1024 * 1024) : 10 * 1024 * 1024;
        maxFiles = int.TryParse(configuration["DIAGNOSTICS_MAX_FILES"], out var count) ? Math.Clamp(count, 2, 100) : 14;
    }

    public IDisposable PushTrace(string traceId)
    {
        var previous = trace.Value;
        trace.Value = Regex.IsMatch(traceId, "^[a-f0-9]{32}$") ? traceId : Guid.NewGuid().ToString("N");
        return new TraceScope(() => trace.Value = previous);
    }

    public void Write(string operation, string outcome, long? elapsedMs = null, Exception? exception = null, int? statusCode = null)
    {
        if (!enabled) return;
        try
        {
            var sql = Find<SqlException>(exception);
            var socket = Find<SocketException>(exception);
            var http = Find<HttpRequestException>(exception);
            var request = Find<RequestError>(exception);
            var record = new
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Level = exception is not null && (request is null || request.Status >= 500) ? "Error" : statusCode >= 400 || request is not null ? "Warning" : "Information",
                TraceId,
                Operation = Code(operation),
                Outcome = Code(outcome),
                ElapsedMs = elapsedMs is null ? null : (long?)Math.Max(0, elapsedMs.Value),
                StatusCode = statusCode,
                ExceptionType = exception is null ? null : Code(exception.GetType().Name),
                ErrorCode = request is null ? null : SafeRequestCode(request.Code),
                SqlNumber = sql?.Number,
                SqlState = sql?.State,
                SqlClass = sql?.Class,
                SocketError = socket is null ? null : (int?)socket.SocketErrorCode,
                UpstreamStatusCode = http?.StatusCode is null ? null : (int?)http.StatusCode,
                ProcessId = Environment.ProcessId,
                Build = typeof(AppDiagnostics).Assembly.GetName().Version?.ToString()
            };
            var line = JsonSerializer.Serialize(record, Json) + "\n";
            lock (gate)
            {
                Directory.CreateDirectory(directory);
                var bytes = Encoding.UTF8.GetByteCount(line);
                if (currentFile is null || !File.Exists(currentFile) || new FileInfo(currentFile).Length + bytes > maxBytes)
                {
                    currentFile = Path.Combine(directory, $"application-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}-{instance}-{sequence++:D5}.jsonl");
                    // Create the active file before pruning so the total limit includes it.
                    using (File.Create(currentFile)) { }
                    foreach (var old in Directory.EnumerateFiles(directory, "application-*.jsonl")
                        .Where(f => f != currentFile).OrderByDescending(File.GetLastWriteTimeUtc).Skip(maxFiles - 1))
                    {
                        try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
                File.AppendAllText(currentFile, line, new UTF8Encoding(false));
            }
        }
        catch (Exception error)
        {
            // Log failure must never replace a business result. Emit only safe
            // metadata to IIS stdout/stderr, throttled to once per minute.
            lock (gate)
            {
                var now = Environment.TickCount64;
                if (lastSinkFailure == 0 || now - lastSinkFailure >= 60_000)
                {
                    lastSinkFailure = now;
                    try { fallback.WriteLine("DIAGNOSTIC_LOG_WRITE_FAILED type=" + Code(error.GetType().Name) + "; check logs directory permissions and disk space."); } catch { }
                }
            }
        }
    }

    public async Task<T> RunAsync<T>(string operation, Func<Task<T>> action)
    {
        using var scope = TraceId is null ? PushTrace(Guid.NewGuid().ToString("N")) : null;
        var timer = Stopwatch.StartNew();
        Write(operation, "started");
        try
        {
            var value = await action();
            Write(operation, "succeeded", timer.ElapsedMilliseconds);
            return value;
        }
        catch (Exception error)
        {
            Write(operation, error is OperationCanceledException ? "cancelled" : "failed", timer.ElapsedMilliseconds, error);
            throw;
        }
    }
    public Task RunAsync(string operation, Func<Task> action) => RunAsync(operation, async () => { await action(); return true; });

    public static string Route(string? path) => path?.ToLowerInvariant() switch
    {
        "/api/session" => "chat.session", "/api/form" => "chat.form", "/api/prechat" => "chat.prechat",
        "/api/choose" => "chat.choose", "/api/message" => "chat.message", "/api/event" => "chat.event",
        "/api/keepalive" => "chat.keepalive", "/api/status" => "chat.status",
        "/api/patient/verifypatient" => "patient.verify", "/api/patient/updateinsurance" => "patient.insurance",
        "/healthz" => "health", "/" or "/preview" or "/preview.html" => "preview",
        "/widget" or "/widget.html" => "widget", "/widget.js" or "/preview.js" or "/medicount-chat.js" => "asset.script",
        "/medicount-chat.css" or "/preview.css" => "asset.style", "/assets/medicount-brand-mark.png" => "asset.image",
        _ => "unmatched"
    };
    private static string Code(string? value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9_.-]{1,100}$") ? value : "redacted";
    private static string SafeRequestCode(string value) => value switch
    {
        "INVALID_REQUEST" or "REQUEST_REJECTED" or "SESSION_UNAVAILABLE" or "SESSION_EXPIRED" or
        "STALE_ACTION" or "STALE_FORM" or "CHECK_FIELDS" or "REVIEW_REQUIRED" or "VERIFICATION_REQUIRED" or
        "REQUEST_CONFLICT" or "RATE_LIMITED" => value,
        _ => "REQUEST_ERROR"
    };
    private static T? Find<T>(Exception? error) where T : Exception
    {
        for (var count = 0; error is not null && count < 8; count++, error = error.InnerException)
            if (error is T match) return match;
        return null;
    }
    private sealed class TraceScope(Action restore) : IDisposable
    {
        private Action? restore = restore;
        public void Dispose() => Interlocked.Exchange(ref restore, null)?.Invoke();
    }
}
