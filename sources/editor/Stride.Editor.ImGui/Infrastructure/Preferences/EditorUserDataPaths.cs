namespace Stride.Editor.ImGui;

internal static class EditorUserDataPaths
{
    internal static readonly string? OverrideRoot = ReadOverride();
    internal static string SharedRoot => OverrideRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "stride");
    private static string? ReadOverride()
    {
        var root = Environment.GetEnvironmentVariable("STRIDE_EDITOR_USER_DATA_PATH");
        if (string.IsNullOrWhiteSpace(root)) return null;
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("STRIDE_EDITOR_USER_DATA_PATH must be an absolute directory path.");
        return Path.GetFullPath(root);
    }
}
