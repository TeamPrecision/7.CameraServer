using Microsoft.AspNetCore.Mvc;
using CameraServer.Models;
using CameraServer.Services;

namespace CameraServer.Controllers;

[ApiController]
[Route("api/logs")]
public class LogController(LogService log) : ControllerBase
{
    /// <summary>Get recent log entries (in-memory ring buffer).</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<LogEntry>> GetRecent(
        [FromQuery] int    count    = 100,
        [FromQuery] string? level   = null,
        [FromQuery] string? category = null)
    {
        return Ok(log.GetRecent(count, level, category));
    }

    /// <summary>Download the current month's JSONL log file.</summary>
    [HttpGet("export")]
    public IActionResult Export()
    {
        var path = log.GetCurrentLogFilePath();
        if (path == null) return NotFound(new { message = "No log file for this month" });

        var bytes = System.IO.File.ReadAllBytes(path);
        string name = System.IO.Path.GetFileName(path);
        return File(bytes, "application/octet-stream", name);
    }
}
