using Medicount.Chatbot.Configuration;
using Medicount.Chatbot.Domain;
using Medicount.Chatbot.Diagnostics;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static Medicount.Chatbot.Services.Guardrails;

namespace Medicount.Chatbot.Services;

public sealed class ConversationEngine(FlowCatalog catalog, IStore store, IBillingGateway billing, BillingApiService billingApi, PatientVerificationRepository patientVerificationRepository, Outbox outbox, ChatOptions options, ChatFormRepository formRepository, AppDiagnostics? diagnostics = null)
{
    private readonly BillingApiService _billingApi = billingApi;
    private readonly PatientVerificationRepository _patientVerificationRepository = patientVerificationRepository;

    private Task<BotResponse> Trace(string operation, Func<Task<BotResponse>> action) => diagnostics is null ? action() : diagnostics.RunAsync(operation, action);
    public Task<BotResponse> Start(Session s) => Trace("conversation.start", () => StartCore(s));
    public Task<BotResponse> Choose(Session s, string id) => Trace("conversation.choose", () => ChooseCore(s, id));
    public Task<BotResponse> Message(Session s, string value) => Trace("conversation.message", () => MessageCore(s, value));
    public Task<BotResponse> Form(Session s, string id, Dictionary<string, string> raw, bool confirmed, string? reviewToken)
        => Trace("conversation.form", () => FormCore(s, id, raw, confirmed, reviewToken));

    private const string Phone = "888-410-6986", Hours = "Monday–Friday, 8:00 AM–5:00 PM Eastern Time";

    private static Field Field(string id, string label, string? type = null) => new(id, label, true, Type: type, MaxLength: 200);
    private static readonly FormDefinition Prechat = new("Your contact details", [Field("name", "Full name"), Field("email", "Email address", "email"), Field("phone", "Phone number", "tel")], "prechat", "Contact details");
    private FormDefinition ContactForm => options.VisitorEmailEnabled
        ? Prechat with
        {
            Fields = [.. Prechat.Fields, new("emailReceipt",
                "Send me a email confirmation when I submit a request", false,
                Type: "checkbox", MaxLength: 3)]
        }
        : Prechat;

    private async Task QueueVisitorConfirmation(Session session, string requestId)
    {
        if (!options.VisitorEmailEnabled || !session.VisitorEmailOptIn ||
            session.Visitor?.Email is not string recipient || !VisitorConfirmation.ValidRecipient(recipient))
            return;

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                submittedAtUtc = DateTimeOffset.UtcNow
            });
            await store.Enqueue(session.Id, VisitorConfirmation.Key(session.Id, requestId),
                                VisitorConfirmation.Kind, recipient, payload);
        }
        catch (Exception ex)
        {
            diagnostics?.Write("conversation.visitor_confirmation", "queue_failed", exception: ex);
        }
    }

    private static readonly FormDefinition Verification = new("Identity verification", [new("name", "Patient name", true, MaxLength: 150), new("dob", "Patient date of birth (MM/DD/YYYY)", true, "numeric", "dob", MaxLength: 10), new("account", "Please enter the numeric portion of your account number (bottom left of your statement)", true, MaxLength: 50)], "verification", "Identity verification");
    private static readonly HashSet<string> PaymentFlows = ["payments", "flow_01_pay_online", "flow_02_pay_phone", "flow_03_plan_intro", "flow_03_plan_request", "flow_04_methods", "flow_05_balance"];
    private static object Text(string html, string? tone = "normal") => new { kind = "text", html, tone = tone ?? "normal" };
    private static BotResponse Reply(params object[] messages) => new() { Messages = [.. messages] };
    private static void AppendResponse(BotResponse parent, BotResponse child)
    {
        parent.Messages.AddRange(child.Messages);
        parent.DiagnosticTraceId = child.DiagnosticTraceId ?? parent.DiagnosticTraceId;
        parent.Status = child.Status ?? parent.Status;
    }
    private static Choice Choice(string label, string flow) => new(label, new(flow));
    private static void Clear(Session s) { s.Actions.Clear(); s.Form = null; }
    
    private object Buttons(Session s, params Choice[] choices) => new
    {
        kind = "buttons",
        buttons = choices.Select(choice =>
        {
            var id = Guid.NewGuid().ToString();
            s.Actions[id] = choice.Target;
            return new { id, label = choice.Label };
        }).ToArray()
    };

    private static object ShowForm(Session s, FormDefinition definition, string purpose)
    {
        var id = Guid.NewGuid().ToString();
        s.Form = new(id, definition, purpose);
        return new
        {
            kind = "form",
            formId = id,
            title = definition.Title,
            fields = definition.Fields,
            confirm = true,
            note = "Review your entries before submitting. You will be asked to confirm them."
        };
    }
    private async Task<BotResponse> StartCore(Session s)
    {
        await store.Event(s.Id, "session_started");
        return Reply(Text(catalog.Globals["welcome"]),
            Text(catalog.Globals["prechat"]),
            ShowForm(s, ContactForm, "prechat"));
    }
    private async Task<BotResponse> ChooseCore(Session s, string id)
    {
        if (!s.Actions.TryGetValue(id, out var target))
        {
            diagnostics?.Write("conversation.choose", "stale_action");
            throw new RequestError(409, "STALE_ACTION");
        }
        diagnostics?.Write("conversation.choose", "accepted");
        if (target.Flow == "__verification_retry")
        {
            if (s.Form?.Purpose != "verify" || s.Attempts >= 3)
                throw new RequestError(409, "STALE_ACTION");
            s.Actions.Remove(id);
            await store.Event(s.Id, "auth_retry_chosen");
            var r = Reply(Text("No problem — please check your details against your statement and submit the form again."));
            r.RetryForm = true; return r;
        }
        //Clear(s);
        //s.Pending = null;       
        //return await Enter(s, target, 0, id);
        Clear(s);
        s.Pending = null;

        if (target.Flow == "menu")
        {
            s.Professional = false;
        }

        return await Enter(s, target, 0, id);
    }
    private BotResponse Lockout(Session s) { diagnostics?.Write("conversation.verification", "locked_out"); 
        Clear(s); s.Pending = null; 
        return Reply(Text(catalog.Globals["lockout"], "alert"), 
            Buttons(s, Choice("Request a callback", "callback"), 
            Choice("⬅ Main menu", "menu"))); }
    private async Task<BotResponse> Enter(Session s, Target target, int depth = 0, string? requestId = null)
    {
        if (depth > 20) throw new RequestError(500);
        requestId ??= Guid.NewGuid().ToString();
        if (!catalog.Flows.TryGetValue(target.Flow, out var flow)) throw new RequestError();
        if (s.Visitor is null) return Reply(ShowForm(s, ContactForm, "prechat"));
        if (flow.Auth == "professional") { s.Professional = true; s.Verified = null; s.VerifiedPatientName = null; s.Pending = null; }
        if (s.Professional && (flow.Auth == "required" || PaymentFlows.Contains(flow.Id) || new[] { "menu", "balance_menu", "insurance_menu", "other", "fallback" }.Contains(flow.Id))) return await Enter(s, new("professional"), depth + 1);
        var needsPlacement = flow.Auth == "required" || PaymentFlows.Contains(flow.Id) || flow.Id is "menu" or "fallback";
        if (s.Verified is not null && needsPlacement)
        {
            try
            {
                // SQL verification supplies placement for SQL-only deployments.
                // Refresh it only when an external billing gateway is configured.
                if (billing.Mode != "disabled" && flow.Id != "flow_05_balance") s.Verified = s.Verified
                    with
                {
                    Placed = await billing.Placement(s.Verified)
                };
            }
            catch (Exception ex)
            {
                diagnostics?.Write("conversation.placement", "failed", exception: ex);
                return Outage(s);
            }
            if (s.Verified.Placed && (PaymentFlows.Contains(flow.Id)
                || flow.Id is "menu" or "fallback"))
                return await Enter(s, new("flow_23_collections"), depth + 1);
        }
        if (flow.Auth == "required" && s.Verified is null)
        {
            if (s.Attempts >= 3)
                return Lockout(s); 
            s.Pending = target;
            await store.Event(s.Id, "auth_gate_shown");
            return 
                Reply
                (Text(catalog.Globals["verification"]), 
                ShowForm(s, Verification, "verify"), 
                Buttons(s, Choice("⬅ Main menu", "menu")));
        }
        if (!flow.States.TryGetValue(target.State ?? flow.Entry, out var state)) throw new RequestError();
        if (state.Event is not null) await store.Event(s.Id, state.Event);
        var result = new BotResponse();
        if (state.Effect is not null)
        {
            try
            {
                if (state.Effect == "balance")
                {
                    if (s.Verified is null || s.Professional || s.Verified.Placed)
                        throw new RequestError(); 
                    var balance = await _patientVerificationRepository.GetAccountBalanceAsync(s.Verified);
                    if (balance is null) return Outage(s);
                    var amount = balance.Value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
                    result.Messages.Add(Text($"Your current balance due is <b>{Html(amount)}</b>."));
                }
                else if (state.Effect == "claim")
                { 
                    if (s.Verified is null || s.Professional) 
                        throw new RequestError();
                    var c = await billing.Claim(s.Verified); 
                    result.Messages.Add(Text($"A claim was sent to <b>{Html(c.Payer)}</b> on {Html(c.SentDate)} and processed on {Html(c.ProcessedDate)}. {Html(c.Outcome)}.")); }
               
                else if (state.Effect.StartsWith("statement_", StringComparison.Ordinal))
                {
                    if (s.Verified is null || s.Professional)
                        throw new RequestError(401, "VERIFICATION_REQUIRED");

                    var kind = state.Effect[10..];

                    if (kind == "itemized")
                    {
                        var statementPayload = JsonSerializer.Serialize(new
                        {
                            visitor = s.Visitor,
                            accountContext = new
                            {
                                clientId = s.Verified.ClientId,
                                accountKey = s.Verified.AccountKey,
                                patientName = s.VerifiedPatientName,
                                verification = "verified"
                            },
                            summary = "Itemized statement requested by postal mail",
                            formValues = new Dictionary<string, string>
                            {
                                ["Request"] = "Please mail an itemized statement.",
                                ["Delivery address"] = "Use the mailing address on file."
                            },
                            submittedAtUtc = DateTimeOffset.UtcNow
                        }, JsonDefaults.Options);

                        var statementRequest = await store.Enqueue(s.Id,requestId,"itemized_statement",options.Queue,statementPayload);

                        await QueueVisitorConfirmation(s, requestId);
                        s.LastReference = statementRequest.Reference;

                        bool delivered = statementRequest.Sent;

                        try
                        {
                            if (!delivered)
                                delivered = await outbox.Deliver(statementRequest.Id);
                        }
                        catch (Exception ex)
                        {
                            diagnostics?.Write(
                                "conversation.statement_email",
                                "delivery_unconfirmed",
                                exception: ex);
                        }

                        result.Messages.Add(Text(
                            delivered
                                ? "Your request has been sent to our Patient Relations team. " +
                                  "They will arrange to mail your itemized statement " +
                                  "to the address on file. " +
                                  $"Reference: <b>{Html(statementRequest.Reference)}</b>."
                                : "Your request was saved, but email delivery could not " +
                                  $"be confirmed. Please call <b>{Phone}</b> and provide " +
                                  $"reference <b>{Html(statementRequest.Reference)}</b>. " +
                                  "You do not need to submit the request again.",
                            delivered ? "good" : "alert"));
                    }
                    else
                    {
                        // Keep the existing email and postal-mail integrations.
                        if (!await billing.Statement(s.Verified, kind, requestId))
                            return Outage(s);

                        if (billing.Mode != "live")
                        {
                            result.Messages.Add(Text(
                                "Test fixture accepted this request. No statement was sent."));
                        }
                        else
                        {
                            result.Messages.Add(Text(kind switch
                            {
                                "email" =>
                                    "Done — your statement copy request was accepted " +
                                    "for the email on file. It usually arrives within " +
                                    "<b>1 business day</b>.",

                                "mail" =>
                                    "Done — your printed statement request was accepted " +
                                    "for the address on file. Please allow " +
                                    "<b>7–10 business days</b>.",

                                _ => "Your statement request was accepted."
                            }, "good"));
                        }
                    }
                }
                else if (state.Effect == "agency")
                { if (s.Verified is null || s.Professional || !s.Verified.Placed) throw new RequestError(); var a = await billing.Agency(s.Verified); result.Messages.Add(Text($"Your account is with <b>{Html(a.Name)}</b> · {Html(a.Phone)} · {Html(a.Hours)}.")); }
                else if (state.Effect == "close")
                { 
                    s.Status = "closed";
                    Clear(s); s.Verified = null;
                    s.VerifiedPatientName = null;
                    s.Pending = null;
                    result.Messages.Add(Text($"Thank you for chatting with the Medicount Billing Assistant.{(s.LastReference is not null ? $" Keep reference <b>{Html(s.LastReference)}</b> for your records." : "")}\n\nYou can start a new chat whenever you need billing help. Take care! 👋")); }
            }
            catch (Exception ex) { diagnostics?.Write("conversation.effect", "failed", exception: ex); return Outage(s); }
        }
        foreach (var say in state.Say ?? []) result.Messages.Add(Text(say, state.Tone));
        if (state.LinkCard is not null) result.Messages.Add(new { kind = "link_card", state.LinkCard.Title, state.LinkCard.Url, state.LinkCard.Note });
        if (state.Form is not null)
        {
            result.Messages.Add(ShowForm(s, state.Form, state.Form.Kind == "insurance_update" ? "insurance_update" : "escalation"));
            if (state.Buttons is null || state.Buttons.Length == 0)
                result.Messages.Add(Buttons(s, Choice("⬅ Main menu", "menu")));
        }
        if (state.Buttons is not null) result.Messages.Add(Buttons(s, state.Buttons));
        if (state.Endpoint is not null)
            await store.Event(s.Id, "endpoint", new { endpoint = state.Endpoint });
        if (state.Next is not null)
            AppendResponse(result, await Enter(s, new(flow.Id, state.Next), depth + 1));
        if (state.Closure)
            AppendResponse(result, await Enter(s, new("closure"), depth + 1));
        result.Status = s.Status;
        return result;
    }
    
    private BotResponse Outage(Session s)
    {
        diagnostics?.Write("conversation.service", "unavailable");
        Clear(s);

        var response = Reply(
            Buttons(
                s,
                Choice("Request a callback", "callback"),
                Choice("⬅ Main menu", "menu"),
                Choice("End chat", "goodbye")
            )
        );

        response.DiagnosticTraceId = diagnostics?.TraceId;
        return response;
    }
    private async Task<BotResponse> SensitiveResponse(Session s)
    {
        diagnostics?.Write("conversation.guardrail", "sensitive_input_blocked");
        await store.Message(s.Id, new("visitor", Redacted));
        await store.Incident(s.Id);
        await store.Event(s.Id, "sensitive_data_detected");
        var choices = new List<Choice>
        { Choice("⬅ Main menu", "menu") };
        if (!s.Professional && s.Verified?.Placed != true)
            choices.Insert(0, Choice("Open secure payment page", "flow_01_pay_online"));
        var r = Reply(Text(catalog.Globals["card"], "alert"),
            Buttons(s, choices.ToArray()));
        r.RetryForm = s.Form is not null;
        return r;
    }
    private async Task<BotResponse> MessageCore(Session s, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new RequestError(422, "CHECK_FIELDS");
        var sensitive = Sensitive(value);
        if (Emergency(value))
        {
            diagnostics?.Write("conversation.guardrail", "emergency_exit");
            await store.Message(s.Id, new("visitor", sensitive ? Redacted : "[EMERGENCY MESSAGE]")); if (sensitive) { await store.Incident(s.Id); await store.Event(s.Id, "sensitive_data_detected"); }
            Clear(s); s.Status = "closed"; s.Question = null; s.Verified = null; await store.Event(s.Id, "emergency_exit_triggered"); await store.Event(s.Id, "endpoint", new { endpoint = "EMERGENCY_EXIT" });
            var r = Reply(Text(catalog.Globals["emergency"], "alert")); r.Status = "closed"; return r;
        }
        if (sensitive) return await SensitiveResponse(s); Clear(s); s.Pending = null;
        if (LiveAgent(value)) { await store.Event(s.Id, "live_agent_requested"); return Reply(Text(catalog.Globals["agent"]), Buttons(s, Choice("Show me the menu", "menu"), Choice("Request a callback", "callback"))); }
        if (s.Visitor is null) return Reply(ShowForm(s, ContactForm, "prechat"));
        await store.Message(s.Id, new("visitor", value)); var flow = Route(value); if (flow == "fallback") { s.Question = value; await store.Event(s.Id, "low_confidence_intent"); }
        var response = await Enter(s, new(flow)); response.VisitorEcho = value; return response;
    }
    private static Dictionary<string, string> Validate(Session s, string id, Dictionary<string, string> raw)
    {
        if (s.Form is null || s.Form.Id != id)
            throw new RequestError(409, "STALE_FORM");
        var fields = s.Form.Definition.Fields;
        if (raw.Keys.Any(k => !fields.Any(f => f.Id == k)))
            throw new RequestError();
        var values = new Dictionary<string, string>();
        foreach (var f in fields)
        {
            var v = raw.GetValueOrDefault(f.Id, "").Trim();
            if ((f.Required && v.Length == 0) || v.Length > (f.MaxLength ?? 1000) || (v.Length > 0 && f.Type == "email" && !Regex.IsMatch(v, @"^[^\s@]+@[^\s@]+\.[^\s@]+$"))) throw new RequestError(422, "CHECK_FIELDS");
            if (v.Length > 0 && f.Type == "tel" && (!Regex.IsMatch(v, @"^[+0-9\s().-]+$") || Regex.Replace(v, "[^0-9]", "").Length is < 7 or > 15)) throw new RequestError(422, "CHECK_FIELDS");
            if (f.Type == "checkbox" && v is not ("" or "yes"))
                throw new RequestError(422, "CHECK_FIELDS");
            values[f.Id] = v;
        }
        if (s.Form.Purpose == "prechat" && values.GetValueOrDefault("emailReceipt") == "yes" &&
            !VisitorConfirmation.ValidRecipient(values["email"]))
            throw new RequestError(422, "CHECK_FIELDS");
        if (s.Form.Purpose == "verify"
            && (!DateOnly.TryParseExact(values["dob"], "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ||
            date.Year < 1850 ||
            date > DateOnly.FromDateTime(DateTime.UtcNow)))
            throw new RequestError(422, "CHECK_FIELDS");
        if (s.Form.Definition.Kind == "contact_update")
        {
            var change = values["field0"].ToLowerInvariant();
            if (change is not ("address" or "phone" or "both")
                || ((change is "address" or "both") && values["field1"].Length == 0)
                || ((change is "phone" or "both") && values["field2"].Length == 0))
                throw new RequestError(422, "CHECK_FIELDS");
            values["field0"] = change;
        }
        if (s.Form.Definition.Kind == "payment_plan"
            && (!decimal.TryParse(values["field0"], NumberStyles.Currency, CultureInfo.GetCultureInfo("en-US"), out var amount)
                || amount <= 0 || decimal.Round(amount, 2) != amount))
            throw new RequestError(422, "CHECK_FIELDS");
        return values;
    }
    private string Hash(string text) =>
    Convert.ToHexString(
        HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.HashSecret),
            Encoding.UTF8.GetBytes(text)
        )
    ).ToLowerInvariant();
    private async Task<BotResponse> FormCore(Session s, string id, Dictionary<string, string> raw, bool confirmed, string? reviewToken)
    {
        diagnostics?.Write("conversation.form", confirmed ? "confirmation_received" : "review_received");
        if (raw.Values.Any(Sensitive))
            return await SensitiveResponse(s);
        var values = Validate(s, id, raw);
        var signature = Hash(JsonSerializer.Serialize(new object[] { s.Id, id, values }, JsonDefaults.Options));
        if (!confirmed)
        {
            diagnostics?.Write("conversation.form", "review_validated");
            return new()
            {
                ReviewToken = signature
            };
        }
        if (reviewToken is null || !Regex.IsMatch(reviewToken, "^[a-f0-9]{64}$") || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature), Convert.FromHexString(reviewToken)))
            throw new RequestError(409, "REVIEW_REQUIRED");
        var current = s.Form!;
        if (current.Purpose == "prechat")
        {
            if (options.Store == "sql") 
                await formRepository.SaveAsync(s.Id, id, values, signature);
            s.Visitor = new(
                values["name"],
                values["email"],
                values["phone"]);
            s.VisitorEmailOptIn = options.VisitorEmailEnabled && values.GetValueOrDefault("emailReceipt") == "yes";
            Clear(s);
            await store.Event(s.Id, "prechat_captured");
            var r = Reply(
                Text(
                    $"Thanks, {Html(Regex.Split(values["name"], @"\s+")[0])}! How can I help you today? Choose an option or type your question."));
            AppendResponse(r, await Enter(s, new("menu")));
            return r;
        }

        if (current.Purpose == "verify")
        {
            if (s.Professional)
                throw new RequestError();

            if (s.Attempts >= 3)
                return Lockout(s);

            // Hash the account number for audit correlation.
            var hash = Hash(values["account"].ToUpperInvariant());

            Verified? identity;

            try
            {
                identity = s.Pending?.Flow == "flow_05_balance"
                    ? await _patientVerificationRepository.VerifyBalancePatientAsync(values["name"], values["dob"], values["account"])
                    : billing.Mode == "fixture"
                    ? await billing.Verify(values["name"], values["dob"], values["account"])
                    : await _billingApi.VerifyAsync(values["name"], values["dob"], values["account"]);
            }
            catch (Exception ex) when (
                ex is HttpRequestException ||
                ex is TaskCanceledException ||
                ex is InvalidOperationException || ex is SqlException || ex is TimeoutException)
            {
                diagnostics?.Write("conversation.verification", "service_error", exception: ex);
                await store.Event(s.Id, "auth_service_error", new
                {
                    verificationStatus = "service_error",
                    sqlErrorNumber = ex is SqlException sql ? (int?)sql.Number : null,
                    attempt = s.Attempts,
                    verifiedAtUtc = DateTimeOffset.UtcNow
                }
                );

                return Outage(s);
            }

            s.Attempts++;
            diagnostics?.Write("conversation.verification", identity is null ? "mismatch" : "verified");

            string verificationStatus = identity is null ? "failed" : "success";

            // Save verification attempt through existing store.
            await store.Attempt(s.Id, hash, identity is null ? "fail" : "pass");

            await store.Message(s.Id, new("system", "Verification submitted"));

            // Save verification result into dbo.events.
            await store.Event(
                s.Id,
                identity is null ? "auth_failed" : "auth_success",
                new
                {
                    verificationStatus = verificationStatus,
                    attempt = s.Attempts,
                    verified = identity is not null,
                    verifiedAtUtc = DateTimeOffset.UtcNow
                }
            );

            if (identity is null)
            {
                if (s.Attempts >= 3)
                    return Lockout(s);

                s.Actions.Clear();

                var r = Reply(
                    Text(
                        $"Those details didn't match our records. " +
                        $"For your security, I can't say which field was incorrect. " +
                        $"(Attempt {s.Attempts} of 3)",
                        "alert"
                    ),
                    Buttons(
                        s,
                        Choice("↻ Try again", "__verification_retry"),
                        Choice("⬅ Main menu", "menu")
                    )
                );

                r.RetryForm = true;

                return r;
            }

            s.Verified = identity;
            s.VerifiedPatientName = values["name"];

            // Continue the action that required verification; insurance is its own flow.
            var target = s.Pending ?? new Target("menu");
            s.Pending = null;
            Clear(s);
            var verifiedResponse = Reply(Text("Thanks — your identity has been verified. ✅", "good"));
            AppendResponse(verifiedResponse, await Enter(s, target, 0, current.Id));
            return verifiedResponse;
        }


        if (current.Purpose == "insurance_update")
        {
            if (s.Verified is null || s.Professional)
                throw new RequestError(401, "VERIFICATION_REQUIRED");

            // Persist first: a notification failure must not undo an accepted request.
            await _patientVerificationRepository.SaveInsuranceUpdateRequestAsync(
                s.Verified.ClientId,
                s.Verified.AccountKey,
                values["insuranceCompany"],
                values["memberId"],
                values.GetValueOrDefault("groupNumber"),
                requestId: current.Id);

            await QueueVisitorConfirmation(s, current.Id);

            // The existing outbox restricts delivery to the approved internal mailbox.
            // Do not include DOB, chat transcript or other unnecessary health details.
            var insuranceEmailPayload = JsonSerializer.Serialize(new
            {
                patientName = s.VerifiedPatientName ?? s.Visitor?.Name ?? "Not available",
                clientId = s.Verified.ClientId,
                accountKey = s.Verified.AccountKey,
                contactEmail = s.Visitor?.Email,
                contactPhone = s.Visitor?.Phone,
                insuranceCompany = values["insuranceCompany"],
                memberId = values["memberId"],
                groupNumber = values.GetValueOrDefault("groupNumber"),
                submittedAtUtc = DateTimeOffset.UtcNow
            }, JsonDefaults.Options);

            Escalation? mail = null;
            bool notificationAccepted = false;
            try
            {
                await store.Event(s.Id, "insurance_update_requested");
                // The form instance ID is the outbox idempotency key.
                mail = await store.Enqueue(s.Id, current.Id, "insurance_update", options.Queue, insuranceEmailPayload);
                notificationAccepted = mail.Sent || await outbox.Deliver(mail.Id);
            }
            catch (Exception ex)
            {
                diagnostics?.Write("conversation.insurance_notification", "failed", exception: ex);
                // Never return an error that encourages resubmitting an already-saved request.
                try { await store.Event(s.Id, "insurance_update_notification_queue_error"); }
                catch (Exception auditError) { diagnostics?.Write("conversation.insurance_audit", "failed", exception: auditError); }
            }

            s.Pending = null;
            if (mail is not null) s.LastReference = mail.Reference;
            Clear(s);
            diagnostics?.Write("conversation.insurance_submission", notificationAccepted ? "saved_and_notified" : "saved_notification_unconfirmed");
            var insuranceResponse = Reply(
                Text(notificationAccepted
                    ? $"Your insurance update request was submitted for review. ✅ Reference: <b>{Html(mail!.Reference)}</b>"
                    : $"Your insurance update request was saved successfully. The notification to our team could not be confirmed. Please call <b>{Phone}</b>, {Hours}, to confirm follow-up. You do not need to submit the form again.",
                    notificationAccepted ? "good" : "alert"),
                Buttons(s,
                    Choice("⬅ Main menu", "menu"),
                    Choice("End chat", "goodbye"))
            );
            if (!notificationAccepted) insuranceResponse.DiagnosticTraceId = diagnostics?.TraceId;
            return insuranceResponse;
        }

        var definition = current.Definition;

        var transcript = await store.Transcript(s.Id);

        object context = s.Verified is not null && !s.Professional
            ? new
            {
                verification = "verified",
                s.Verified.ClientId,
                s.Verified.AccountKey
            }
            : new
            {
                verification = "not_verified"
            };
        var payload = JsonSerializer.Serialize
            (new
            {
                visitor = s.Visitor,
                accountContext = context,
                summary = definition.Summary,
                formValues = definition.Fields.ToDictionary
                (
                    f => f.Label,
                    f => values[f.Id]
                 ),
                question = definition.Kind == "general_question"
                ? s.Question : null,
                transcript
            }, JsonDefaults.Options
           );
        var row = await store.Enqueue(
            s.Id,
            current.Id,
            definition.Kind,
            definition.Sla == "dispute"
            ? options.DisputeQueue
            : options.Queue,
            payload
            );
        if (options.Store == "sql") await formRepository.SaveAsync(
            s.Id,
            current.Id,
            values,
            signature
        );
        await QueueVisitorConfirmation(s, current.Id);
        Clear(s);
        s.Question = null;
        s.LastReference = row.Reference;
        bool delivered = row.Sent;
        try { if (!delivered) delivered = await outbox.Deliver(row.Id); }
        catch (Exception ex) { diagnostics?.Write("conversation.delivery", "failed", exception: ex); }
        if (!delivered)
        {
            diagnostics?.Write("conversation.submission", "saved_delivery_unconfirmed");
            var unconfirmed = Reply(Text($"Your request was saved, but I couldn't confirm delivery to our billing team. Reference: <b>{Html(row.Reference)}</b>. Please call <b>{Phone}</b>, {Hours}, to confirm follow-up. You do not need to submit the form again.", "alert"), Buttons(s, Choice("⬅ Main menu", "menu"), Choice("End chat", "goodbye")));
            unconfirmed.DiagnosticTraceId = diagnostics?.TraceId;
            return unconfirmed;
        }
        diagnostics?.Write("conversation.submission", "saved_and_delivered");
        try
        {
            await store.Event(s.Id, "endpoint", new
            {
                endpoint = definition.Kind == "callback" ? "ASYNC_REVIEW_CALLBACK" : "ASYNC_REVIEW_EMAIL",
                row.Reference
            });
        }
        catch (Exception ex) { diagnostics?.Write("conversation.delivery_audit", "failed", exception: ex); }
        var sla = definition.Sla == "dispute" ? "5 business days" : "1 business day";
        var followUp = definition.Kind == "callback"
            ? $"Our team will call you within <b>{sla}</b>."
            : $"Our team will respond in writing within <b>{sla}</b>.";
        var response = Reply(Text($"Your <b>{Html(definition.Summary.ToLowerInvariant())}</b> has been submitted for review. ✅\n\nReference: <b>{Html(row.Reference)}</b>\n{followUp}", "good"), new { kind = "closure", row.Reference }); AppendResponse(response, await Enter(s, new("closure"))); return response;

    }


}
