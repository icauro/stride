using System.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed class ActiveProjectStalePathParityRun
{
    internal required ActiveProjectParityRun Owners;
    internal required string OriginalPath, ChangedPath;
    internal Task<string>? Request;
    internal Process? Compiler;
    internal CompilerRunIdentity? CompilerIdentity;
    internal DateTime CompilerStartedUtc;
    internal string? Output;
    internal int Phase;
    internal bool PathChanged, Retired;
    internal readonly List<object> Checks = [];
}

internal sealed partial class EditorGame
{
    private ActiveProjectStalePathParityRun? activeProjectStalePathParity;

    internal ActiveProjectStalePathParityRun BeginActiveProjectStalePathParityVerification()
    {
        if (activeProjectStalePathParity != null || !OperatingSystem.IsWindows())
            throw new InvalidOperationException("Stale native project-path verification requires its independent private Windows case.");
        var fixture = activeProjectParityFixture ?? throw new InvalidOperationException("The private native two-project fixture is unavailable.");
        var alternate = Path.Combine(Path.GetDirectoryName(fixture.SecondProject)!, "StaleTarget-" + Guid.NewGuid().ToString("N") + ".csproj");
        // The target remains a genuine readable SDK project throughout the
        // native path change; the real submitted compiler still owns the original.
        File.Copy(fixture.SecondProject, alternate, overwrite: false);
        var alternateBytes = File.ReadAllBytes(alternate);
        if (!alternateBytes.AsSpan().SequenceEqual(File.ReadAllBytes(fixture.SecondProject)))
            throw new InvalidDataException("The private alternate SDK project is not the original certified fixture copy.");
        var owners = BeginActiveProjectParityVerification();
        owners.AuthoredFiles.Add(alternate, alternateBytes);
        // Zero-reference clean caches may be collected by normal compiler input
        // preparation. Protect all actual editor/history/dirty/live borrowers.
        owners.StateBaselines = owners.StateBaselines.Where(state => state.ProtectedOwner || state.Dirty || state.ViewReferences != 0).ToArray();
        var original = owners.Second.VSProject.FullPath;
        return activeProjectStalePathParity = new()
        {
            Owners = owners, OriginalPath = original,
            ChangedPath = alternate
        };
    }

    internal object? StepActiveProjectStalePathParityVerification(ActiveProjectStalePathParityRun run)
    {
        switch (run.Phase)
        {
            case 0:
                AssertActiveProjectParityOwners(run.Owners);
                if (IsAuthoringPipelineUpdating || IsActiveProjectSwitching || IsUserProjectBuilding || assetPreviewBuild != null) return null;
                StartActiveProjectSwitch(run.Owners.Second);
                run.Request = activeProjectBuild ?? throw new InvalidOperationException("The real stale-path case submitted no native compiler.");
                run.Owners.Requests.Add(run.Request);
                run.Phase++; return null;
            case 1:
                if (!ReferenceEquals(activeProjectBuild, run.Request) || run.Request!.IsCompleted)
                    throw new InvalidOperationException("The real compiler completed before the native path-change boundary.");
                var state = Volatile.Read(ref activeProjectLoadState);
                if (state?.Phase != "Compiling assets" || state.ProcessId is not { } pid || state.CompilerRun == null) return null;
                if (activeProjectTarget == null || !ReferenceEquals(activeProjectTarget.Workspace, run.Owners.Workspace)
                    || !ReferenceEquals(activeProjectTarget.Project, run.Owners.Second)
                    || !SamePath(activeProjectTarget.Path, run.OriginalPath)
                    || state.CompilerRun.Source != "assets:" + run.OriginalPath)
                    throw new InvalidOperationException("The observed compiler does not own the original certified native target path.");
                var process = Process.GetProcessById(pid);
                if (process.HasExited) { process.Dispose(); return null; }
                _ = process.SafeHandle;
                run.Compiler = process; run.CompilerStartedUtc = process.StartTime.ToUniversalTime();
                run.CompilerIdentity = state.CompilerRun;
                run.Owners.Second.VSProject.FullPath = run.ChangedPath;
                run.PathChanged = true;
                if (!SamePath(run.Owners.Second.FullPath.ToOSPath(), run.ChangedPath)
                    || !SamePath(activeProjectTarget.Path, run.OriginalPath))
                    throw new InvalidOperationException("The native target path did not diverge from its immutable real compiler request.");
                run.Checks.Add(new { contract = "actual-native-target-path-changed", original = run.OriginalPath,
                    changed = run.ChangedPath, immutableRequest = activeProjectTarget.Path, compiler = run.CompilerIdentity,
                    process = pid, processStartedUtc = run.CompilerStartedUtc });
                run.Phase++; return null;
            case 2:
                if (!run.Request!.IsCompleted || IsActiveProjectSwitching) return null;
                if (!run.Request.IsCompletedSuccessfully)
                    throw new InvalidOperationException("Stale-output rejection requires a genuinely successful compiler, not cancellation or compilation failure.", run.Request.Exception);
                run.Output ??= run.Request.GetAwaiter().GetResult();
                if (run.Compiler?.HasExited != true || !NativeCompilerProcessRetirement.TryDrainForOutput(run.Output)) return null;
                if (activeProjectTarget != null || activeProjectCancellation != null
                    || !ReferenceEquals(workspace!.Session.CurrentProject, run.Owners.First)
                    || !ReferenceEquals(projectContent, run.Owners.BaselineContent)
                    || !SamePath(workspace.CompiledOutput, run.Owners.BaselineContent.OutputPath))
                    throw new InvalidOperationException("A successful stale native compiler output was adopted by another active-project owner.");
                if (Directory.Exists(run.Output)) return null;
                if (!SamePath(run.Owners.Second.VSProject.FullPath, run.ChangedPath))
                    throw new InvalidOperationException("The stale native target was restored before production terminal certification.");
                run.Checks.Add(new { contract = "successful-stale-output-rejected", task = run.Request.Status.ToString(),
                    output = run.Output, outputDeleted = true, childExited = true, noAdoption = true,
                    original = run.OriginalPath, changed = run.ChangedPath });
                RestoreActiveProjectStalePath(run);
                AssertActiveProjectParityOwners(run.Owners);
                run.Phase++; return null;
            case 3:
                if (!RetireActiveProjectStalePathParityVerification(run)) return null;
                AssertActiveProjectParityOwners(run.Owners);
                return new { passed = true, checks = run.Checks.ToArray(), ownerReceipt = run.Owners.LastOwnerReceipt,
                    targetPathRestored = true,
                    scope = "Real successful native compiler rejected after native target path divergence; exact process/output retirement and unchanged persistent owners, journal, scene, camera and files. No synthetic compiler task or production guard changes." };
            default: throw new InvalidOperationException("Unknown stale native project-path phase.");
        }
    }

    private static void RestoreActiveProjectStalePath(ActiveProjectStalePathParityRun run)
    {
        if (!run.PathChanged) return;
        if (!SamePath(run.Owners.Second.VSProject.FullPath, run.ChangedPath))
            throw new InvalidOperationException("Another owner changed the private native target path before restoration.");
        run.Owners.Second.VSProject.FullPath = run.OriginalPath;
        run.PathChanged = false;
    }

    internal bool RetireActiveProjectStalePathParityVerification(ActiveProjectStalePathParityRun run)
    {
        if (run.Retired) return true;
        RestoreActiveProjectStalePath(run);
        if (!RetireActiveProjectParityVerification(run.Owners)) return false;
        if (run.Request?.IsFaulted == true) _ = run.Request.Exception;
        if (run.Request?.IsCompletedSuccessfully == true) run.Output ??= run.Request.Result;
        if (run.Compiler != null && !run.Compiler.HasExited) return false;
        if (run.Output != null && (!NativeCompilerProcessRetirement.TryDrainForOutput(run.Output) || Directory.Exists(run.Output))) return false;
        run.Compiler?.Dispose(); run.Compiler = null;
        run.Retired = true;
        if (ReferenceEquals(activeProjectStalePathParity, run)) activeProjectStalePathParity = null;
        return true;
    }

    internal bool TryDrainActiveProjectStalePathParityVerification()
        => activeProjectStalePathParity == null || RetireActiveProjectStalePathParityVerification(activeProjectStalePathParity);
}
