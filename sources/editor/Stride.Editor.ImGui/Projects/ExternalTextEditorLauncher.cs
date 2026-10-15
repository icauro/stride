using System.Diagnostics;

namespace Stride.Editor.ImGui;

internal static class ExternalTextEditorLauncher
{
    public static void Open(string path, string editorPath, bool requireExistingFile = true)
    {
        if (requireExistingFile && !File.Exists(path)) throw new FileNotFoundException("You need to save the file before you can open it.", path);
        var executable = Environment.ExpandEnvironmentVariables(editorPath);
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("The path to the text editor in Settings is empty.");
        try
        {
            var start = new ProcessStartInfo(executable)
            { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path))! };
            start.ArgumentList.Add(Path.GetFullPath(path));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The text editor could not be started.");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new InvalidOperationException("There was a problem starting the text editor. Make sure the path to the text editor in Settings is correct.", error);
        }
    }
}
