using System.IO;

namespace DynamicBay.Core;

/// <summary>Plain-text log at %AppData%\DynamicBay\logs\dynamicbay.log (rotated at 1 MB). See docs/TROUBLESHOOTING.md.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string Folder => Path.Combine(AppSettings.Folder, "logs");
    public static string FilePath => Path.Combine(Folder, "dynamicbay.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string area, Exception? ex) => Write("ERROR", $"[{area}] {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    File.Copy(FilePath, FilePath + ".1", true);
                    File.Delete(FilePath);
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
