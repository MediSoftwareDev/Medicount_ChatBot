using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Medicount.Chatbot.Domain;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
public record Field(string Id, string Label, bool Required, string? Inputmode = null, string? Mask = null, string? Type = null, int? MaxLength = null);
public record Target(string Flow, string? State = null);
public record Choice(string Label, Target Target);
public record FormDefinition(string Title, Field[] Fields, string Kind, string Summary, string? Sla = null);
public record FormState(string Id, FormDefinition Definition, string Purpose);
public record LinkCard(string Title, string Url, string Note);
public record FlowState(
    string[]? Say = null,
    string? Tone = null,
    Choice[]? Buttons = null,
    string? Next = null,
    FormDefinition? Form = null,
    LinkCard? LinkCard = null,
    string? Effect = null,
    string? Endpoint = null,
    string? Event = null,
    bool Closure = false
    );
public record Flow(string Id, string Title, string Auth, string Entry, Dictionary<string, FlowState> States);
public record Visitor(string Name, string? Email = null, string? Phone = null);
public record Verified(string ClientId, string AccountKey, bool Placed);
public record LastPayment(string Date, string Amount);
public record Balance(string BalanceValue, string Status, string MaskedAccount, string DateOfService, LastPayment? LastPayment = null);
public record ClaimStatus(string Payer, string SentDate, string ProcessedDate, string Outcome);
public record Agency(string Name, string Phone, string Hours);
public class Session
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TokenHash { get; set; } = "";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long LastActivity { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string Status { get; set; } = "active";
    public Visitor? Visitor { get; set; }
    public bool VisitorEmailOptIn { get; set; }
    public Verified? Verified { get; set; }
    public string? VerifiedPatientName { get; set; }
    public int Attempts { get; set; }
    public bool Professional { get; set; }
    public Target? Pending { get; set; }
    public Dictionary<string, Target> Actions { get; set; } = [];
    public FormState? Form { get; set; }
    public string? LastReference { get; set; }
    public string? Question { get; set; }
}
public class BotResponse
{
    public string? DiagnosticTraceId { get; set; }
    public List<object> Messages { get; set; } = [];
    public string? Status { get; set; }
    public bool? RetryForm { get; set; }
    public string? ReviewToken { get; set; }
    public string? VisitorEcho { get; set; }
    public SessionTiming? SessionTiming { get; set; }
    public string? Token { get; set; }
    public string? Mode { get; set; }
}
public record SessionTiming(long IdleMs, long AbsoluteMs);
public record Transcript(string Who, string Body, string Kind = "text");
public record Escalation(long Id, string SessionId, string Reference, string Key, string Kind, string Queue, string Payload,
    bool Sent = false, int Attempts = 0, long LeaseUntil = 0, long NextAttempt = 0);
public sealed class RequestError(int status = 400, string code = "INVALID_REQUEST") : Exception(code)
{ 
    public int Status { get; } = status; 
    public string Code { get; } = code;
}
