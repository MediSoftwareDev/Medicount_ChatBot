using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
namespace Medicount.Chatbot.Services;

public static class Guardrails
{
    public const string Redacted = "[REDACTED — POSSIBLE CARD NUMBER]";
    public static bool Emergency(string text) => Regex.IsMatch(text, @"chest pain|not breathing|can['’]?t breathe|overdose|heart attack|bleeding|unconscious|stroke|suicid|emergency|\b911\b", RegexOptions.IgnoreCase);
    public static bool LiveAgent(string text) => Regex.IsMatch(text, @"\b(agent|human|representative|real person|operator|speak to someone|live chat)\b", RegexOptions.IgnoreCase);
    public static string Html(string text) => WebUtility.HtmlEncode(text);
    public static bool Sensitive(string text)
    {
        var value = Regex.Replace(text.Normalize(NormalizationForm.FormKC), "[\u200B-\u200D\uFEFF]", "");
        foreach (Match match in Regex.Matches(value, @"[0-9](?:[\s-]*[0-9]){12,}"))
        {
            var digits = Regex.Replace(match.Value, "[^0-9]", ""); 
            if (digits.Length > 19) 
                return true;
            if (digits.Distinct().Count() == 1) 
                continue;
            var sum = 0; var doubleIt = false;
            for (var i = digits.Length - 1; i >= 0; i--)
            { 
                var d = digits[i] - '0'; 
                if (doubleIt) 
                { 
                    d *= 2; if (d > 9) d -= 9; 
                } 
                sum += d; doubleIt = !doubleIt;
            }
            if (sum % 10 == 0) 
                return true;
        }
        return Regex.IsMatch(value, @"\b(routing|iban|bank\s*(account|number)|ach\s*(account|number))\b[\s\S]{0,40}[0-9][0-9\s-]{5,}", RegexOptions.IgnoreCase);
    }
    public static string Route(string value)
    {
        (string Pattern, string Flow)[] routes =
        [
            (@"\b(attorney|lawyer|records|chartswap|settlement|lien|payer|w-?9)\b", "professional"),
            (@"\b(dispute|wrong|error|collections?|collecting)\b", "disputes"),
            (@"\b(insurance|insurer|coverage|claims?|medicare|medicaid)\b", "insurance_menu"),
            (@"\b(address|phone number|itemized|fax|moved|paperless)\b", "other"),
            (@"\b(pay|paying|payments?|bill pay|overpayment)\b", "payments"),
            (@"\b(balance|owe|how much|status)\b", "balance_menu")
        ];
        return routes.FirstOrDefault(r => Regex.IsMatch(value, r.Pattern, RegexOptions.IgnoreCase)).Flow ?? "fallback";
    }
}
