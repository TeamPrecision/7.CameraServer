using Microsoft.AspNetCore.Mvc;
using CameraServer.Models;
using CameraServer.Services;

namespace CameraServer.Controllers;

[ApiController]
[Route("api/test")]
public class TestController(CameraService camera) : ControllerBase
{
    /// <summary>
    /// Start a test and BLOCK until result or timeout.
    /// The parent tester calls this and waits — no polling needed.
    /// </summary>
    [HttpPost("start")]
    public async Task<ActionResult<TestResult>> Start(
        [FromBody] TestRequest req,
        CancellationToken ct)
    {
        if (req.TimeoutMs <= 0) req.TimeoutMs = 30_000;
        var result = await camera.RunTestAsync(req, ct);
        int httpStatus = result.Status == TestStatus.Pass   ? 200
                       : result.Status == TestStatus.Fail   ? 200   // 200 with status in body
                       : result.Status == TestStatus.Error  ? 500
                       : 200;
        return StatusCode(httpStatus, result);
    }

    /// <summary>Non-blocking: get current camera + test status.</summary>
    [HttpGet("status")]
    public ActionResult<CameraStatus> Status() => camera.GetStatus();

    /// <summary>Cancel any running test.</summary>
    [HttpDelete("cancel")]
    public IActionResult Cancel()
    {
        camera.CancelTest();
        return Ok(new { message = "cancelled" });
    }

    /// <summary>Quick health ping.</summary>
    [HttpGet("ping")]
    public IActionResult Ping() =>
        Ok(new { ok = true, time = DateTime.Now.ToString("o") });
}
