using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace Medicount.Chatbot.Services;

// Integration seam for the supplied IVR API. Intentionally not registered in the patient-facing engine:
// the normalized bridge contract still needs client/collections policy and statement/claim endpoints.
public sealed record IvrAccount(string PatientId, string PatientName, string DateOfBirth, string AccountBalance, string? ClientId, bool? IsInCollections)
{
    public (string ClientId, bool Placed) RequireScope() => ClientId is not null && IsInCollections is not null ? (ClientId, IsInCollections.Value) : throw new InvalidOperationException("BILLING_SCOPE_UNAVAILABLE");
    public bool Matches(string name, string dob, string patientId)
    {
        static string Normalize(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ").ToLowerInvariant();
        return DateOnly.TryParseExact(dob, "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == DateOfBirth && PatientId == patientId.Trim() && Normalize(PatientName) == Normalize(name) && Normalize(name).Length > 0;
    }
    public static IvrAccount Parse(JsonElement raw, string requestedId)
    {
        string Take(string key, int max) 
        { var p = raw.GetProperty(key); 
            if (p.ValueKind != JsonValueKind.String 
                || p.GetString() is not { } s 
                || string.IsNullOrWhiteSpace(s) 
                || s.Length > max 
                || s.Any(c => c is '\r' or '\n' or '\0'))
                throw new InvalidOperationException("BILLING_UNAVAILABLE"); return s.Trim();
        }
        var id = Take("PatientID", 100); 
        if (id != requestedId.Trim()) 
            throw new InvalidOperationException("BILLING_UNAVAILABLE");
        var dob = Take("DOB", 50); 
        if (!Regex.IsMatch(dob, @"^\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})?)?$") 
            || !DateOnly.TryParseExact(dob[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) 
            || date.Year < 1900 || date > DateOnly.FromDateTime(DateTime.UtcNow))
            throw new InvalidOperationException("BILLING_UNAVAILABLE");
        string? client = raw.TryGetProperty("ClientID", out var c) && c.ValueKind != JsonValueKind.Null ? Take("ClientID", 100) : null;
        bool? placed = raw.TryGetProperty("IsInCollections", out var p) && p.ValueKind != JsonValueKind.Null ? UpstreamJson.Bool(raw, "IsInCollections") : null;
        return new(id, Take("PatientName", 200), dob[..10], Take("AccountBalance", 100), client, placed);
    }
}
public sealed class MedicountIvrClient : IDisposable
{
    private readonly HttpClient http; private readonly string origin; private readonly string? accessToken; private readonly Dictionary<string, string> form; private readonly string method, paymentPath;
    private readonly SemaphoreSlim tokenLock = new(1); private string? cached; private DateTime expires;
    public MedicountIvrClient(IConfiguration configuration, HttpMessageHandler? handler = null)
    {
        origin = configuration["IVR_BASE_URL"] ?? "https://ivrsandboxapi.medicount.com";
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.GetLeftPart(UriPartial.Authority) != origin || uri.UserInfo.Length > 0) throw new InvalidOperationException("IVR requires an HTTPS origin.");
        accessToken = configuration["IVR_ACCESS_TOKEN"]; form = JsonSerializer.Deserialize<Dictionary<string, string>>(configuration["IVR_TOKEN_FORM_JSON"] ?? "{}")!;
        if (string.IsNullOrEmpty(accessToken) == (form.Count == 0) || form.Count > 20 || form.Any(p => string.IsNullOrWhiteSpace(p.Key) || string.IsNullOrWhiteSpace(p.Value) || p.Key.Concat(p.Value).Any(char.IsControl)) || (form.Count > 0 && !form.ContainsKey("grant_type")) || accessToken?.Any(char.IsWhiteSpace) == true) throw new InvalidOperationException("Configure exactly one protected IVR access token or token form.");
        method = configuration["IVR_TOKEN_METHOD"] ?? "GET"; paymentPath = configuration["IVR_PAYMENT_PATH"] ?? "/api/Hoimageme/UpdatePaymentDetails";
        if (method is not ("GET" or "POST") || paymentPath is not ("/api/Hoimageme/UpdatePaymentDetails" or "/api/home/UpdatePaymentDetails")) throw new InvalidOperationException("Invalid IVR route configuration.");
        http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };
    }
    private async Task<JsonElement> Send(HttpRequestMessage request)
    { 
        using (request)
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)) 
        { 
            if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType is not ("application/json" or "text/json")) 
                throw new InvalidOperationException("BILLING_UNAVAILABLE"); return await UpstreamJson.Read(response);
        } 
    }
    private async Task<string> Token()
    {
        if (!string.IsNullOrEmpty(accessToken)) return accessToken; await tokenLock.WaitAsync();
        try
        {
            if (cached is not null && expires > DateTime.UtcNow) return cached;
            var raw = await Send(new(new HttpMethod(method), origin + "/token") { Content = new FormUrlEncodedContent(form) });
            var token = raw.GetProperty("access_token").GetString(); if (string.IsNullOrEmpty(token) || token.Length > 16384 || token.Any(char.IsWhiteSpace) || raw.GetProperty("token_type").GetString()?.ToLowerInvariant() != "bearer") throw new InvalidOperationException("BILLING_UNAVAILABLE");
            var ttl = raw.TryGetProperty("expires_in", out var p) ? p.GetDouble() : 0; if (!double.IsFinite(ttl) || ttl < 0 || (raw.TryGetProperty("expires_in", out _) && ttl == 0)) throw new InvalidOperationException("BILLING_UNAVAILABLE");
            cached = token; expires = DateTime.UtcNow.AddSeconds(Math.Max(0, Math.Min(ttl, 86400) - 30)); return token;
        }
        finally { tokenLock.Release(); }
    }
    private static string Required(string value, int max = 200) { if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(c => c is '\r' or '\n' or '\0')) throw new InvalidOperationException("INVALID_IVR_INPUT"); return value.Trim(); }
    private async Task<JsonElement> Request(string path, HttpMethod method, Dictionary<string, string> parameters)
    {
        var query = string.Join('&', parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(Required(p.Value))));
        var request = new HttpRequestMessage(method, origin + path + "?" + query); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Token());
        try { return await Send(request); } catch { cached = null; throw new InvalidOperationException("BILLING_UNAVAILABLE"); }
    }
    public async Task<IvrAccount> ReadAccount(string id) 
    { 
        id = Required(id, 100);
        return IvrAccount.Parse(await Request("/api/home/CheckAccountDetails", HttpMethod.Get, 
            new() { { "m_PatientID", id } }), id);
    }
    public async Task UpdateAddress(string id, string street, string city, string state, string zip)
    { var r = await Request("/api/home/UpdateAddressDetails", HttpMethod.Post, new() { { "m_PatientID", Required(id, 100) }, { "m_Street", street }, { "m_City", city }, { "m_State", state }, { "m_ZipCode", zip } }); if (r.ValueKind != JsonValueKind.String || r.GetString() != "Patient address details updated successfully") throw new InvalidOperationException("BILLING_UNAVAILABLE"); }
    public async Task UpdatePhone(string id, string phone)
    { if (!Regex.IsMatch(phone, @"^[+0-9\s().-]+$") || Regex.Replace(phone, "[^0-9]", "").Length is < 7 or > 15) throw new InvalidOperationException("INVALID_IVR_INPUT"); var r = await Request("/api/home/UpdatePhoneNumberDetails", HttpMethod.Post, new() { { "m_PatientID", Required(id, 100) }, { "m_phoneNumber", phone } }); if (r.ValueKind != JsonValueKind.String || r.GetString() != "Patient Phone number updated successfully") throw new InvalidOperationException("BILLING_UNAVAILABLE"); }
    // Only independently verified payment evidence may reach this method; never a chat self-report.
    public Task<JsonElement> UpdatePayment(string id, string payment, string comment) => Request(paymentPath, HttpMethod.Post, new() { { "m_PatientID", Required(id, 100) }, { "m_Payment", payment }, { "m_Comment", comment } });
    public void Dispose() { http.Dispose(); tokenLock.Dispose(); }
}
