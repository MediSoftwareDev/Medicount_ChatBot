using Medicount.Chatbot.Configuration;
using Medicount.Chatbot.Domain;
using Medicount.Chatbot.Diagnostics;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace Medicount.Chatbot.Services;

public interface IBillingGateway
{
    string Mode { get; }
    Task<Verified?> Verify(string name, string dob, string account);
    Task<bool> Placement(Verified identity); 
    Task<Balance> Balance(Verified identity);
    Task<ClaimStatus> Claim(Verified identity);
    Task<Agency> Agency(Verified identity); Task<bool> Statement(Verified identity, string kind, string requestId);
}
public static class UpstreamJson
{
    public static async Task<JsonElement> Read(HttpResponseMessage response, int max = 65536)
    {
        if (response.Content.Headers.ContentLength > max) throw new InvalidOperationException("UPSTREAM_RESPONSE_INVALID");
        await using var stream = await response.Content.ReadAsStreamAsync(); using var buffer = new MemoryStream();
        byte[] bytes = new byte[4096]; int count;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while ((count = await stream.ReadAsync(bytes, deadline.Token)) > 0) { if (buffer.Length + count > max) throw new InvalidOperationException("UPSTREAM_RESPONSE_INVALID"); buffer.Write(bytes, 0, count); }
        using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 }); return json.RootElement.Clone();
    }
    public static void Shape(JsonElement value, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) 
            throw new InvalidOperationException("UPSTREAM_RESPONSE_INVALID");
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Distinct().Count() != names.Length 
            || required.Any(k => !names.Contains(k)) 
            || names.Any(k => !required.Contains(k) 
            && !optional.Contains(k))) 
            throw new InvalidOperationException("UPSTREAM_RESPONSE_INVALID");
    }
    public static string Text(JsonElement value, string key) { var p = value.GetProperty(key); if (p.ValueKind != JsonValueKind.String || p.GetString() is not { Length: > 0 and <= 500 } s) throw new InvalidOperationException("UPSTREAM_RESPONSE_INVALID"); return s; }
    public static bool Bool(JsonElement value, string key) { var p = value.GetProperty(key); if (p.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidOperationException("UPSTREAM_RESPONSE_INVALID"); return p.GetBoolean(); }
}
public sealed class BillingGateway(ChatOptions options, HttpClient http, AppDiagnostics? diagnostics = null) : IBillingGateway
{
    public string Mode => options.BillingMode;
    private Task<T> Trace<T>(string operation, Func<Task<T>> action) => diagnostics is null ? action() : diagnostics.RunAsync(operation, action);
    public Task<Verified?> Verify(string name, string dob, string account) => Trace("billing.verify", () => VerifyCore(name, dob, account));
    public Task<bool> Placement(Verified identity) => Trace("billing.placement", () => PlacementCore(identity));
    public Task<Balance> Balance(Verified identity) => Trace("billing.balance", () => BalanceCore(identity));
    public Task<ClaimStatus> Claim(Verified identity) => Trace("billing.claim", () => ClaimCore(identity));
    public Task<Agency> Agency(Verified identity) => Trace("billing.agency", () => AgencyCore(identity));
    public Task<bool> Statement(Verified identity, string kind, string requestId) => Trace("billing.statement", () => StatementCore(identity, kind, requestId));
    private JsonElement[]? fixtures;
    private JsonElement[] Fixtures => fixtures ??= JsonDocument.Parse(File.ReadAllText(Path.Combine(options.DataRoot, "fixtures", "accounts.json"))).RootElement.EnumerateArray().Select(v => v.Clone()).ToArray();
    private JsonElement Account(Verified i) => Fixtures.First(a => a.GetProperty("key").GetString() == i.AccountKey && a.GetProperty("clientId").GetString() == i.ClientId);
    private static object Scope(Verified i) => new { i.ClientId, i.AccountKey };
    private Task<JsonElement> Post(string path, object body, string? key = null) => Trace("billing.http", async () =>
    {
        if (Mode != "live")
        {
            diagnostics?.Write("billing.http", "disabled");
            throw new InvalidOperationException("BILLING_UNAVAILABLE");
        }

        var requestUri = new Uri(options.BillingBaseUrl.TrimEnd('/') + "/" + path.TrimStart('/'));
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(body, options: JsonDefaults.Options)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.BillingToken);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        diagnostics?.Write("billing.http", "response_received", statusCode: (int)response.StatusCode);
        if (response.StatusCode != HttpStatusCode.OK
            || response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new InvalidOperationException("BILLING_UNAVAILABLE");
        return await Trace("billing.json", () => UpstreamJson.Read(response));
    });
    private async Task<Verified?> VerifyCore(string name, string dob, string account)
    {
        if (Mode == "fixture") 
        {
            var a = Fixtures.FirstOrDefault(a => a.GetProperty("name").GetString()!.Equals(name.Trim(),
                StringComparison.OrdinalIgnoreCase) && 
                a.GetProperty("dob").GetString() == dob && 
                a.GetProperty("account").GetString()!.Equals(account.Trim(), 
                StringComparison.OrdinalIgnoreCase)); 
            return a.ValueKind == JsonValueKind.Undefined ? null : 
                new(a.GetProperty("clientId").GetString()!,
                a.GetProperty("key").GetString()!, 
                a.GetProperty("placed").GetBoolean());
        }
        var r = await Post("/verify-identity", new { name, dob, account }); 
        var ok = UpstreamJson.Bool(r, "ok"); 
        UpstreamJson.Shape(r, ok ? ["ok", "clientId", "accountKey", "placed"] : ["ok"]);
        return ok ? 
            new(UpstreamJson.Text(r, "clientId"), 
            UpstreamJson.Text(r, "accountKey"),
            UpstreamJson.Bool(r, "placed")) : null;
    }
    private async Task<bool> PlacementCore(Verified i) { if (Mode == "fixture") return Account(i).GetProperty("placed").GetBoolean(); var r = await Post("/placement", Scope(i)); UpstreamJson.Shape(r, ["placed"]); return UpstreamJson.Bool(r, "placed"); }
    private async Task<Balance> BalanceCore(Verified i)
    {
        JsonElement r;
        if (Mode == "fixture") { r = Account(i);
            if (r.GetProperty("placed").GetBoolean()) 
                throw new InvalidOperationException("PLACED"); 
        }
        else 
        { 
            r = await Post("/balance", Scope(i)); 
            UpstreamJson.Shape(r, ["balance", "status", "maskedAccount", "dateOfService"], "lastPayment"); 
        }
        var masked = UpstreamJson.Text(r, "maskedAccount"); 
        var service = UpstreamJson.Text(r, "dateOfService");
        if (!Regex.IsMatch(masked, @"^\*{4}[A-Za-z0-9]{1,4}$") || !DateOnly.TryParseExact(service, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) 
            throw new InvalidOperationException("UPSTREAM_RESPONSE_INVALID");
        LastPayment? last = null; 
        if (r.TryGetProperty("lastPayment", out var p))
        { 
            UpstreamJson.Shape(p, ["date", "amount"]); 
            last = new(UpstreamJson.Text(p, "date"), UpstreamJson.Text(p, "amount")); }
        return new(UpstreamJson.Text(r, "balance"), UpstreamJson.Text(r, "status"), masked, service, last);
    }
    private async Task<ClaimStatus> ClaimCore(Verified i) { var r = Mode == "fixture" ? Account(i).GetProperty("claim") : await Post("/claim-status", Scope(i)); UpstreamJson.Shape(r, ["payer", "sentDate", "processedDate", "outcome"]); return new(UpstreamJson.Text(r, "payer"), UpstreamJson.Text(r, "sentDate"), UpstreamJson.Text(r, "processedDate"), UpstreamJson.Text(r, "outcome")); }
    private async Task<Agency> AgencyCore(Verified i) { var r = Mode == "fixture" ? Account(i).GetProperty("agency") : await Post("/agency", Scope(i)); UpstreamJson.Shape(r, ["name", "phone", "hours"]); return new(UpstreamJson.Text(r, "name"), UpstreamJson.Text(r, "phone"), UpstreamJson.Text(r, "hours")); }
    private async Task<bool> StatementCore(Verified i, string kind, string requestId) { if (kind is not ("email" or "mail" or "itemized")) throw new InvalidOperationException("Invalid statement type"); if (Mode == "fixture") { Account(i); return true; } var r = await Post("/statement-requests", new { i.ClientId, i.AccountKey, kind }, requestId); UpstreamJson.Shape(r, ["accepted"]); return UpstreamJson.Bool(r, "accepted"); }
}
