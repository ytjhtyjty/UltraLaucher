using System.Text;

namespace UltraLauncher.Core.Common;

/// <summary>Запись файлов через временный файл, чтобы обрыв не оставлял полузаписанный результат.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content, string? backupPath = null)
        => WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content), backupPath);

    public static void WriteAllBytes(string path, byte[] content, string? backupPath = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(content);
            fs.Flush(flushToDisk: true);
        }

        if (File.Exists(path) && backupPath is not null)
        {
            File.Replace(temp, path, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path, overwrite: true);
        }
    }
}
