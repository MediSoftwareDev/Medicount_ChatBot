using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using System.IO;

namespace Medicount.Chatbot.Controllers;

/// <summary>
/// Backward-compatible static web entry points for the WordPress widget and preview.
/// This controller does not render Razor views; the application uses API controllers and static assets.
/// </summary>
[ApiController]
public sealed class PagesController : ControllerBase
{
    private readonly IWebHostEnvironment environment;
    public PagesController(IWebHostEnvironment environment) => this.environment = environment;

    [HttpGet("/")]
    [HttpGet("preview")]
    [HttpGet("preview.html")]
    public IActionResult Preview()
    {
        if (Request.PathBase.HasValue && !Request.Path.HasValue)
            return Redirect(Request.PathBase.Value + "/");
        return Html("preview.html");
    }

    [HttpGet("widget")]
    [HttpGet("widget.html")]
    public IActionResult Widget() => Html("widget.html");

    private IActionResult Html(string fileName)
    {
        var path = Path.Combine(environment.WebRootPath, fileName);
        return PhysicalFile(path, "text/html; charset=utf-8");
    }
}
