namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal Task<string>? VerificationAssetCompilationTask => assetPreviewBuild;
    internal float VerificationRuntimeAssetRadius(string url) => InspectionViewport?.RuntimeModelHalfExtent(url)
        ?? throw new InvalidOperationException("The real scene runtime model is unavailable.");
    internal string CreateAssetCompilationFailureVerification()
    {
        var directory = Path.GetDirectoryName(workspace?.BuildProjectPath ?? throw new InvalidOperationException("No actual build project."))!;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Compiler failure injection requires the disposable UI fixture copy.");
        var path = Path.Combine(directory, "CapabilityCompileFailure_" + Guid.NewGuid().ToString("N") + ".cs");
        using var file = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        file.WriteLine("#error Intentional E04 disposable compilation failure");
        return path;
    }
}
