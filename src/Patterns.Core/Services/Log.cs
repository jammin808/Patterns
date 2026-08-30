using System.Text;

namespace Patterns.Core.Services;

/// <summary>
/// Tiny portable logger: appends to patterns.log beside the exe. Never throws —
/// a failing logger must not take a show down.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string directory)
    {
        try
        {
            _path = Path.Combine(directory, "patterns.log");
            // Rotate if it grew past 2 MB — this app can run for weeks.
            if (File.Exists(_path) && new FileInfo(_path).Length > 2 * 1024 * 1024)
            {
                File.Copy(_path, _path + ".old", overwrite: true);
                File.WriteAllText(_path, "");
            }
            Info($"—— Patterns started (v{typeof(Log).Assembly.GetName().Version}) ——");
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public static void Error(string message, Exception? ex = null)
    {
        // Every contained error passes through here — it doubles as the health counter.
        HealthMonitor.Record(message);
        Write("ERROR", message, ex);
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(message);
            if (ex is not null) sb.Append(" :: ").Append(ex);
            var line = sb.ToString();

            if (_path is not null)
            {
                lock (Gate)
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
            }
            else
            {
                Console.Error.WriteLine(line);
            }
        }
        catch
        {
            // Swallow: logging is best-effort by design.
        }
    }
}
