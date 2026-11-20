using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Medicount.Chatbot.Domain;
using Medicount.Chatbot.Configuration;
using Medicount.Chatbot.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
namespace Medicount.Chatbot.Services;

public sealed class SqlStore(ChatOptions options, AppDiagnostics? diagnostics = null) : IStore
{
    private Task<T> Trace<T>(string operation, Func<Task<T>> action) =>
        diagnostics is null ? action() : diagnostics.RunAsync(operation, action);

    private async Task<T> Use<T>(Func<SqlConnection, Task<T>> action, [CallerMemberName] string operation = "")
    {
        // operation is a compiler-provided member name (or an explicit nameof
        // below), never a SQL statement, event name, identifier or user value.
        var prefix = "sql.store." + operation.ToLowerInvariant();
        await using var connection = await Trace(prefix + ".connection_open", async () =>
        {
            var opened = new SqlConnection(options.SqlConnection ?? throw new InvalidOperationException("SQL connection is not configured."));
            try
            {
                await opened.OpenAsync();
                return opened;
            }
            catch
            {
                await opened.DisposeAsync();
                throw;
            }
        });
        return await Trace(prefix + ".query", () => action(connection));
    }
    private static SqlCommand Command(SqlConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        var command = new SqlCommand(sql, connection) 
        {
            CommandTimeout = 30
        };
        foreach (var (name, value) in args)
        {
            var p = value 
                switch
            { 
                long => new SqlParameter(name, SqlDbType.BigInt),
                int => new SqlParameter(name, SqlDbType.Int), 
                bool => new SqlParameter(name, SqlDbType.Bit), 
                DateTime => new SqlParameter(name, SqlDbType.DateTime2), 
                _ => new SqlParameter(name, SqlDbType.NVarChar, -1) 
            };
            p.Value = value ?? DBNull.Value;
            command.Parameters.Add(p);
        }
        return command;
    }
    private Task<int> Execute(string operation, string sql, params (string Name, object? Value)[] args) => Use(async c =>
    { 
        await using var command = Command(c, sql, args); return await command.ExecuteNonQueryAsync(); 
    }, operation);
    private static DateTime Date(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    public async Task Create(Session s) => _ = await Execute(nameof(Create), "INSERT dbo.sessions(id,token_hash,state,created_at,last_activity,status) VALUES(@id,@hash,@state,@created,@last,@status)",
            ("@id", s.Id),
            ("@hash", s.TokenHash),
            ("@state", JsonSerializer.Serialize(s, JsonDefaults.Options)), 
            ("@created", Date(s.CreatedAt)), 
            ("@last", Date(s.LastActivity)),
            ("@status", s.Status)
            );
    public Task<Session?> Get(string hash) 
        => Use<Session?>(async c => 
        { 
            await using var cmd = Command(c, "SELECT state,status FROM dbo.sessions WHERE token_hash=@hash", ("@hash", hash)); 
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null; 
            var s = JsonSerializer.Deserialize<Session>(r.GetString(0), JsonDefaults.Options)!;
            s.Status = r.GetString(1); 
            return s; 
        });
    public async Task Save(Session s) => 
        _ = await Execute(nameof(Save), "UPDATE dbo.sessions SET state=@state,status=@status,last_activity=@last WHERE id=@id",
            ("@state", JsonSerializer.Serialize(s, JsonDefaults.Options)), 
            ("@status", s.Status),
            ("@last", Date(s.LastActivity)), 
            ("@id", s.Id)
            );
    public async Task Message(string id, Transcript m) => 
        _ = await Execute(nameof(Message), "INSERT dbo.messages(session_id,who,body,kind) VALUES(@id,@who,@body,@kind)",
            ("@id", id), 
            ("@who", m.Who), 
            ("@body", m.Body),
            ("@kind", m.Kind)
            );
    public Task<List<Transcript>> Transcript(string id) => Use(async c => { await using var cmd = Command(c, "SELECT who,body,kind FROM dbo.messages WHERE session_id=@id ORDER BY id", ("@id", id)); await using var r = await cmd.ExecuteReaderAsync(); var rows = new List<Transcript>(); while (await r.ReadAsync()) rows.Add(new(r.GetString(0), r.GetString(1), r.GetString(2))); return rows; });
    public async Task Event(string id, string name, object? data = null) =>
        _ = await Execute(nameof(Event), "INSERT dbo.events(session_id,name,data) VALUES(@id,@name,@data)",
            ("@id", id), ("@name", name),
            ("@data", JsonSerializer.Serialize(data ?? new { }, JsonDefaults.Options)));
    public async Task Incident(string id) => _ = await Execute(nameof(Incident), "INSERT dbo.incidents(session_id,type,detail) VALUES(@id,'sensitive_data','{}')", ("@id", id));
    public async Task Attempt(string id, string hash, string outcome) => _ = await Execute(nameof(Attempt), "INSERT dbo.verification_attempts(session_id,account_hash,outcome) VALUES(@id,@hash,@outcome)", ("@id", id), ("@hash", hash), ("@outcome", outcome));
    private static Escalation Read(SqlDataReader r)
    {
        long Time(string name) => r[name] 
            switch 
        { DateTimeOffset d => d.ToUnixTimeMilliseconds(), 
            DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), _ => 0 };
        return new(Convert.ToInt64(r["id"]), 
            Convert.ToString(r["session_id"])!,
            Convert.ToString(r["reference"])!, 
            Convert.ToString(r["idempotency_key"])!, 
            Convert.ToString(r["kind"])!, 
            Convert.ToString(r["queue_email"])!,
            Convert.ToString(r["payload"])!, 
            Convert.ToBoolean(r["sent"]), 
            Convert.ToInt32(r["send_attempts"]), 
            Time("lease_until"), 
            Time("next_attempt"));
    }
    public Task<Escalation> Enqueue(string sessionId, string key, string kind, string queue, string payload) => Use(async c =>
    {
        await using var transaction = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable);
        await using var cmd = Command(c, "SELECT * FROM dbo.escalations WITH(UPDLOCK,HOLDLOCK) WHERE idempotency_key=@key", ("@key", key)); cmd.Transaction = transaction;
        Escalation? result;
        await using (var reader = await cmd.ExecuteReaderAsync()) result = await reader.ReadAsync() ? Read(reader) : null;
        if (result is null)
        {
            await using var insert = Command(c, "INSERT dbo.escalations(session_id,idempotency_key,kind,queue_email,payload) OUTPUT INSERTED.id VALUES(@sid,@key,@kind,@queue,@payload)", ("@sid", sessionId), ("@key", key), ("@kind", kind), ("@queue", queue), ("@payload", payload)); insert.Transaction = transaction;
            var id = Convert.ToInt64(await insert.ExecuteScalarAsync()); var reference = References.For(id);
            await using var update = Command(c, "UPDATE dbo.escalations SET reference=@ref WHERE id=@id", ("@ref", reference), ("@id", id)); update.Transaction = transaction; await update.ExecuteNonQueryAsync();
            result = new(id, sessionId, reference, key, kind, queue, payload);
        }
        await transaction.CommitAsync(); return result;
    });
    public Task<Escalation?> Claim(long id) => Use<Escalation?>(async c => { await using var cmd = Command(c, "UPDATE dbo.escalations SET lease_until=DATEADD(second,120,SYSUTCDATETIME()),send_attempts=send_attempts+1 OUTPUT INSERTED.* WHERE id=@id AND sent=0 AND (lease_until IS NULL OR lease_until<=SYSUTCDATETIME()) AND (next_attempt IS NULL OR next_attempt<=SYSUTCDATETIME())", ("@id", id)); await using var r = await cmd.ExecuteReaderAsync(); return await r.ReadAsync() ? Read(r) : null; });
    public async Task Delivered(long id) => _ = await Execute(nameof(Delivered), "UPDATE dbo.escalations SET sent=1,sent_at=SYSUTCDATETIME(),lease_until=NULL WHERE id=@id", ("@id", id));
    public async Task Failed(long id) => _ = await Execute(nameof(Failed), "UPDATE dbo.escalations SET lease_until=NULL,next_attempt=DATEADD(second,300,SYSUTCDATETIME()) WHERE id=@id", ("@id", id));
    public Task<List<Escalation>> Unsent() => Use(async c => { await using var cmd = Command(c, "SELECT TOP (25) * FROM dbo.escalations WHERE sent=0 AND (lease_until IS NULL OR lease_until<=SYSUTCDATETIME()) AND (next_attempt IS NULL OR next_attempt<=SYSUTCDATETIME()) ORDER BY id"); await using var r = await cmd.ExecuteReaderAsync(); var rows = new List<Escalation>(); while (await r.ReadAsync()) rows.Add(Read(r)); return rows; });
    public async Task Expire(long before) => _ = await Execute(nameof(Expire), "UPDATE dbo.sessions SET status='expired' WHERE last_activity<@before AND status='active'", ("@before", Date(before)));
}

