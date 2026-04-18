using Microsoft.AspNetCore.Mvc;
using CameraServer.Models;
using CameraServer.Services;

namespace CameraServer.Controllers;

[ApiController]
[Route("api/camera")]
public class CameraController(CameraService camera) : ControllerBase
{
    [HttpGet("status")]
    public ActionResult<CameraStatus> Status() => camera.GetStatus();

    [HttpGet("config")]
    public ActionResult<CameraProps> GetConfig() => camera.GetProps();

    [HttpPost("config")]
    public IActionResult SetConfig([FromBody] CameraProps props)
    {
        camera.UpdateProps(props);
        return Ok(new { message = "config updated" });
    }

    /// <summary>Change camera port (index) and reopen.</summary>
    [HttpPost("port")]
    public IActionResult SetPort([FromBody] PortRequest req)
    {
        camera.SetPort(req.Port);
        return Ok(new { message = $"port set to {req.Port}" });
    }

    /// <summary>Force-reopen camera (useful after black-frame recovery).</summary>
    [HttpPost("reopen")]
    public IActionResult Reopen()
    {
        camera.ForceReopen();
        return Ok(new { message = "reopen triggered" });
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        camera.StopCamera();
        return Ok(new { message = "camera stopped" });
    }

    [HttpPost("start")]
    public IActionResult Start()
    {
        camera.StartCamera();
        return Ok(new { message = "camera started" });
    }

    [HttpPost("resolution")]
    public IActionResult SetResolution([FromBody] ResolutionRequest req)
    {
        camera.SetResolution(req.Width, req.Height);
        return Ok(new { message = $"resolution set to {req.Width}×{req.Height}" });
    }

    [HttpPost("fps")]
    public IActionResult SetFps([FromBody] FpsRequest req)
    {
        camera.SetTargetFps(req.Fps);
        return Ok(new { message = $"fps set to {req.Fps}" });
    }

    [HttpPost("rotation")]
    public IActionResult SetRotation([FromBody] RotationRequest req)
    {
        camera.SetRotation(req.Degrees);
        return Ok(new { message = $"rotation set to {req.Degrees}°" });
    }

    [HttpPost("save")]
    public IActionResult SaveSettings()
    {
        camera.Save();
        return Ok(new { message = "settings saved" });
    }

    /// <summary>
    /// Open the camera's native DirectShow property dialog on the server screen.
    /// The dialog shows manufacturer controls: exposure, focus, white balance, etc.
    /// </summary>
    [HttpPost("show-properties")]
    public IActionResult ShowProperties()
    {
        var (ok, message) = camera.ShowPropertiesDialog();
        return ok ? Ok(new { message }) : BadRequest(new { message });
    }
}

public record PortRequest(int Port);
public record ResolutionRequest(int Width, int Height);
public record FpsRequest(int Fps);
public record RotationRequest(int Degrees);
