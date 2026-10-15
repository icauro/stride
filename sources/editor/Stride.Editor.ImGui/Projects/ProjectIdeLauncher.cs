using System.Diagnostics;
using Stride.Core.CodeEditorSupport;

namespace Stride.Editor.ImGui;

internal static class ProjectIdeLauncher
{
    private static readonly Lazy<IDEInfo[]> Installed = new(() =>
        [IDEInfo.DefaultIDE, .. IDEInfoVersions.AvailableIDEs().Where(ide => ide.HasProgram)]);
    public static IReadOnlyList<IDEInfo> Available => Installed.Value;
    public static Task OpenAsync(ProjectWorkspace workspace, string preferredName, CancellationToken cancellation = default)
    {
        var solution = workspace.Session.SolutionPath?.ToOSPath();
        if (string.IsNullOrWhiteSpace(solution) || !File.Exists(solution))
            throw new InvalidOperationException("The session currently open is not a solution session.");
        // Only immutable paths leave the actor. COM calls can block while an IDE
        // is busy, so they run on their own STA rather than the rendering thread.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Open(solution, preferredName, cancellation); completion.SetResult(); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { completion.SetCanceled(cancellation); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "Stride editor IDE activation" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Open(string solution, string preferredName, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var ide = preferredName == IDEInfo.DefaultIDE.DisplayName ? IDEInfo.DefaultIDE
            : Available.FirstOrDefault(candidate => candidate.DisplayName == preferredName) ?? IDEInfo.DefaultIDE;
        if (ide.IDEType == IDEType.VisualStudio && WindowsVisualStudioInstance.TryActivate(solution, cancellation)) return;
        cancellation.ThrowIfCancellationRequested();
        ProcessStartInfo start;
        if (ide.ProgramPath is { Length: > 0 } program && File.Exists(program))
        {
            start = new(program) { WorkingDirectory = Path.GetDirectoryName(solution)!, CreateNoWindow = ide.IDEType == IDEType.VSCode };
            start.ArgumentList.Add(ide.IDEType == IDEType.VSCode ? Path.GetDirectoryName(solution)! : solution);
        }
        else start = new(solution) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(solution)! };
        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"An error occurred while starting {ide.IDEType}.");
    }
}
