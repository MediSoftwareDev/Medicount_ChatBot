using MailKit.Security;
using Medicount.Chatbot.Configuration;
using Medicount.Chatbot.Domain;
using Medicount.Chatbot.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
namespace Medicount.Chatbot.Services;

public interface IMailTransport { Task Send(Escalation row); }
public sealed class MailTransport(ChatOptions options, HttpClient http, AppDiagnostics? diagnostics = null) : IMailTransport
{
    private Task Trace(string operation, Func<Task> action) => diagnostics is null ? action() : diagnostics.RunAsync(operation, action);
    private Task<T> Trace<T>(string operation, Func<Task<T>> action) => diagnostics is null ? action() : diagnostics.RunAsync(operation, action);
    public Task Send(Escalation row) => Trace("mail.send", () => SendCore(row));
    private static string BuildVisitorBody(Escalation row)
    {
        using var payload = JsonDocument.Parse(row.Payload);
        var root = payload.RootElement;
        DateTimeOffset? submittedAtUtc = null;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("submittedAtUtc", out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.TryGetDateTimeOffset(out var timestamp))
        {
            submittedAtUtc = timestamp;
        }

        return VisitorConfirmation.BuildBody(row.Reference, submittedAtUtc);
    }
    private async Task SendCore(Escalation row)
    {
        //if (options.Email is not ("smtp" or "graph"))
        //    throw new InvalidOperationException("EMAIL_DISABLED");

        var visitor = row.Kind == VisitorConfirmation.Kind;
        if (visitor
            ? !options.VisitorEmailEnabled || !VisitorConfirmation.ValidRecipient(row.Queue)
            : row.Queue != options.Queue && row.Queue != options.DisputeQueue)
        {
            diagnostics?.Write("mail.send", "recipient_rejected");
            throw new InvalidOperationException("UNAPPROVED_RECIPIENT");
        }

        // The visitor branch never reads the internal reference or payload.
        var subject = visitor ? VisitorConfirmation.Subject : $"[Chatbot] {row.Kind} — {row.Reference}";
        var formattedBody = visitor
                        ? BuildVisitorBody(row)
                        : row.Kind == "insurance_update"
                            ? FormatInsuranceEmail(row.Reference, row.Payload)
                            : FormatEmailBody(row.Reference, row.Payload);


        if (options.Email == "smtp")
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(options.SmtpFrom));
            message.To.Add(MailboxAddress.Parse(row.Queue));
            if (!visitor)
            {
                var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    row.Queue
                };

                foreach (var address in options.StaffEmailCc)
                {
                    if (recipients.Add(address))
                        message.Cc.Add(MailboxAddress.Parse(address));
                }

                foreach (var address in options.StaffEmailBcc)
                {
                    if (recipients.Add(address))
                        message.Bcc.Add(MailboxAddress.Parse(address));
                }
            }
            message.Subject = subject;
            if (!visitor)
                message.MessageId = $"chatbot-{row.Reference}@medicount.com";
            message.Body = new TextPart("plain") { Text = formattedBody };
            using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 15000 };
            await Trace("mail.smtp.connect", () => client.ConnectAsync("smtp.office365.com", 587, SecureSocketOptions.StartTls));
            await Trace("mail.smtp.authenticate", () => client.AuthenticateAsync(options.SmtpUser, options.SmtpPassword));
            await Trace("mail.smtp.send", () => client.SendAsync(message));
            diagnostics?.Write("mail.smtp", "accepted");
            // A disconnect failure after SMTP acceptance must not mark the accepted message unsent.
            try
            {
                await Trace("mail.smtp.disconnect", () => client.DisconnectAsync(true));
            }
            catch (Exception ex) { diagnostics?.Write("mail.smtp.disconnect", "failed_after_acceptance", exception: ex); }
            return;
        }
        if (options.Email == "graph")
        {
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{Uri.EscapeDataString(options.GraphTenant)}/oauth2/v2.0/token") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { { "client_id", options.GraphClient }, { "client_secret", options.GraphSecret }, { "scope", "https://graph.microsoft.com/.default" }, { "grant_type", "client_credentials" } }) };
            using var tokenResponse = await Trace("mail.graph.authenticate", () => http.SendAsync(tokenRequest));
            diagnostics?.Write("mail.graph.authenticate", "response_received", statusCode: (int)tokenResponse.StatusCode);
            if (!tokenResponse.IsSuccessStatusCode) throw new InvalidOperationException("MAIL_UNAVAILABLE");
            var token = (await Trace("mail.graph.token_parse", () => UpstreamJson.Read(tokenResponse))).GetProperty("access_token").GetString()!;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.GraphMailbox)}/sendMail") { Content = JsonContent.Create(new { message = new { subject, body = new { contentType = "Text", content = formattedBody }, toRecipients = new[] { new { emailAddress = new { address = row.Queue } } } }, saveToSentItems = true }) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await Trace("mail.graph.send", () => http.SendAsync(request));
            diagnostics?.Write("mail.graph.send", "response_received", statusCode: (int)response.StatusCode);
            if (response.StatusCode != System.Net.HttpStatusCode.Accepted) throw new InvalidOperationException("MAIL_UNAVAILABLE");
            diagnostics?.Write("mail.graph", "accepted");
            return;
        }
        if (options.Email == "disabled")
        {
             var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(options.SmtpFrom));
            message.To.Add(MailboxAddress.Parse(row.Queue));
            if (!visitor)
            {
                var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    row.Queue
                };

                foreach (var address in options.StaffEmailCc)
                {
                    if (recipients.Add(address))
                        message.Cc.Add(MailboxAddress.Parse(address));
                }

                foreach (var address in options.StaffEmailBcc)
                {
                    if (recipients.Add(address))
                        message.Bcc.Add(MailboxAddress.Parse(address));
                }
            }
            message.Subject = subject;
            if (!visitor)
                message.MessageId = $"chatbot-{row.Reference}@medicount.com";
            message.Body = new TextPart("plain") { Text = formattedBody };
            using var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 15000 };
            await Trace("mail.smtp.connect", () => client.ConnectAsync("smtp.office365.com", 587, SecureSocketOptions.StartTls));
            await Trace("mail.smtp.authenticate", () => client.AuthenticateAsync(options.SmtpUser, options.SmtpPassword));
            await Trace("mail.smtp.send", () => client.SendAsync(message));
            diagnostics?.Write("mail.smtp", "accepted");
            try
            {
                await Trace("mail.smtp.disconnect", () => client.DisconnectAsync(true));
            }
            catch (Exception ex) { diagnostics?.Write("mail.smtp.disconnect", "failed_after_acceptance", exception: ex); }
            return;
        }
        throw new InvalidOperationException("EMAIL_DISABLED");
    }

    private static string FormatInsuranceEmail(string reference, string jsonPayload)
    {
        using var document = JsonDocument.Parse(jsonPayload);
        var root = document.RootElement;
        static string Value(JsonElement root, string property)
        {
            if (!root.TryGetProperty(property, out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return "Not provided";
            // Prevent user-entered line breaks from creating forged email headers/sections.
            var text = Regex.Replace(value.ToString(), @"[\r\n\t]+", " ").Trim();
            return text.Length == 0 ? "Not provided" : text;
        }
        var body = new StringBuilder();
        body.AppendLine("MEDICOUNT CHATBOT - INSURANCE UPDATE REQUEST");
        body.AppendLine("===========================================");
        body.AppendLine($"Reference: {reference}");
        body.AppendLine($"Submitted (UTC): {Value(root, "submittedAtUtc")}");
        body.AppendLine();
        body.AppendLine("PATIENT INFORMATION");
        body.AppendLine($"Patient name: {Value(root, "patientName")}");
        body.AppendLine($"Client ID: {Value(root, "clientId")}");
        body.AppendLine($"Account key: {Value(root, "accountKey")}");
        body.AppendLine($"Contact email: {Value(root, "contactEmail")}");
        body.AppendLine($"Contact phone: {Value(root, "contactPhone")}");
        body.AppendLine();
        body.AppendLine("INSURANCE UPDATE DETAILS");
        body.AppendLine($"Insurance company: {Value(root, "insuranceCompany")}");
        body.AppendLine($"Member ID: {Value(root, "memberId")}");
        body.AppendLine($"Group number: {Value(root, "groupNumber")}");
        body.AppendLine();
        body.AppendLine("Status: Submitted for billing team review (not yet applied to account)");
        body.AppendLine("Generated by Medicount Chatbot");
        body.AppendLine();
        return body.ToString();
    }

    private static string FormatEmailBody(string reference, string jsonPayload)
    {
        var sb = new StringBuilder();
        using var document = JsonDocument.Parse(jsonPayload);
        var root = document.RootElement;
        static string GetValue(JsonElement element, string key)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(key, out var value))
            {
                return value.ToString();
            }

            return "";
        }

        static string CleanText(string text)
        {
            string cleaned = Regex.Replace(
                text,
                "<[^>]*>",
                "",
                RegexOptions.None,
                TimeSpan.FromSeconds(1)
            );

            return WebUtility.HtmlDecode(cleaned);
        }

        sb.AppendLine("MEDICOUNT CHATBOT - REQUEST DETAILS");
        sb.AppendLine("===================================");
        sb.AppendLine();
        sb.AppendLine($"Reference Number: {reference}");
        sb.AppendLine();

        if (root.TryGetProperty("visitor", out var visitor))
        {
            sb.AppendLine("VISITOR DETAILS");
            sb.AppendLine("----------------");
            sb.AppendLine($"Name: {GetValue(visitor, "name")}");
            sb.AppendLine($"Email: {GetValue(visitor, "email")}");
            sb.AppendLine($"Phone: {GetValue(visitor, "phone")}");
            sb.AppendLine();
        }

        if (root.TryGetProperty("accountContext", out var account))
        {
            sb.AppendLine("ACCOUNT INFORMATION");
            sb.AppendLine("-------------------");
            sb.AppendLine(
                $"Verification: {GetValue(account, "verification")}"
            );
            if (GetValue(account, "verification") == "verified")
            {
                sb.AppendLine($"Client ID: {GetValue(account, "clientId")}");
                sb.AppendLine($"Account key: {GetValue(account, "accountKey")}");
            }
            sb.AppendLine();
        }
        sb.AppendLine($"Summary: {GetValue(root, "summary")}");
        if (!string.IsNullOrWhiteSpace(GetValue(root, "question")))
            sb.AppendLine($"Question: {CleanText(GetValue(root, "question"))}");
        sb.AppendLine();

        if (root.TryGetProperty("formValues", out var formValues) &&
            formValues.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("SUBMITTED FORM DETAILS");
            sb.AppendLine("----------------------");

            foreach (var field in formValues.EnumerateObject())
            {
                sb.AppendLine(
                    $"{field.Name}: {CleanText(field.Value.ToString())}"
                );
            }

            sb.AppendLine();
        }

        if (root.TryGetProperty("transcript", out var transcript) &&
            transcript.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("CHATBOT CONVERSATION");
            sb.AppendLine("--------------------");

            foreach (var entry in transcript.EnumerateArray())
            {
                string who = GetValue(entry, "who");
                string body = CleanText(GetValue(entry, "body"));

                sb.AppendLine($"{who.ToUpperInvariant()}: {body}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("-----------------------------------");
        sb.AppendLine("Generated by Medicount Chatbot");

        return sb.ToString();
    }
}



public sealed class Outbox(IStore store, IMailTransport transport, ILogger<Outbox> logger, AppDiagnostics? diagnostics = null)
{
    public Task<bool> Deliver(long id) => diagnostics is null ? DeliverCore(id) : diagnostics.RunAsync("outbox.deliver", () => DeliverCore(id));
    private async Task<bool> DeliverCore(long id)
    {
        var row = await store.Claim(id);
        if (row is null) { diagnostics?.Write("outbox.claim", "not_available"); return false; }
        diagnostics?.Write("outbox.claim", "acquired");
        try
        {
            await transport.Send(row);
        }
        catch (Exception ex)
        {
            diagnostics?.Write("outbox.provider", "failed", exception: ex);
            await store.Failed(id);
            diagnostics?.Write("outbox.retry", "scheduled");
            await Audit(row, "escalation_email_failed");
            if (row.Attempts >= 3)
            {
                diagnostics?.Write("outbox.delivery", "operator_review_required");
                logger.LogWarning("Mail delivery requires operator review.");
            }
            return false;
        }

        // Once the provider accepts a message, an audit failure must not turn
        // success into a retryable send failure or report failure to the visitor.
        diagnostics?.Write("outbox.provider", "accepted");
        await store.Delivered(id);
        diagnostics?.Write("outbox.delivery", "recorded");
        await Audit(row, "escalation_email_accepted");
        return true;
    }
    private async Task Audit(Escalation row, string name)
    {
        try { await store.Event(row.SessionId, name, new { row.Reference, attempt = row.Attempts }); }
        catch (Exception ex) { diagnostics?.Write("outbox.audit", "failed", exception: ex); logger.LogWarning("Mail audit event could not be saved."); }
    }
    public Task Retry() => diagnostics is null ? RetryCore() : diagnostics.RunAsync("outbox.retry_batch", RetryCore);
    private async Task RetryCore()
    {
        foreach (var row in await store.Unsent())
        {
            try { await Deliver(row.Id); }
            catch (Exception ex) { diagnostics?.Write("outbox.retry_item", "failed", exception: ex); logger.LogWarning("Outbox processing requires operator review."); }
        }
    }
}
public sealed class Maintenance(IStore store, Outbox outbox, ChatOptions options, ILogger<Maintenance> logger, AppDiagnostics? diagnostics = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        diagnostics?.Write("maintenance", "started");
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                using var trace = diagnostics?.PushTrace(Guid.NewGuid().ToString("N"));
                try
                {
                    diagnostics?.Write("maintenance.tick", "started");
                    await store.Expire(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - options.IdleMs);
                    await outbox.Retry();
                    diagnostics?.Write("maintenance.tick", "completed");
                }
                catch (Exception ex)
                {
                    diagnostics?.Write("maintenance.tick", "failed", exception: ex);
                    logger.LogError("Maintenance operation failed; check database and mail connectivity.");
                }
            }
        }
        catch (OperationCanceledException)
        when (stoppingToken.IsCancellationRequested)
        { diagnostics?.Write("maintenance", "stopped"); }
    }
}


