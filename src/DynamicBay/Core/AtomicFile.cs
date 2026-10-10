using System.IO;
using System.Text;

namespace DynamicBay.Core;

/// <summary>
/// Writes a file so a crash, a full disk or a power cut never leaves it half written: the text goes into a temp file
/// next to it first, which then takes the old file's place in one step (the old one is kept as "{path}.bak").
/// </summary>
public static class AtomicFile
{
    /// <param name="backup">Keep the previous version as "{path}.bak".</param>
    public static void WriteAllText(string path, string text, Encoding? enc = null, bool backup = true)
    {
        if (AppSettings.ReadOnly) return; // snapshot runs: the user's files stay untouched
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = full + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, enc ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                w.Write(text);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            if (!File.Exists(full)) { File.Move(tmp, full); return; }
            if (!backup) { File.Move(tmp, full, overwrite: true); return; }
            try { File.Replace(tmp, full, full + ".bak", ignoreMetadataErrors: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Replace is not offered by every drive (some network shares, FAT sticks) and fails when a virus scanner
                // holds the backup: copy the backup by hand, then move the new file over the old one.
                try { if (File.Exists(full)) File.Copy(full, full + ".bak", overwrite: true); } catch { }
                File.Move(tmp, full, overwrite: true);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// A file that could not be read is copied aside as "{name}.corrupt-{time}{ext}" before anything overwrites it,
    /// so nothing is lost for good. False: the copy failed (the caller then must not overwrite the file).
    /// </summary>
    public static bool SetAside(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            var target = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(path)}");
            File.Copy(path, target, overwrite: true);
            Log.Info($"Unreadable {Path.GetFileName(path)} kept as {Path.GetFileName(target)}");
            return true;
        }
        catch (Exception ex) { Log.Error("SetAside " + Path.GetFileName(path), ex); return false; }
    }
}
