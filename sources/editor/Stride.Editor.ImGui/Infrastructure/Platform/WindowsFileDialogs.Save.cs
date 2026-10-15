namespace Stride.Editor.ImGui;

internal static partial class WindowsFileDialogs
{
    internal static string? Save(nint owner, string? title, string filter, string? directory, string extension, string? initialFileName = null)
        => ShowFileDialog(owner, title, filter, directory, initialFileName, false, true, extension).FirstOrDefault();
}
