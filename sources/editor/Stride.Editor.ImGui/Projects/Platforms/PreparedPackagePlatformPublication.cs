namespace Stride.Editor.ImGui;

internal sealed class PreparedPackagePlatformPublication : IDisposable
{
    internal readonly PreparedPackagePlatformUpdate Files;
    internal readonly PreparedSolutionProjects Membership;
    internal readonly string TargetPath, Output;
    private bool membershipReleased, filesReleased;
    private PreparedPackagePlatformPublication(PreparedPackagePlatformUpdate files, PreparedSolutionProjects membership, string targetPath, string output)
    { Files = files; Membership = membership; TargetPath = targetPath; Output = output; }
    internal static PreparedPackagePlatformPublication Prepare(PackagePlatformUpdateSnapshot snapshot, string targetPath,
        bool compile, CancellationToken token, Action<ProjectLoadState>? progress)
    {
        PreparedPackagePlatformUpdate? files = null; PreparedSolutionProjects? membership = null;
        try
        {
            token.ThrowIfCancellationRequested(); files = PreparedPackagePlatformUpdate.Prepare(snapshot, token);
            membership = PreparedSolutionProjects.PreparePlatformProjects(files, token);
            token.ThrowIfCancellationRequested();
            var output = compile ? CompiledProjectBuild.Build(System.IO.Path.Combine(files.Mirror.StageRoot,
                System.IO.Path.GetRelativePath(files.Mirror.OriginalRoot, targetPath)), token, progress) : "";
            token.ThrowIfCancellationRequested();
            return new(files, membership, targetPath, output);
        }
        catch
        {
            var failed = new FailedResources(files, membership);
            EditorBackgroundJobRetirement.Retire(Task.FromResult(failed), null, value => value.Dispose(), "Platform update publication preparation");
            throw;
        }
    }
    private sealed class FailedResources(PreparedPackagePlatformUpdate? files, PreparedSolutionProjects? membership)
    {
        private bool membershipReleased, filesReleased;
        internal void Dispose()
        {
            if (!membershipReleased) { membership?.Dispose(); membershipReleased = true; }
            if (!filesReleased) { files?.Dispose(); filesReleased = true; }
        }
    }
    public void Dispose()
    {
        if (!membershipReleased) { if (!Membership.IsRetained) Membership.Dispose(); membershipReleased = true; }
        if (!filesReleased) { Files.Dispose(); filesReleased = true; }
    }
}
