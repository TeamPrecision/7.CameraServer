using System.Diagnostics;
using System.Drawing;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Features2D;
using Emgu.CV.Flann;
using Emgu.CV.Structure;
using Emgu.CV.Util;
using ZXing;
using CameraServer.Models;

namespace CameraServer.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  CameraService  (singleton BackgroundService)
// ─────────────────────────────────────────────────────────────────────────────
public class CameraService : IHostedService, IDisposable
{
    private sealed class TestSession(TestRequest req)
    {
        private readonly TaskCompletionSource<TestResult> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestRequest  Request   { get; } = req;
        public Stopwatch    Elapsed   { get; } = Stopwatch.StartNew();

        // CheckLed / BlinkLed state
        public Stopwatch LedHighTimer { get; } = new Stopwatch();
        public Stopwatch LedLowTimer  { get; } = Stopwatch.StartNew();
        public bool   LedWasHigh    { get; set; }
        public double BlinkFreqAcc  { get; set; }
        public double BlinkDutyAcc  { get; set; }
        public int    BlinkCount    { get; set; }

        // Compare state
        public int CompareStep { get; set; } = 1;

        public void Complete(TestResult r)  => _tcs.TrySetResult(r);
        public void Cancel()                => _tcs.TrySetCanceled();
        public Task<TestResult> WaitAsync(CancellationToken ct) => _tcs.Task.WaitAsync(ct);
    }

    // ── camera handle ──
    private VideoCapture?       _cap;
    private VideoCapture.API    _api   = VideoCapture.API.DShow;
    private int                 _port;
    private volatile bool       _running;
    private Thread?             _thread;
    private CameraState         _state = CameraState.Opening;

    // ── frame buffer ──
    private volatile byte[]?    _latestJpeg;
    private long                _frameCount;
    private readonly Stopwatch  _fpsWatch       = Stopwatch.StartNew();
    private readonly Stopwatch  _frameStopwatch = Stopwatch.StartNew();
    private double              _fps;

    // ── rotation / fps ──
    private int _rotationDegrees = 0;
    private int _targetFps       = 30;

    // ── property dialog ──
    private volatile bool _showingDialog = false;

    // ── on/off / reopen signals (written by HTTP threads, consumed by capture thread) ──
    private volatile bool _cameraEnabled    = true;
    private volatile bool _stopRequested    = false;
    private volatile bool _reopenRequested  = false;

    // ── black-frame guard ──
    private int  _consecutiveBlack;
    private readonly int    _blackThreshold;
    private readonly double _blackMean;
    private readonly int    _reopenDelay;

    // ── session ──
    private volatile TestSession? _session;
    private readonly object       _sessionLock = new();

    // ── config / services ──
    private CameraProps         _props;
    private readonly LogService _log;
    private readonly IConfiguration _cfg;
    private readonly Stopwatch  _uptime = Stopwatch.StartNew();

    private static readonly string SettingsPath = Path.Combine("data", "settings.json");

    private class SavedSettings
    {
        public int Port      { get; set; }
        public int Width     { get; set; }
        public int Height    { get; set; }
        public int TargetFps { get; set; } = 30;
        public int Rotation  { get; set; }
    }

    public CameraService(LogService log, IConfiguration cfg)
    {
        _log  = log;
        _cfg  = cfg;
        _props = cfg.GetSection("CameraProps").Get<CameraProps>() ?? new CameraProps();
        _port  = cfg.GetValue<int>("Camera:Port");
        _targetFps      = cfg.GetValue("Camera:TargetFps", 30);
        _blackThreshold = cfg.GetValue("Camera:BlackFrameThreshold", 5);
        _blackMean      = cfg.GetValue("Camera:BlackFrameMean", 8.0);
        _reopenDelay    = cfg.GetValue("Camera:ReopenDelayMs", 800);

        if (cfg["Camera:Api"]?.Equals("Any", StringComparison.OrdinalIgnoreCase) == true)
            _api = VideoCapture.API.Any;

        LoadSavedSettings();
    }

    private void LoadSavedSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var s = System.Text.Json.JsonSerializer.Deserialize<SavedSettings>(
                        File.ReadAllText(SettingsPath));
            if (s == null) return;
            _port           = s.Port;
            _targetFps      = s.TargetFps > 0 ? s.TargetFps : _targetFps;
            _rotationDegrees = s.Rotation;
            if (s.Width  > 0) _props.Width  = s.Width;
            if (s.Height > 0) _props.Height = s.Height;
        }
        catch { }
    }

    private void PersistSettings()
    {
        try
        {
            Directory.CreateDirectory("data");
            File.WriteAllText(SettingsPath,
                System.Text.Json.JsonSerializer.Serialize(new SavedSettings
                {
                    Port      = _port,
                    Width     = _props.Width,
                    Height    = _props.Height,
                    TargetFps = _targetFps,
                    Rotation  = _rotationDegrees
                }));
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  IHostedService
    // ─────────────────────────────────────────────────────────────────────────
    public Task StartAsync(CancellationToken _)
    {
        _running = true;
        _log.Event("CAM_SERVICE_START", $"port={_port}");
        OpenCamera();
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "CamCapture" };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken _)
    {
        _running = false;
        _thread?.Join(3000);
        _log.Event("CAM_SERVICE_STOP", $"port={_port}");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cap?.Dispose();
        _cap = null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API used by controllers
    // ─────────────────────────────────────────────────────────────────────────
    public byte[]? GetLatestJpeg() => _latestJpeg;

    public CameraStatus GetStatus() => new()
    {
        State           = _state,
        Enabled         = _cameraEnabled,
        Port            = _port,
        Width           = _cap?.Width  ?? 0,
        Height          = _cap?.Height ?? 0,
        Fps             = Math.Round(_fps, 1),
        TargetFps       = _targetFps,
        Rotation        = _rotationDegrees,
        TestStatus      = _session == null ? TestStatus.Idle : TestStatus.Running,
        ActiveMode      = _session?.Request.Mode,
        BlackFrameCount = _consecutiveBlack,
        UptimeSeconds   = (long)_uptime.Elapsed.TotalSeconds
    };

    public CameraProps GetProps()           => _props;
    public int         GetPort()            => _port;

    /// <summary>Start a test, block (async) until result or timeout.</summary>
    public async Task<TestResult> RunTestAsync(TestRequest req, CancellationToken ct = default)
    {
        TestSession session;
        lock (_sessionLock)
        {
            if (_session != null)
                return new TestResult { Status = TestStatus.Error, Details = "Another test is already running" };

            session  = new TestSession(req);
            _session = session;
        }

        _log.Event("TEST_START", $"mode={req.Mode},head={req.Head},step={req.Step},timeout={req.TimeoutMs}",
            req.Head, req.Step);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(req.TimeoutMs);

        TestResult result;
        try
        {
            result = await session.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = new TestResult
            {
                Status    = ct.IsCancellationRequested ? TestStatus.Cancelled : TestStatus.Timeout,
                Details   = ct.IsCancellationRequested ? "cancelled by caller" : "timeout",
                ElapsedMs = session.Elapsed.ElapsedMilliseconds,
                Head      = req.Head,
                Step      = req.Step
            };
        }
        finally
        {
            lock (_sessionLock) { if (_session == session) _session = null; }
        }

        _log.Event("TEST_END",
            $"status={result.Status},value={result.Value},elapsed={result.ElapsedMs}ms",
            req.Head, req.Step);

        return result;
    }

    public void CancelTest()
    {
        lock (_sessionLock)
        {
            _session?.Cancel();
            _session = null;
        }
    }

    public void UpdateProps(CameraProps props)
    {
        // Preserve resolution from current props if caller didn't send them
        if (props.Width  <= 0) props.Width  = _props.Width;
        if (props.Height <= 0) props.Height = _props.Height;
        _props = props;
        ApplyImageQualityProps();
        _log.Event("CAM_CONFIG_UPDATED", $"brightness={props.Brightness},contrast={props.Contrast},gain={props.Gain}");
    }

    public void SetPort(int port)
    {
        _port = port;
        _log.Event("CAM_PORT_CHANGE", $"port={port}");
        PersistSettings();
        if (_cameraEnabled) _reopenRequested = true;
    }

    public void ForceReopen()
    {
        if (_cameraEnabled) _reopenRequested = true;
    }

    // Signal the capture thread to stop; it will dispose _cap itself.
    public void StopCamera()
    {
        _stopRequested = true;
        _cameraEnabled = false;
        _log.Event("CAM_STOP", $"port={_port}");
    }

    // Signal the capture thread to (re)open the camera.
    public void StartCamera()
    {
        if (_cameraEnabled) return;
        _cameraEnabled   = true;
        _reopenRequested = true;
        _log.Event("CAM_START", $"port={_port}");
    }

    public void SetResolution(int width, int height)
    {
        _props.Width  = width;
        _props.Height = height;
        _log.Event("CAM_RES_CHANGE", $"width={width},height={height}");
        PersistSettings();
        if (_cameraEnabled) _reopenRequested = true;
    }

    public void SetTargetFps(int fps)
    {
        _targetFps = Math.Max(1, Math.Min(fps, 120));
        _log.Event("CAM_FPS_CHANGE", $"fps={_targetFps}");
    }

    public void SetRotation(int degrees)
    {
        _rotationDegrees = degrees switch { 90 => 90, 180 => 180, 270 => 270, _ => 0 };
        _log.Event("CAM_ROTATION_CHANGE", $"degrees={_rotationDegrees}");
    }

    public void Save()
    {
        PersistSettings();
        _log.Event("CAM_SETTINGS_SAVED", $"fps={_targetFps},rotation={_rotationDegrees},port={_port},res={_props.Width}x{_props.Height}");
    }

    /// <summary>
    /// Open the camera manufacturer's native property dialog on the server screen.
    /// Uses OpenCV DirectShow CAP_PROP_SETTINGS (value 37) on an STA thread.
    /// Capture loop is paused while the dialog is open.
    /// </summary>
    public (bool ok, string message) ShowPropertiesDialog()
    {
        if (_cap == null || _cap.Ptr == IntPtr.Zero || _cap.Width == 0)
            return (false, "Camera is not open");

        if (_showingDialog)
            return (false, "Property dialog is already open");

        _showingDialog = true;
        Thread.Sleep(80); // let capture loop finish its current frame

        var t = new Thread(() =>
        {
            try
            {
                _log.Event("CAM_PROP_DIALOG_OPEN", $"port={_port}");
                // CV_CAP_PROP_SETTINGS = 37 — tells OpenCV DirectShow backend
                // to call OleCreatePropertyFrame with the camera's property pages
                _cap?.Set((CapProp)37, 1);
                _log.Event("CAM_PROP_DIALOG_CLOSE", $"port={_port}");
            }
            catch (Exception ex)
            {
                _log.Error("CAM_PROP_DIALOG_EX", ex.Message);
            }
            finally
            {
                _showingDialog = false;
            }
        });
        t.SetApartmentState(ApartmentState.STA); // COM dialogs require STA
        t.IsBackground = true;
        t.Name = "CamPropsDialog";
        t.Start();

        return (true, "Property dialog opened on server screen");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Camera open / reopen
    // ─────────────────────────────────────────────────────────────────────────
    private void OpenCamera()
    {
        _state = CameraState.Opening;
        try
        {
            _cap?.Dispose();
            _cap = new VideoCapture(_port, _api);
            if (_cap.Width == 0)
            {
                _log.Warn("CAM_OPEN_FAIL", $"port={_port},width=0");
                _state = CameraState.Error;
                return;
            }
            ApplyProps();
            _state = CameraState.Ready;
            _log.Event("CAM_OPEN_OK", $"port={_port},width={_cap.Width},height={_cap.Height}");
        }
        catch (Exception ex)
        {
            _state = CameraState.Error;
            _log.Error("CAM_OPEN_EX", ex.Message);
        }
    }

    private void DoReopenCamera()
    {
        _state = CameraState.Reopening;
        _log.Event("CAM_REOPEN_START", $"port={_port}");
        try { _cap?.Dispose(); } catch { }
        _cap = null;
        Thread.Sleep(_reopenDelay);
        OpenCamera();
        if (_state == CameraState.Ready)
            _log.Event("CAM_REOPEN_OK", $"port={_port},width={_cap?.Width}");
        else
            _log.Warn("CAM_REOPEN_FAIL", $"port={_port}");
    }

    // Called once on camera open — sets resolution first, then quality props
    private void ApplyProps()
    {
        if (_cap == null || _cap.Ptr == IntPtr.Zero) return;
        if (_props.Width  > 0) SetProp(CapProp.FrameWidth,  _props.Width);
        if (_props.Height > 0) SetProp(CapProp.FrameHeight, _props.Height);
        ApplyImageQualityProps();
    }

    // Safe to call at any time — never touches resolution
    private void ApplyImageQualityProps()
    {
        if (_cap == null || _cap.Ptr == IntPtr.Zero) return;
        SetProp(CapProp.Brightness, _props.Brightness);
        SetProp(CapProp.Contrast,   _props.Contrast);
        SetProp(CapProp.Saturation, _props.Saturation);
        SetProp(CapProp.Sharpness,  _props.Sharpness);
        SetProp(CapProp.Gain,       _props.Gain);
        if (_props.Zoom     >  0)   SetProp(CapProp.Zoom,     _props.Zoom);
        if (_props.Pan      != 0)   SetProp(CapProp.Pan,      _props.Pan);
        if (_props.Tilt     != 0)   SetProp(CapProp.Tilt,     _props.Tilt);
        if (_props.Focus    >= 0)   SetProp(CapProp.Focus,    _props.Focus);
        if (_props.Exposure > -999) SetProp(CapProp.Exposure, _props.Exposure);
    }

    private void SetProp(CapProp prop, double val)
    {
        try { _cap?.Set(prop, val); } catch { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Capture loop  (dedicated background thread)
    // ─────────────────────────────────────────────────────────────────────────
    private void CaptureLoop()
    {
        while (_running)
        {
            try   { ProcessFrame(); }
            catch (Exception ex)
            {
                _log.Error("CAP_LOOP_EX", ex.Message);
                Thread.Sleep(200);
            }
        }
    }

    private void ProcessFrame()
    {
        // ── stopped: dispose cap on this thread then idle ──
        if (!_cameraEnabled)
        {
            if (_stopRequested)
            {
                _stopRequested = false;
                try { _cap?.Dispose(); } catch { }
                _cap   = null;
                _state = CameraState.Stopped;
                _fps   = 0;
            }
            Thread.Sleep(100);
            return;
        }

        // ── reopen signal from HTTP thread ──
        if (_reopenRequested)
        {
            _reopenRequested = false;
            DoReopenCamera();
            return;
        }

        // pause while native property dialog is open
        if (_showingDialog) { Thread.Sleep(50); return; }

        // ── health check ──
        if (_cap == null || _cap.Ptr == IntPtr.Zero || _cap.Width == 0)
        {
            _log.Warn("CAM_NULL", $"port={_port}");
            DoReopenCamera();
            Thread.Sleep(500);
            return;
        }

        ThrottleFps();

        using var rawFrame = _cap.QueryFrame();
        if (rawFrame == null || rawFrame.IsEmpty) { Thread.Sleep(10); return; }

        using var mat = ApplyRotation(rawFrame);

        // ── black frame guard ──
        if (IsBlackFrame(mat))
        {
            _consecutiveBlack++;
            _log.Warn("BLACK_FRAME", $"consecutive={_consecutiveBlack}");
            _state = CameraState.BlackFrame;
            if (_consecutiveBlack >= _blackThreshold)
            {
                _consecutiveBlack = 0;
                DoReopenCamera();
            }
            return;
        }
        _consecutiveBlack = 0;
        _state = CameraState.Ready;

        // ── mode processing ──
        var session = _session;
        string overlayText = "";
        bool passThisFrame = false;

        if (session != null)
        {
            if (session.Elapsed.ElapsedMilliseconds > session.Request.TimeoutMs)
            {
                FinishSession(session, TestStatus.Timeout, "", "timeout");
            }
            else
            {
                (passThisFrame, overlayText) = ProcessSession(mat, session);
            }
        }

        // ── update JPEG ──
        EncodeAndStore(mat, session?.Request.Roi, overlayText);

        // ── FPS counter ──
        _frameCount++;
        if (_fpsWatch.Elapsed.TotalSeconds >= 2)
        {
            _fps = _frameCount / _fpsWatch.Elapsed.TotalSeconds;
            _frameCount = 0;
            _fpsWatch.Restart();
        }

        _ = passThisFrame; // consumed inside ProcessSession
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Mode processing
    // ─────────────────────────────────────────────────────────────────────────
    private (bool pass, string overlay) ProcessSession(Mat frame, TestSession session)
    {
        var req = session.Request;
        return req.Mode switch
        {
            TestMode.Read2d   => ProcessRead2d  (frame, session),
            TestMode.Compare  => ProcessCompare (frame, session),
            TestMode.CheckLed => ProcessCheckLed(frame, session),
            TestMode.BlinkLed => ProcessBlinkLed(frame, session),
            _                 => (false, "Normal")
        };
    }

    // ── barcode decode helper using raw grayscale bytes (no Bitmap needed) ──
    private static ZXing.Result? DecodeBarcode(Image<Bgr, byte> img)
    {
        using var gray = img.Convert<Gray, byte>();
        var src    = new ZXing.RGBLuminanceSource(gray.Bytes, img.Width, img.Height,
                         ZXing.RGBLuminanceSource.BitmapFormat.Gray8);
        var reader = new ZXing.BarcodeReaderGeneric();
        return reader.Decode(src);
    }

    // ── Read2d ──────────────────────────────────────────────────────────────
    private (bool, string) ProcessRead2d(Mat frame, TestSession session)
    {
        var req = session.Request;
        using var roi = CropRoi(frame, req.Roi);
        using var img = roi.ToImage<Bgr, byte>();

        // optional pre-rotation
        Image<Bgr, byte>? working = req.AdjustDegree != 0
            ? img.Rotate(req.AdjustDegree, new Bgr())
            : img;

        ZXing.Result? result = null;

        for (int i = 0; i <= 17 && result == null; i++)
        {
            using var rotated = i == 0 ? working.Copy() : working.Rotate(i * 5, new Bgr());
            result = DecodeBarcode(rotated);
            if (result == null && i > 0)
            {
                using var rotatedNeg = working.Rotate(-i * 5, new Bgr());
                result = DecodeBarcode(rotatedNeg);
            }
        }

        if (req.AdjustDegree != 0) working?.Dispose();

        if (result == null)
            return (false, "not read");

        bool pass = req.ExpectedDigits <= 0 || result.Text.Length == req.ExpectedDigits;
        string overlay = $"{result.Text}  [{(pass ? "OK" : "DIGIT?")}]";

        if (pass)
            FinishSession(session, TestStatus.Pass, result.Text,
                $"digits={result.Text.Length}");
        else
            FinishSession(session, TestStatus.Fail, result.Text,
                $"digits={result.Text.Length},expected={req.ExpectedDigits}");

        return (pass, overlay);
    }

    // ── Compare ─────────────────────────────────────────────────────────────
    private (bool, string) ProcessCompare(Mat frame, TestSession session)
    {
        var req = session.Request;
        string folder = string.IsNullOrEmpty(req.CompareFolder)
            ? Path.Combine("config", req.Step)
            : req.CompareFolder;

        string refPath = Path.Combine(folder, $"{req.Head}Image{session.CompareStep}.png");
        if (!File.Exists(refPath))
        {
            FinishSession(session, TestStatus.Error, "", $"reference not found: {refPath}");
            return (false, $"ref missing: step{session.CompareStep}");
        }

        using var roi    = CropRoi(frame, req.Roi);
        using var model  = CvInvoke.Imread(refPath, ImreadModes.Grayscale);
        using var obs    = roi.Clone();

        int matches = CountFeatureMatches(model, obs);
        bool found  = matches > 0;
        string overlay = $"step={session.CompareStep} matches={matches} [{(found ? "OK" : "NO")}]";

        if (!found) return (false, overlay);

        session.CompareStep++;
        string nextRef = Path.Combine(folder, $"{req.Head}Image{session.CompareStep}.png");
        if (!File.Exists(nextRef))
        {
            FinishSession(session, TestStatus.Pass, "Image Detected",
                $"allSteps={session.CompareStep - 1}");
        }
        return (found, overlay);
    }

    // ── CheckLed ────────────────────────────────────────────────────────────
    private (bool, string) ProcessCheckLed(Mat frame, TestSession session)
    {
        int px = CountColorPixels(frame, session.Request);
        bool above = px >= session.Request.ColorMaskPx;

        if (above)
        {
            if (!session.LedHighTimer.IsRunning) session.LedHighTimer.Restart();
            if (session.LedHighTimer.ElapsedMilliseconds >= session.Request.ColorHoldMs)
            {
                FinishSession(session, TestStatus.Pass, "Color Detected",
                    $"pixels={px},holdMs={session.LedHighTimer.ElapsedMilliseconds}");
                return (true, $"px={px} PASS");
            }
        }
        else
        {
            session.LedHighTimer.Reset();
        }

        return (false, $"px={px}/{session.Request.ColorMaskPx}  hold={session.LedHighTimer.ElapsedMilliseconds}ms");
    }

    // ── BlinkLed ─────────────────────────────────────────────────────────────
    private (bool, string) ProcessBlinkLed(Mat frame, TestSession session)
    {
        var req = session.Request;
        int px  = CountColorPixels(frame, req);
        bool high = px >= req.ColorMaskPx;

        if (high != session.LedWasHigh)
        {
            // transition
            if (!session.LedWasHigh) // LOW→HIGH: end of low period
            {
                double timeLow  = session.LedLowTimer.Elapsed.TotalSeconds;
                double timeHigh = session.LedHighTimer.Elapsed.TotalSeconds;
                if (timeLow > 0 && timeHigh > 0)
                {
                    double period = timeLow + timeHigh;
                    session.BlinkFreqAcc += 1.0 / period;
                    session.BlinkDutyAcc += (timeHigh / period) * 100.0;
                    session.BlinkCount++;
                }
                session.LedHighTimer.Restart();
                session.LedLowTimer.Reset();
            }
            else // HIGH→LOW: end of high period
            {
                session.LedHighTimer.Stop();
                session.LedLowTimer.Restart();
            }
            session.LedWasHigh = high;
        }

        if (session.BlinkCount < req.BlinkSampleCount)
            return (false, $"px={px} freq=measuring... ({session.BlinkCount}/{req.BlinkSampleCount})");

        double freq = session.BlinkFreqAcc / session.BlinkCount;
        double duty = session.BlinkDutyAcc / session.BlinkCount;

        bool freqOk = req.ExpectedFrequency <= 0 || Math.Abs(freq - req.ExpectedFrequency) <= req.FrequencyTolerance;
        bool dutyOk = req.ExpectedDuty      <= 0 || Math.Abs(duty - req.ExpectedDuty)      <= req.DutyTolerance;
        bool pass   = freqOk && dutyOk;

        FinishSession(session, pass ? TestStatus.Pass : TestStatus.Fail,
            $"freq={freq:F3}Hz duty={duty:F1}%",
            $"freqOk={freqOk},dutyOk={dutyOk}");

        return (pass, $"freq={freq:F3}Hz duty={duty:F1}% [{(pass ? "PASS" : "FAIL")}]");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────
    private void FinishSession(TestSession session, TestStatus status, string value, string details)
    {
        lock (_sessionLock) { if (_session == session) _session = null; }
        session.Complete(new TestResult
        {
            Status    = status,
            Value     = value,
            Details   = details,
            ElapsedMs = session.Elapsed.ElapsedMilliseconds,
            Head      = session.Request.Head,
            Step      = session.Request.Step,
            Timestamp = DateTime.Now
        });
    }

    private bool IsBlackFrame(Mat m)
    {
        var mean = CvInvoke.Mean(m);
        return (mean.V0 + mean.V1 + mean.V2) < _blackMean;
    }

    private static Mat CropRoi(Mat frame, RoiConfig roi)
    {
        if (!roi.IsValid) return frame.Clone();
        int x = Math.Max(0, roi.X);
        int y = Math.Max(0, roi.Y);
        int w = Math.Min(roi.Width,  frame.Width  - x);
        int h = Math.Min(roi.Height, frame.Height - y);
        if (w <= 0 || h <= 0) return frame.Clone();
        return new Mat(frame, new Rectangle(x, y, w, h));
    }

    private int CountColorPixels(Mat frame, TestRequest req)
    {
        using var roi = CropRoi(frame, req.Roi);
        if (req.UseHsv)
        {
            using var hsv  = new Mat();
            CvInvoke.CvtColor(roi, hsv, ColorConversion.Bgr2Hsv);
            using var img  = hsv.ToImage<Hsv, byte>();
            var low  = new Hsv(req.ColorLow[0],  req.ColorLow[1],  req.ColorLow[2]);
            var high = new Hsv(req.ColorHigh[0], req.ColorHigh[1], req.ColorHigh[2]);
            return img.InRange(low, high).CountNonzero()[0];
        }
        else
        {
            using var img  = roi.ToImage<Bgr, byte>();
            var low  = new Bgr(req.ColorLow[0],  req.ColorLow[1],  req.ColorLow[2]);
            var high = new Bgr(req.ColorHigh[0], req.ColorHigh[1], req.ColorHigh[2]);
            return img.InRange(low, high).CountNonzero()[0];
        }
    }

    private static int CountFeatureMatches(Mat model, Mat observed)
    {
        try
        {
            using var modelGray = new Mat();
            using var obsGray   = new Mat();
            if (model.NumberOfChannels    > 1) CvInvoke.CvtColor(model,    modelGray, ColorConversion.Bgr2Gray);
            else model.CopyTo(modelGray);
            if (observed.NumberOfChannels > 1) CvInvoke.CvtColor(observed, obsGray,   ColorConversion.Bgr2Gray);
            else observed.CopyTo(obsGray);

            using var kaze     = new KAZE();
            using var mkp      = new VectorOfKeyPoint();
            using var okp      = new VectorOfKeyPoint();
            using var mDesc    = new Mat();
            using var oDesc    = new Mat();
            using var uModel   = modelGray.GetUMat(AccessType.Read);
            using var uObs     = obsGray.GetUMat(AccessType.Read);

            kaze.DetectAndCompute(uModel, null, mkp, mDesc, false);
            kaze.DetectAndCompute(uObs,   null, okp, oDesc, false);

            if (mkp.Size == 0 || okp.Size == 0) return 0;

            using var matches = new VectorOfVectorOfDMatch();
            using var lp = new LinearIndexParams();
            using var sp = new SearchParams();
            using var matcher = new FlannBasedMatcher(lp, sp);
            matcher.Add(mDesc);
            matcher.KnnMatch(oDesc, matches, 2, null);

            int good = 0;
            for (int i = 0; i < matches.Size; i++)
            {
                if (matches[i].Size >= 2 && matches[i][0].Distance < 0.75f * matches[i][1].Distance)
                    good++;
            }
            return good;
        }
        catch { return 0; }
    }

    private void ThrottleFps()
    {
        if (_targetFps <= 0) return;
        int targetMs  = 1000 / _targetFps;
        int elapsedMs = (int)_frameStopwatch.ElapsedMilliseconds;
        int waitMs    = targetMs - elapsedMs;
        if (waitMs > 1) Thread.Sleep(waitMs);
        _frameStopwatch.Restart();
    }

    private Mat ApplyRotation(Mat src)
    {
        if (_rotationDegrees == 0) return src.Clone();
        var dst = new Mat();
        CvInvoke.Rotate(src, dst, _rotationDegrees switch
        {
            90  => RotateFlags.Rotate90Clockwise,
            180 => RotateFlags.Rotate180,
            _   => RotateFlags.Rotate90CounterClockwise  // 270
        });
        return dst;
    }

    private void EncodeAndStore(Mat frame, RoiConfig? roi, string overlay)
    {
        try
        {
            using var disp = frame.Clone();

            // draw ROI rectangle
            if (roi != null && roi.IsValid)
                CvInvoke.Rectangle(disp, new Rectangle(roi.X, roi.Y, roi.Width, roi.Height),
                    new MCvScalar(0, 0, 255), 2);

            // overlay text
            if (!string.IsNullOrEmpty(overlay))
                CvInvoke.PutText(disp, overlay, new System.Drawing.Point(10, 30),
                    FontFace.HersheySimplex, 0.6, new MCvScalar(0, 255, 0), 2);

            // FPS overlay
            CvInvoke.PutText(disp, $"{_fps:F0} fps", new System.Drawing.Point(10, disp.Height - 10),
                FontFace.HersheyPlain, 1.0, new MCvScalar(200, 200, 200), 1);

            using var buf = new Emgu.CV.Util.VectorOfByte();
            CvInvoke.Imencode(".jpg", disp, buf,
                new KeyValuePair<ImwriteFlags, int>(ImwriteFlags.JpegQuality, 80));
            _latestJpeg = buf.ToArray();
        }
        catch { /* never throw from store */ }
    }
}
