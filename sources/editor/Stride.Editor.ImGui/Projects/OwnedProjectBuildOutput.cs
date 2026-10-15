namespace Stride.Editor.ImGui;

/// <summary>Releases only a compiler run inside the editor-owned output cache.</summary>
internal static partial class OwnedProjectBuildOutput
{
    internal static void Release(string output)
    {
        var actual = ValidateRun(output);
        if (!NativeCompilerProcessRetirement.TryDrainForOutput(actual))
            throw new IOException("The native compiler still owns this output.");
        if (Directory.Exists(actual)) Directory.Delete(actual, true);
    }
    private static string ValidateRun(string output)
    {
        var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stride", "Editor.ImGui", "build"));
        var actual = Path.GetFullPath(output);
        if (!actual.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(actual), "N", out _)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(actual)), "runs", StringComparison.OrdinalIgnoreCase))
            throw new IOException("The unadopted compiler output is outside its owned cache directory.");
        return actual;
    }
}
