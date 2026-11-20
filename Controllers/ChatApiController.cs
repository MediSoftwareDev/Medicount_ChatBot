using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Medicount.Chatbot.Configuration;
using Medicount.Chatbot.Domain;
using Medicount.Chatbot.Services;
using System.Threading.Tasks;

namespace Medicount.Chatbot.Controllers;

[ApiController]
[Route("api")]
public sealed class ChatApiController : ControllerBase
{
    private readonly ChatOptions options;
    private readonly IStore store;
    private readonly ConversationEngine engine;
    private readonly SessionMutex mutex;
    private readonly RequestLimits limits;

    public ChatApiController(ChatOptions options, IStore store, ConversationEngine engine,SessionMutex mutex, RequestLimits limits)
    {
        this.options = options;
        this.store = store;
        this.engine = engine;
        this.mutex = mutex;
        this.limits = limits;
    }

    [HttpGet("status")]
    public IActionResult Status() => new JsonResult(new
    {
        billing = options.BillingMode == "live" ? "configured" : "not_configured",
        email = options.Email != "disabled" ? "configured" : "not_configured",
        confirmation = "live_only"
    });

    [HttpPost("session")]
    public async Task<IActionResult> Session()
    {
        var body = await ApiBody.Read(HttpContext, true);
        ApiBody.Shape(body, []);
        limits.Check("create:" + HttpContext.Connection.RemoteIpAddress, Math.Min(20, options.RateLimit));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var s = new Medicount.Chatbot.Domain.Session { TokenHash = ApiBody.TokenHash(token) };
        await store.Create(s);
        var result = await engine.Start(s);
        await store.Save(s);
        result.Token = token;
        result.Mode = "live_confirmation";
        result.SessionTiming = new(options.IdleMs, options.AbsoluteMs);
        return new JsonResult(result, JsonDefaults.Options) { StatusCode = 201 };
    }

    [HttpPost("choose")]
    [HttpPost("message")]
    [HttpPost("form")]
    [HttpPost("prechat")]
    [HttpPost("keepalive")]
    [HttpPost("event")]
    public async Task<IActionResult> ActionRequest()
    {
        var path = HttpContext.Request.Path.Value?.Split('/').LastOrDefault() ?? "";
        var body = await ApiBody.Read(HttpContext);
        ApiBody.Validate(path, body);
        var auth = HttpContext.Request.Headers.Authorization.ToString();
        if (!Regex.IsMatch(auth, @"^Bearer [A-Za-z0-9_-]{43}$"))
            throw new RequestError(401, "SESSION_UNAVAILABLE");
        var hash = ApiBody.TokenHash(auth[7..]);
        return await mutex.Run(hash, async () =>
        {
            var s = await store.Get(hash);
            if (s is null || s.Status != "active")
                throw new RequestError(401, "SESSION_UNAVAILABLE");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - s.LastActivity >= options.IdleMs || now - s.CreatedAt >= options.AbsoluteMs)
            {
                s.Status = "expired";
                s.Actions.Clear();
                s.Form = null;
                s.Verified = null;
                await store.Save(s);
                throw new RequestError(401, "SESSION_EXPIRED");
            }
            limits.Check("session:" + hash, options.RateLimit);
            s.LastActivity = now;
            BotResponse result;
            switch (path)
            {
                case "choose":
                    result = await engine.Choose(s, body.GetProperty("buttonId").GetString()!);
                    break;
                case "message":
                    result = await engine.Message(s, body.GetProperty("text").GetString()!.Trim());
                    break;
                case "form":
                    
                case "prechat":
                    if (path == "prechat" && s.Form?.Purpose != "prechat") 
                        throw new RequestError();
                    result = await engine.Form(
                        s,
                        body.GetProperty("formId").GetString()!,
                        body.GetProperty("values").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!),
                        body.GetProperty("confirmed").GetBoolean(),
                        body.TryGetProperty("reviewToken", out var reviewToken) ? reviewToken.GetString() : null);
                    break;
                case "event":
                    // Keep existing allowlist and data-free event contract intact.
                    await store.Event(s.Id, body.GetProperty("name").GetString()!);
                    result = new();
                    break;
                default:
                    result = new();
                    break;
            }
            await store.Save(s);
            
            foreach (var message in result.Messages)
            {
                var m = JsonSerializer.SerializeToElement(message, JsonDefaults.Options);
                if (m.GetProperty("kind").GetString() == "text")
                    await store.Message(s.Id, new("bot", m.GetProperty("html").GetString()!));
            }
            result.SessionTiming = new(options.IdleMs,
                Math.Max(0, options.AbsoluteMs - (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - s.CreatedAt)));
            return (IActionResult)new JsonResult(result, JsonDefaults.Options);
        });
    }
}
