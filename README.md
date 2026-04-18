# 📷 CameraServer

An ASP.NET Core 8 web application that keeps a DirectShow camera open permanently and exposes it via REST API and a browser-based dashboard. Designed for automated vision testing on manufacturing or QA lines.

## Features

- **Persistent camera** — opens once, stays open; no open/close overhead per test call
- **Live MJPEG stream** in the browser at any resolution
- **Five test modes** — Read2d (DataMatrix barcode), Compare (feature matching), CheckLed, BlinkLed, Normal
- **Blocking REST API** — `POST /api/test/start` returns only when the test result is ready
- **Snapshot freeze** — click to freeze the display; resume live at any time
- **Image rotation** — 0°/90°/180°/270°, applied server-side to all frames and tests
- **Camera on/off control** — start and stop the camera without restarting the server
- **Structured JSONL logging** with in-memory ring buffer and monthly file export
- **Persistent settings** — port, resolution, FPS, and rotation survive server restarts

## Requirements

| Component | Version |
|-----------|---------|
| .NET SDK | 8.0+ |
| Windows | 10 / 11 (DirectShow required) |
| Emgu.CV | 4.9.0 |
| ZXing.Net | 0.16.9 |
| Camera | Any DirectShow-compatible USB or built-in webcam |

## Quick Start

```bash
# Clone or copy the project folder
cd CameraServer

# Run the server
dotnet run

# Open the dashboard
# http://localhost:5000
```

The server listens on `http://0.0.0.0:5000` by default (configurable in `appsettings.json`).

## Project Structure

```
CameraServer/
├── Controllers/
│   ├── CameraController.cs   # /api/camera/*
│   ├── TestController.cs     # /api/test/*
│   ├── StreamController.cs   # /stream, /snapshot
│   └── LogController.cs      # /api/logs/*
├── Services/
│   ├── CameraService.cs      # Core camera + test engine (BackgroundService)
│   └── LogService.cs         # Structured JSONL logger
├── Models/
│   └── Models.cs             # All DTOs and enums
├── wwwroot/
│   ├── index.html            # Single-page dashboard
│   ├── app.js                # Frontend logic
│   └── style.css             # Dark theme
├── data/
│   └── settings.json         # Persisted runtime settings (auto-created)
├── logs/
│   └── camera_YYYY-MM.jsonl  # Monthly log files (auto-created)
├── appsettings.json          # Server configuration
├── CameraServer.postman_collection.json  # Postman API collection
└── CameraServer_Manual.html  # Full usage manual (open in browser → Print → Save as PDF)
```

## Configuration (`appsettings.json`)

```json
{
  "Urls": "http://0.0.0.0:5000",
  "Camera": {
    "Port": 0,
    "Api": "DShow",
    "TargetFps": 30,
    "BlackFrameThreshold": 5,
    "BlackFrameMean": 8.0,
    "ReopenDelayMs": 800
  }
}
```

| Key | Description |
|-----|-------------|
| `Port` | Camera device index (0 = first camera) |
| `Api` | `DShow` (DirectShow) or `Any` |
| `TargetFps` | Default capture rate |
| `BlackFrameThreshold` | Consecutive black frames before auto-reopen |
| `BlackFrameMean` | Pixel mean threshold to classify a frame as black |
| `ReopenDelayMs` | Delay before reopen attempt after black frame |

Runtime settings (port, resolution, FPS, rotation) are saved to `data/settings.json` by clicking **Save Settings** in the dashboard and override `appsettings.json` on next startup.

## API Overview

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/camera/status` | Camera state, resolution, FPS, uptime |
| POST | `/api/camera/port` | Change camera index |
| POST | `/api/camera/resolution` | Change resolution (triggers reopen) |
| POST | `/api/camera/fps` | Set target FPS |
| POST | `/api/camera/rotation` | Set rotation (0/90/180/270°) |
| POST | `/api/camera/save` | Persist settings to disk |
| POST | `/api/camera/stop` | Stop camera (LED off) |
| POST | `/api/camera/start` | Start camera |
| POST | `/api/camera/reopen` | Force reopen |
| POST | `/api/camera/show-properties` | Open DirectShow properties dialog |
| **POST** | **`/api/test/start`** | **Run a test — blocks until result** |
| DELETE | `/api/test/cancel` | Cancel running test |
| GET | `/stream` | MJPEG live stream |
| GET | `/snapshot` | Download latest frame as JPEG |
| GET | `/snapshot/inline` | Latest frame inline (no download) |
| GET | `/api/logs` | Recent log entries |
| GET | `/api/logs/export` | Download monthly JSONL log file |

See `CameraServer.postman_collection.json` for ready-to-use request examples for all endpoints.

## Test Modes

### Read2d
Reads a 2D DataMatrix barcode from the camera frame. Tries multiple small rotations until decoded or timeout.

### Compare
KAZE feature matching against a reference PNG image. Supports multi-step sequences.

### CheckLed
Detects a solid LED color (BGR or HSV). PASS when enough pixels are in the color range for a hold duration.

### BlinkLed
Measures LED blink frequency (Hz) and duty cycle (%). Samples a configurable number of full cycles.

### Normal
No processing — keeps the camera streaming. Useful for live observation tests.

## Logging

Log entries are written to `logs/camera_YYYY-MM.jsonl` (one JSON object per line) and kept in a 500-entry in-memory ring buffer for the dashboard.

```jsonl
{"time":"2025-01-15T10:23:01.123","level":"EVENT","category":"TEST_START","head":"A","step":"scan","message":"mode=Read2d,head=A,step=scan,timeout=30000"}
{"time":"2025-01-15T10:23:03.456","level":"EVENT","category":"TEST_END","head":"A","step":"scan","message":"status=Pass,value=1234567890,elapsed=2333ms"}
```

## License

MIT — see [LICENSE](LICENSE).
