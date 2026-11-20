using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Medicount.Chatbot.Domain;
namespace Medicount.Chatbot.Configuration;

public sealed class FlowCatalog
{
    public Dictionary<string, Flow> Flows { get; } = [];
    public Dictionary<string, string> Globals { get; }
    public FlowCatalog(ChatOptions options)
    {
        var dir = Path.Combine(options.DataRoot, "flows");
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Where(f => Path.GetFileName(f) is not ("schema.json" or "globals.json")))
        {
            var flow = JsonSerializer.Deserialize<Flow>(File.ReadAllText(file), JsonDefaults.Options) ?? throw new InvalidOperationException("Invalid flow.");
            if (flow.Auth is not ("none" or "required" or "professional") || !flow.States.ContainsKey(flow.Entry) || !Flows.TryAdd(flow.Id, flow)) throw new InvalidOperationException("Invalid flow entry or duplicate.");
        }
        foreach (var flow in Flows.Values) 
            foreach (var state in flow.States.Values)
        {
            if (state.Next is not null && !flow.States.ContainsKey(state.Next))
                    throw new InvalidOperationException("Invalid state transition.");
            foreach (var button in state.Buttons ?? []) 
                    if (!Flows.TryGetValue(button.Target.Flow, out var target) 
                        || !target.States.ContainsKey(button.Target.State ?? target.Entry)) 
                        throw new InvalidOperationException("Invalid button target.");
            if (state.Form is not null &&
                    (state.Form.Fields.Length == 0 
                    || state.Form.Fields.Any(f => f.MaxLength is <= 0 or > 2000
                    || string.IsNullOrWhiteSpace(f.Id) || string.IsNullOrWhiteSpace(f.Label))
                    || state.Form.Fields.Select(f => f.Id).Distinct().Count() != state.Form.Fields.Length)) 
                    throw new InvalidOperationException("Form fields must have valid lengths, labels, and unique names.");
            if (state.LinkCard is not null && state.LinkCard.Url is not ("https://medicount.patientwallet.com/#/" or "https://www.chartswap.com")) 
                    throw new InvalidOperationException("Unapproved hosted link.");
            if (state.Effect is not null && state.Effect is not ("balance" or "claim" or "statement_email" or "statement_mail" or "statement_itemized" or "agency" or "close")) 
                    throw new InvalidOperationException("Unknown flow effect.");
        }
        foreach (var flow in Flows.Values) foreach (var start in flow.States.Keys)
        {
            var seen = new HashSet<string>(); string? name = start;
            while (name is not null) 
            { if (!seen.Add(name)) 
                    throw new InvalidOperationException("Flow transition cycle.");
                name = flow.States[name].Next;
            }
        }
        Globals = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "globals.json")), JsonDefaults.Options)!;
        string[] keys = ["welcome", "prechat", "emergency", "card", "agent", "verification", "lockout", "fallback", "closure", "insuranceUpdate"];
        if (Globals.Count != keys.Length || keys.Any(k => !Globals.TryGetValue(k, out var value) || value is null)) 
            throw new InvalidOperationException("Invalid global scripts.");
    }
}
