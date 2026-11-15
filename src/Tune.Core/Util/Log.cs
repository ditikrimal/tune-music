namespace Tune.Core.Util;

/// <summary>Tiny file logger: no-op until <see cref="Init"/>; 1 MB cap with one rollover file.</summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();
    private static string? _path;

    public static void Init(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _path = path;
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? e = null) => Write("WARN", message, e);
    public static void Error(string message, Exception? e = null) => Write("ERROR", message, e);

    private static void Write(string level, string message, Exception? e)
    {
        if (_path is null) return;
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(_path, _path + ".1", overwrite: true);
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
                if (e is not null) line += $" | {e.GetType().Name}: {e.Message}";
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // logging must never break the app
        }
    }
}
