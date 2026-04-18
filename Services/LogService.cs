using System.Collections.Concurrent;
using System.Text.Json;
using CameraServer.Models;

namespace CameraServer.Services;

public class LogService
{
    private readonly string _logDir;
    private readonly int    _maxMonths;
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private const int MaxRecent = 500;
    private readonly object _writeLock = new();

    public LogService(IConfiguration cfg)
    {
        _logDir   = cfg["Log:Path"] ?? "logs";
        _maxMonths = int.TryParse(cfg["Log:MaxMonths"], out var m) ? m : 6;
        Directory.CreateDirectory(_logDir);
    }

    public void Info (string cat, string msg, string head = "", string step = "") => Write("INFO",  cat, msg, head, step);
    public void Warn (string cat, string msg, string head = "", string step = "") => Write("WARN",  cat, msg, head, step);
    public void Error(string cat, string msg, string head = "", string step = "") => Write("ERROR", cat, msg, head, step);
    public void Event(string cat, string msg, string head = "", string step = "") => Write("EVENT", cat, msg, head, step);

    public void Write(string level, string category, string message, string head = "", string step = "")
    {
        var entry = new LogEntry
        {
            Time     = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
            Level    = level,
            Category = category,
            Head     = head,
            Step     = step,
            Message  = message
        };

        // In-memory ring buffer for UI
        _recent.Enqueue(entry);
        while (_recent.Count > MaxRecent) _recent.TryDequeue(out _);

        // Write to JSONL file
        lock (_writeLock)
        {
            try
            {
                var now  = DateTime.Now;
                var file = Path.Combine(_logDir, $"camera_{now:yyyy-MM}.jsonl");
                var line = JsonSerializer.Serialize(entry);
                File.AppendAllText(file, line + "\n");
                PurgeOldFiles();
            }
            catch { /* log write must never throw */ }
        }
    }

    public IReadOnlyList<LogEntry> GetRecent(int count = 100, string? level = null, string? category = null)
    {
        var all = _recent.ToArray();
        IEnumerable<LogEntry> q = all.Reverse();
        if (level    != null) q = q.Where(e => e.Level.Equals(level,    StringComparison.OrdinalIgnoreCase));
        if (category != null) q = q.Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        return q.Take(count).ToList();
    }

    public string? GetCurrentLogFilePath()
    {
        var file = Path.Combine(_logDir, $"camera_{DateTime.Now:yyyy-MM}.jsonl");
        return File.Exists(file) ? file : null;
    }

    private void PurgeOldFiles()
    {
        var cutoff = DateTime.Now.AddMonths(-_maxMonths);
        foreach (var f in Directory.GetFiles(_logDir, "camera_*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(f); // camera_2024-01
            if (DateTime.TryParse(name.Replace("camera_", "") + "-01", out var d) && d < cutoff)
                try { File.Delete(f); } catch { }
        }
    }
}
