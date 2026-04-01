using System.IO;
using System.Runtime.CompilerServices;

namespace FatimaTTS.Services;

/// <summary>
/// Simple file-based logger that writes to %AppData%\FatimaTTS\logs\
/// Log files rotate daily: fatima_2026-04-01.log
/// Old logs beyond 30 days are pruned automatically on startup.
/// </summary>
public sealed class AppLogger : IDisposable
{
    private readonly string _logDir;
    private StreamWriter?   _writer;
    private string?         _currentFile;
    private readonly object _lock = new();

    public AppLogger()
    {
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FatimaTTS", "logs");
        Directory.CreateDirectory(_logDir);
        PruneOldLogs();
    }

    // ── Public API ────────────────────────────────────────────────────────

    public void Info(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath]   string file   = "")
        => Write("INFO ", caller, file, message);

    public void Warn(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath]   string file   = "")
        => Write("WARN ", caller, file, message);

    public void Error(string message, Exception? ex = null,
        [CallerMemberName] string caller = "",
        [CallerFilePath]   string file   = "")
    {
        Write("ERROR", caller, file, message);
        if (ex is not null)
            Write("ERROR", caller, file, $"  → {ex.GetType().Name}: {ex.Message}");
    }

    public void Debug(string message,
        [CallerMemberName] string caller = "",
        [CallerFilePath]   string file   = "")
    {
#if DEBUG
        Write("DEBUG", caller, file, message);
#endif
    }

    public string LogDirectory => _logDir;

    // ── Core write ────────────────────────────────────────────────────────

    private void Write(string level, string caller, string filePath, string message)
    {
        lock (_lock)
        {
            EnsureWriter();
            var className = Path.GetFileNameWithoutExtension(filePath);
            var line      = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{className}.{caller}] {message}";
            _writer?.WriteLine(line);
            _writer?.Flush();
        }
    }

    private void EnsureWriter()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var target = Path.Combine(_logDir, $"fatima_{today}.log");

        if (_currentFile == target && _writer is not null) return;

        _writer?.Dispose();
        _writer      = new StreamWriter(target, append: true, System.Text.Encoding.UTF8);
        _currentFile = target;
        _writer.WriteLine($"[{DateTime.Now:HH:mm:ss}] [----] Session started — Fatima TTS");
        _writer.Flush();
    }

    // ── Log pruning ───────────────────────────────────────────────────────

    private void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Today.AddDays(-30);
            foreach (var file in Directory.GetFiles(_logDir, "fatima_*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch { /* non-fatal */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
