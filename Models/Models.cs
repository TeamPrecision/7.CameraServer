using System.Text.Json.Serialization;

namespace CameraServer.Models;

// ─────────── Enums ───────────
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestMode { Normal, Read2d, Compare, CheckLed, BlinkLed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestStatus { Idle, Running, Pass, Fail, Timeout, Error, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CameraState { Opening, Ready, BlackFrame, Reopening, Error, Stopped }

// ─────────── ROI ───────────
public class RoiConfig
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width  { get; set; }
    public int Height { get; set; }
    public bool IsValid => Width > 0 && Height > 0;
}

// ─────────── Test request / result ───────────
public class TestRequest
{
    public TestMode Mode        { get; set; } = TestMode.Normal;
    public string   Head        { get; set; } = "";
    public string   Step        { get; set; } = "";
    public int      TimeoutMs   { get; set; } = 30_000;
    public RoiConfig Roi        { get; set; } = new();

    // Read2d
    public int  ExpectedDigits  { get; set; } = 10;
    public int  AdjustDegree    { get; set; } = 0;

    // CheckLed / BlinkLed  — BGR low[0..2] / high[0..2]  OR  HSV low[0..2] / high[0..2]
    public bool     UseHsv        { get; set; }
    public double[] ColorLow      { get; set; } = [0, 0, 0];
    public double[] ColorHigh     { get; set; } = [255, 255, 255];
    public int      ColorMaskPx   { get; set; } = 100;   // min pixels that must match
    public int      ColorHoldMs   { get; set; } = 500;   // must hold for this long

    // BlinkLed
    public double ExpectedFrequency  { get; set; }
    public double ExpectedDuty       { get; set; }
    public double FrequencyTolerance { get; set; } = 0.5;
    public double DutyTolerance      { get; set; } = 5.0;
    public int    BlinkSampleCount   { get; set; } = 20;  // cycles to average

    // Compare
    public string CompareFolder { get; set; } = "";
    public int    CompareSteps  { get; set; } = 1;
}

public class TestResult
{
    public TestStatus Status    { get; set; }
    public string     Value     { get; set; } = "";
    public string     Details   { get; set; } = "";
    public long       ElapsedMs { get; set; }
    public string     Head      { get; set; } = "";
    public string     Step      { get; set; } = "";
    public DateTime   Timestamp { get; set; } = DateTime.Now;
}

// ─────────── Camera config (runtime-adjustable props) ───────────
public class CameraProps
{
    public int Width      { get; set; } = 1280;
    public int Height     { get; set; } = 720;
    public int Brightness { get; set; } = 128;
    public int Contrast   { get; set; } = 128;
    public int Saturation { get; set; } = 128;
    public int Sharpness  { get; set; } = 128;
    public int Gain       { get; set; } = 0;
    public int Zoom       { get; set; } = 0;
    public int Pan        { get; set; } = 0;
    public int Tilt       { get; set; } = 0;
    public int Focus      { get; set; } = -1;   // -1 = auto
    public int Exposure   { get; set; } = -999; // -999 = auto
}

// ─────────── Status ───────────
public class CameraStatus
{
    public CameraState State          { get; set; }
    public bool        Enabled        { get; set; }
    public int         Port           { get; set; }
    public int         Width          { get; set; }
    public int         Height         { get; set; }
    public double      Fps            { get; set; }
    public int         TargetFps      { get; set; }
    public int         Rotation       { get; set; }
    public TestStatus  TestStatus     { get; set; }
    public TestMode?   ActiveMode     { get; set; }
    public int         BlackFrameCount{ get; set; }
    public long        UptimeSeconds  { get; set; }
}

// ─────────── Log ───────────
public class LogEntry
{
    public string   Time     { get; set; } = "";
    public string   Level    { get; set; } = "INFO";
    public string   Category { get; set; } = "";
    public string   Head     { get; set; } = "";
    public string   Step     { get; set; } = "";
    public string   Message  { get; set; } = "";
}
