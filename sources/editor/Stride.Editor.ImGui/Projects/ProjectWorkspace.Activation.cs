using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectWorkspace
{
    internal sealed record Activation(Package? Package, SolutionProject? Project, string PackagePath,
        string BuildProjectPath, string Name, string Output, Exception? CompilationFailure);
    internal Activation CaptureActivation() => new(Package, Session.CurrentProject, PackagePath, BuildProjectPath, Name, CompiledOutput, CompilationFailure);
    internal void RestoreActivation(Activation state)
    {
        if (state.Package != null && !Session.Packages.Contains(state.Package)
            || state.Project != null && !Session.Projects.Contains(state.Project))
            throw new InvalidOperationException("The native active package/project is no longer part of this session.");
        Package = state.Package; Session.CurrentProject = state.Project; PackagePath = state.PackagePath;
        BuildProjectPath = state.BuildProjectPath; Name = state.Name; CompiledOutput = state.Output; CompilationFailure = state.CompilationFailure;
    }
    internal void ClearActivation()
    {
        Package = null; Session.CurrentProject = null!; PackagePath = BuildProjectPath = CompiledOutput = "";
        CompilationFailure = null; Name = Path.GetFileNameWithoutExtension(OpenPath);
    }
}
