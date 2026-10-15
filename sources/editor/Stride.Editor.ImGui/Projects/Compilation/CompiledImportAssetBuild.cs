namespace Stride.Editor.ImGui;

/// <summary>Import refresh uses the same native compiler and immutable authored inputs as previews.</summary>
internal static class CompiledImportAssetBuild
{
    public static string Build(MediaCompilationSnapshot snapshot, CancellationToken token, Action<ProjectLoadState> progress)
        => CompiledMediaBuild.Build(snapshot, token, progress);
    public static void ReleaseOutput(string output) => CompiledMediaBuild.ReleaseOutput(output);
}
