using System;
using System.IO;

namespace DesktopPetUi;

public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                _file ??= Resolve();
                if (_file != null)
                {
                    File.AppendAllText(_file, $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
                }
            }
        }
        catch { }
    }

    private static string? Resolve()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "pet.log"),
            Path.Combine(Path.GetTempPath(), "desktop-pet-ui-pet.log"),
        };
        foreach (var c in candidates)
        {
            try
            {
                File.AppendAllText(c, "");
                return c;
            }
            catch { }
        }
        return null;
    }
}
