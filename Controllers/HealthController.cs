using Microsoft.AspNetCore.Mvc;

namespace Medicount.Chatbot.Controllers;

[ApiController]
public sealed class HealthController : ControllerBase
{
    [HttpGet("/healthz")]
    public IActionResult Health() => new JsonResult(new { status = "ok" });
}
