namespace Stride.Editor.ImGui;

internal static partial class WindowsFileDialogs
{
    public static string? Open(nint owner, string? title, string filter, string? directory, string? initialPath = null)
        => ShowFileDialog(owner, title, filter, directory, initialPath, false, false, null).FirstOrDefault();

    public static string[] OpenMany(nint owner, string? title, string filter, string? directory)
        => ShowFileDialog(owner, title, filter, directory, null, true, false, null);
}
