using System;
using System.Security.Cryptography;
using System.Text;
using Medicount.Chatbot.Configuration;

namespace Medicount.Chatbot.Services;

public static class VisitorConfirmation
{
    internal const string Kind = "visitor_confirmation";
    internal const string Subject = "We received your message";
    internal const string Body =
        "Thank you for contacting us. We received your submission.\r\n\r\n" +
        "For assistance, please return to the website where you started the chat. " +
        "Please do not send personal or confidential information by replying to this email.";

    internal static bool ValidRecipient(string value) =>
        value.Length <= 254 && ChatOptions.Mailbox(value) && !value.Contains('\r') && !value.Contains('\n');

    // The SQL outbox uses a uniqueidentifier. Derive a separate stable key from the request.
    internal static string Key(string sessionId, string requestId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Kind}:{sessionId}:{requestId}"));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
    internal static string BuildBody(string reference, DateTimeOffset? submittedAtUtc)
    {
        var body = new StringBuilder();
        body.AppendLine("Thank you for contacting us. We received your submission.");
        body.AppendLine();
        body.AppendLine($"Confirmation reference: {reference}");
        body.AppendLine();
        if (submittedAtUtc is { } submitted)
        {
            body.AppendLine(FormattableString.Invariant(
                $"Submitted (UTC): {submitted.UtcDateTime:yyyy-MM-dd HH:mm:ss}"));
            body.AppendLine();
        }
        body.AppendLine();
        body.AppendLine("For assistance, please return to the website where you started the chat.");
        body.AppendLine("Please do not send personal or confidential information by replying to this email.");
        return body.ToString();
    }
}
