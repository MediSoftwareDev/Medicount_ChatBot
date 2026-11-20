using Medicount.Chatbot.Domain;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
namespace Medicount.Chatbot.Services;

public interface IStore
{
    Task Create(Session s);
    Task<Session?> Get(string hash); 
    Task Save(Session s);
    Task Message(string id, Transcript message);
    Task<List<Transcript>> Transcript(string id);
    Task Event(string id, string name, object? data = null);
    Task Incident(string id); Task Attempt(string id, string hash, string outcome);
    Task<Escalation> Enqueue(string sessionId, string key, string kind, string queue, string payload);
    Task<Escalation?> Claim(long id); 
    Task Delivered(long id); 
    Task Failed(long id); 
    Task<List<Escalation>> Unsent();
    Task Expire(long before);
}
public static class References 
{
    public static string For(long id) => $"MC-{DateTime.UtcNow.Year}-{id:D6}"; }
public sealed class MemoryStore : IStore
{
    private readonly object gate = new();
    public Dictionary<string, Session> Sessions { get; } = [];
    public Dictionary<string, List<Transcript>> Messages { get; } = [];
    public List<(string Id, string Name, object? Data)> Events { get; } = [];
    public List<(string Id, string Hash, string Outcome)> Attempts { get; } = [];
    public List<string> Incidents { get; } = [];
    public List<Escalation> Escalations { get; } = [];
    public Task Create(Session s)
    { 
        lock (gate) Sessions[s.TokenHash] = JsonDefaults.Clone(s); 
        return Task.CompletedTask; 
    }
    public Task Save(Session s) => Create(s);
    public Task<Session?> Get(string hash) 
    { 
        lock (gate)
            return Task.FromResult(Sessions.TryGetValue(hash, out var s) ? JsonDefaults.Clone(s) : null);
    }
    public Task Message(string id, Transcript message)
    { 
        lock (gate) 
        { 
            if (!Messages.TryGetValue(id, out var list)) Messages[id] = list = []; 
            list.Add(message); 
        } 
        return Task.CompletedTask;
    }
    public Task<List<Transcript>> Transcript(string id) { lock (gate) return Task.FromResult(Messages.TryGetValue(id, out var list) ? list.ToList() : []); }
    public Task Event(string id, string name, object? data = null)
    {
        lock (gate)
            Events.Add((id, name, data));
        return Task.CompletedTask;
    }
    
    public Task Incident(string id) { lock (gate) Incidents.Add(id); return Task.CompletedTask; }
    public Task Attempt(string id, string hash, string outcome) 
    { 
        lock (gate) Attempts.Add((id, hash, outcome));
        return Task.CompletedTask;
    }
    public Task<Escalation> Enqueue(string sessionId, string key, string kind, string queue, string payload)
    { 
        lock (gate)
        { var old = Escalations.Find(e => e.Key == key);
            if (old is not null) return Task.FromResult(old); 
            var id = Escalations.Count + 1;
            var row = new Escalation(id, sessionId, References.For(id), key, kind, queue, payload); 
            Escalations.Add(row); return Task.FromResult(row); 
        } 
    }
    public Task<Escalation?> Claim(long id)
    {
        lock (gate) 
        { 
            var index = Escalations.FindIndex(e => e.Id == id);
            if (index < 0) return Task.FromResult<Escalation?>(null); 
            var row = Escalations[index]; 
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); 
            if (row.Sent || row.LeaseUntil > now || row.NextAttempt > now) return Task.FromResult<Escalation?>(null);
            return Task.FromResult<Escalation?>(Escalations[index] = row with 
            { LeaseUntil = now + 120_000, Attempts = row.Attempts + 1 
            }); 
        } 
    }
    public Task Delivered(long id) 
    { 
        lock (gate) 
        { 
            var index = Escalations.FindIndex(e => e.Id == id); Escalations[index] = Escalations[index] with { Sent = true, LeaseUntil = 0 }; 
        }
        return Task.CompletedTask;
    }
    public Task Failed(long id) { lock (gate) { var index = Escalations.FindIndex(e => e.Id == id); Escalations[index] = Escalations[index] with { LeaseUntil = 0, NextAttempt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 300_000 }; } return Task.CompletedTask; }
    public Task<List<Escalation>> Unsent() { lock (gate) { var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); return Task.FromResult(Escalations.Where(e => !e.Sent && e.LeaseUntil <= now && e.NextAttempt <= now).Take(25).ToList()); } }
    public Task Expire(long before) { lock (gate) foreach (var pair in Sessions.Where(p => p.Value.LastActivity < before).ToArray()) { Sessions.Remove(pair.Key); Messages.Remove(pair.Value.Id); } return Task.CompletedTask; }
}
// Fixed stripes bound memory use while serializing requests across a single service process.
public sealed class SessionMutex
{
    private readonly SemaphoreSlim[] locks = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1)).ToArray();
    public async Task<T> Run<T>(string key, Func<Task<T>> action)
    { 
        var gate = locks[(uint)StringComparer.Ordinal.GetHashCode(key) % locks.Length]; 
        await gate.WaitAsync();
        try 
        { 
            return await action();
        } 
        finally 
        { 
            gate.Release(); 
        } 
    }
}
