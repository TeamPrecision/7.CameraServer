using System.Text;
using Microsoft.AspNetCore.Mvc;
using CameraServer.Services;

namespace CameraServer.Controllers;

[ApiController]
public class StreamController(CameraService camera) : ControllerBase
{
    /// <summary>MJPEG live stream — use as &lt;img src="/stream"&gt;</summary>
    [HttpGet("/stream")]
    public async Task Stream(CancellationToken ct)
    {
        Response.ContentType = "multipart/x-mixed-replace;boundary=frame";
        Response.Headers["Cache-Control"]     = "no-cache, no-store";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()
            ?.DisableBuffering();

        byte[] boundary = Encoding.ASCII.GetBytes("--frame\r\nContent-Type: image/jpeg\r\n");

        while (!ct.IsCancellationRequested)
        {
            var jpeg = camera.GetLatestJpeg();
            if (jpeg != null)
            {
                try
                {
                    await Response.Body.WriteAsync(boundary, ct);
                    await Response.Body.WriteAsync(
                        Encoding.ASCII.GetBytes($"Content-Length: {jpeg.Length}\r\n\r\n"), ct);
                    await Response.Body.WriteAsync(jpeg, ct);
                    await Response.Body.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), ct);
                    await Response.Body.FlushAsync(ct);
                }
                catch { break; }
            }
            await Task.Delay(33, ct); // ~30 fps cap
        }
    }

    /// <summary>Single JPEG snapshot — prompts browser download.</summary>
    [HttpGet("/snapshot")]
    public IActionResult Snapshot()
    {
        var jpeg = camera.GetLatestJpeg();
        if (jpeg == null) return NoContent();

        string name = $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
        return File(jpeg, "image/jpeg", name);
    }

    /// <summary>Single JPEG snapshot as inline image (for display in browser).</summary>
    [HttpGet("/snapshot/inline")]
    public IActionResult SnapshotInline()
    {
        var jpeg = camera.GetLatestJpeg();
        if (jpeg == null) return NoContent();
        return File(jpeg, "image/jpeg");
    }
}
