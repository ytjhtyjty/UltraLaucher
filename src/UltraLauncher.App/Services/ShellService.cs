using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace UltraLauncher.App.Services;

/// <summary>Операции оболочки Windows: проводник, браузер, диалоги выбора, буфер обмена.</summary>
public static class ShellService
{
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    }

    public static void RevealFile(string path)
    {
        if (!File.Exists(path)) { OpenFolder(Path.GetDirectoryName(path)!); return; }
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path }, UseShellExecute = false });
    }

    public static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static string? PickFolder(string? initial, string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)) dialog.InitialDirectory = initial;
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderName : null;
    }

    public static string? PickJava(string? initial)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите java.exe или javaw.exe",
            Filter = "Java (java.exe; javaw.exe)|java.exe;javaw.exe",
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(initial) && File.Exists(initial)) dialog.InitialDirectory = Path.GetDirectoryName(initial);
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }

    public static void CopyText(string text)
    {
        try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.ExternalException) { /* буфер занят другой программой */ }
    }
}
