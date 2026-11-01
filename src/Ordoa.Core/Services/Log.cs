using System.Runtime.InteropServices;

namespace Ordoa.Core.Services;

public static class Log
{
    private static readonly object Sync = new();
    private static readonly long MaxSize = 2 * 1024 * 1024;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Util.AppPaths.StateDirectory);
                var file = Util.AppPaths.LogFile;
                if (File.Exists(file) && new FileInfo(file).Length > MaxSize)
                {
                    var previous = file + ".old";
                    try { File.Move(file, previous, true); } catch { /* ignore */ }
                }

                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (exception is not null)
                    line += Environment.NewLine + exception;
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never break the application.
        }
    }
}
