using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed class ProjectFolderReadFailureParityRun
{
    internal required ActiveProjectParityRun Owners;
    internal required string OriginalPath, AlternatePath, MissingPath;
    internal required string EvidencePath;
    internal FileStream? Lock;
    internal EditorOutputEntry[] Output = [];
    internal long OutputVersion = -1, FirstObservedAt;
    internal int Phase, FirstFrame;
    internal bool PathOwned, Retired;
    internal bool OriginalPanelVisible, PresentationRequested;
    internal long InitialRefreshCalls;
    internal readonly List<object> Checks = [];
}

internal sealed partial class EditorGame
{
    private ProjectFolderReadFailureParityRun? projectFolderReadFailureParity;

    internal ProjectFolderReadFailureParityRun BeginProjectFolderReadFailureParityVerification(string artifacts)
    {
        if (projectFolderReadFailureParity != null)
            throw new InvalidOperationException("The prior private project-file case still owns cleanup.");
        var fixture = activeProjectParityFixture ?? throw new InvalidOperationException("The native project fixture is unavailable.");
        var alternates = Directory.GetFiles(Path.GetDirectoryName(fixture.SecondProject)!, "StaleTarget-*.csproj");
        if (alternates.Length != 1 || !File.ReadAllBytes(alternates[0]).AsSpan().SequenceEqual(File.ReadAllBytes(fixture.SecondProject)))
            throw new InvalidDataException("The real stale-path alternate SDK fixture is not uniquely certified.");
        var owners = BeginActiveProjectParityVerification();
        owners.AuthoredFiles.Add(alternates[0], File.ReadAllBytes(alternates[0]));
        owners.StateBaselines = owners.StateBaselines.Where(state => state.ProtectedOwner || state.Dirty || state.ViewReferences != 0).ToArray();
        return projectFolderReadFailureParity = new()
        {
            Owners = owners, OriginalPath = owners.Second.VSProject.FullPath, AlternatePath = alternates[0],
            EvidencePath = Path.Combine(Path.GetFullPath(artifacts), "project-folder-read-latest.json"),
            OriginalPanelVisible = SolutionExplorerController.Visible, InitialRefreshCalls = InspectProjectFolderRefresh().Calls,
            MissingPath = Path.Combine(Path.GetDirectoryName(alternates[0])!, "Missing-" + Guid.NewGuid().ToString("N") + ".csproj")
        };
    }

    internal object? StepProjectFolderReadFailureParityVerification(ProjectFolderReadFailureParityRun run)
    {
        File.WriteAllText(run.EvidencePath, System.Text.Json.JsonSerializer.Serialize(CaptureProjectFolderReadFailureParity(run)));
        AssertActiveProjectParityOwners(run.Owners);
        if (outputLog.Read(ref run.OutputVersion, out var output)) run.Output = output;
        var package = run.Owners.Second.Package;
        var path = run.Phase is 1 ? run.MissingPath : run.AlternatePath;
        var receipt = InspectProjectFolderRead(package, path);
        var errors = run.Output.Where(entry => entry.Channel == "Assets" && entry.Module == path
            && entry.Level == Stride.Core.Diagnostics.LogMessageType.Error).ToArray();
        switch (run.Phase)
        {
            case 0:
                if (!run.PresentationRequested)
                {
                    var root = assetBrowser.Packages.Single(folder => ReferenceEquals(folder.Package, package));
                    // Existing production reveal action requests only ImGui panel
                    // presentation. It does not synthesize a refresh or OS focus.
                    SolutionExplorerController.RevealFolder(root);
                    run.PresentationRequested = true;
                    return null;
                }
                var refresh = InspectProjectFolderRefresh();
                if (refresh.Calls <= run.InitialRefreshCalls || refresh.LastFrame < Gui.GetFrameCount() - 1) return null;
                if (File.Exists(run.MissingPath)) throw new InvalidOperationException("The private missing-path fixture unexpectedly exists.");
                run.Owners.Second.VSProject.FullPath = run.MissingPath; run.PathOwned = true;
                run.Phase++; return null;
            case 1:
            case 4:
                if (!receipt.Failed) return null;
                if (errors.Length != 1 || receipt.Error == null || errors[0].Exception != receipt.Error
                    || !assetBrowser.Packages.Any(root => ReferenceEquals(root.Package, package)))
                    throw new InvalidOperationException("The actual project-file failure did not preserve native package roots and its exact single Output diagnostic.");
                if (run.FirstObservedAt == 0)
                { run.FirstObservedAt = Environment.TickCount64; run.FirstFrame = Gui.GetFrameCount(); return null; }
                if (Environment.TickCount64 - run.FirstObservedAt < 1250 || receipt.Attempts < 2) return null;
                if (Gui.GetFrameCount() <= run.FirstFrame + 1)
                    throw new InvalidOperationException("Normal editor drawing did not advance during project-file refusal.");
                run.Checks.Add(new { contract = run.Phase == 1 ? "actual-missing-project-refused" : "actual-exclusive-project-read-refused",
                    path, attempts = receipt.Attempts, outputSequence = errors[0].Sequence, error = errors[0].Exception,
                    retainedPackageRoot = true, frameBefore = run.FirstFrame, frameAfter = Gui.GetFrameCount(), retryAt = receipt.RetryAt });
                run.Lock?.Dispose(); run.Lock = null;
                run.Owners.Second.VSProject.FullPath = run.AlternatePath;
                run.FirstObservedAt = 0; run.Phase++; return null;
            case 2:
            case 5:
                if (receipt.Failed || !receipt.Published) return null;
                var actual = sessionHistory!.Registry.Find(run.AlternatePath);
                if (actual?.Document is not ProjectBuildSettingsDocument settings || !actual.IsPublished || actual.IsDisposed
                    || settings.AuthoringRevision != receipt.Revision)
                    throw new InvalidOperationException("Project-file recovery did not publish the genuine native authoring document and folders.");
                run.Checks.Add(new { contract = "actual-project-file-recovered", path = run.AlternatePath,
                    actual.Generation, receipt.Revision, packageRoot = assetBrowser.Packages.Any(root => ReferenceEquals(root.Package, package)) });
                run.Phase++; return null;
            case 3:
                // Retire the unreferenced alternate settings cache before locking
                // its actual file; otherwise acquisition legitimately reuses it.
                run.Owners.Second.VSProject.FullPath = run.OriginalPath;
                var current = sessionHistory!.Registry.Find(run.AlternatePath);
                if (current != null) return null;
                run.Lock = new FileStream(run.AlternatePath, FileMode.Open, FileAccess.Read, FileShare.None);
                run.Owners.Second.VSProject.FullPath = run.AlternatePath;
                run.Phase++; return null;
            case 6:
                RestoreProjectFolderReadFailurePath(run);
                if (!RetireProjectFolderReadFailureParityVerification(run)) return null;
                AssertActiveProjectParityOwners(run.Owners);
                return new { passed = true, checks = run.Checks.ToArray(), ownerReceipt = run.Owners.LastOwnerReceipt,
                    scope = "Actual normal folder refresh missing/exclusive-read refusal, raw deduplicated Output, package roots, frame progress, throttled retry and genuine native document recovery; no synthetic filesystem errors or diagnostic refresh invocation." };
            default: throw new InvalidOperationException("Unknown private project-file verification phase.");
        }
    }

    private static void RestoreProjectFolderReadFailurePath(ProjectFolderReadFailureParityRun run)
    {
        run.Lock?.Dispose(); run.Lock = null;
        if (!run.PathOwned) return;
        var current = run.Owners.Second.VSProject.FullPath;
        if (!SamePath(current, run.MissingPath) && !SamePath(current, run.AlternatePath) && !SamePath(current, run.OriginalPath))
            throw new InvalidOperationException("Another owner changed the private project-file test path.");
        run.Owners.Second.VSProject.FullPath = run.OriginalPath; run.PathOwned = false;
    }

    internal bool RetireProjectFolderReadFailureParityVerification(ProjectFolderReadFailureParityRun run)
    {
        if (run.Retired) return true;
        RestoreProjectFolderReadFailurePath(run);
        if (!RetireActiveProjectParityVerification(run.Owners)) return false;
        SolutionExplorerController.Visible = run.OriginalPanelVisible;
        run.Retired = true;
        if (ReferenceEquals(projectFolderReadFailureParity, run)) projectFolderReadFailureParity = null;
        return true;
    }

    internal bool TryDrainProjectFolderReadFailureParityVerification()
        => projectFolderReadFailureParity == null || RetireProjectFolderReadFailureParityVerification(projectFolderReadFailureParity);

    internal object? CaptureProjectFolderReadFailureParity(ProjectFolderReadFailureParityRun? run)
    {
        if (run == null) return null;
        var path = run.Owners.Second.VSProject.FullPath;
        var receipt = InspectProjectFolderRead(run.Owners.Second.Package, path);
        var registered = sessionHistory?.Registry.Find(run.AlternatePath);
        var currentRegistered = sessionHistory?.Registry.Find(path);
        var refresh = InspectProjectFolderRefresh();
        var window = Hexa.NET.ImGui.ImGuiP.FindWindowByName(EditorLocalization.Widget("Solution explorer", "View"));
        long version = -1;
        outputLog.Read(ref version, out var output);
        return new
        {
            phase = run.Phase, nativePath = path, packageContainerPath = (run.Owners.Second.Package.Container as Stride.Core.Assets.SolutionProject)?.FullPath.ToOSPath(),
            run.OriginalPath, run.AlternatePath, run.MissingPath, run.PathOwned, run.Retired,
            frame = Gui.GetFrameCount(), run.FirstFrame, run.FirstObservedAt, now = Environment.TickCount64,
            folderRefresh = new { refresh.Calls, refresh.LastFrame },
            panel = new { visible = SolutionExplorerController.Visible, focusRequested = SolutionExplorerController.FocusRequested,
                nativeWindowExists = !window.IsNull, nativeActive = !window.IsNull && window.Active },
            run.PresentationRequested,
            receipt = new { receipt.Failed, receipt.Attempts, receipt.RetryAt, receipt.Error, receipt.Published, receipt.Revision },
            packageRootPublished = assetBrowser.Packages.Any(root => ReferenceEquals(root.Package, run.Owners.Second.Package)),
            alternateRegistered = registered == null ? null : new { registered.Generation, registered.CanonicalPath,
                type = registered.Document.GetType().FullName, registered.ViewReferences, registered.HistoryReferences,
                registered.IsPublished, registered.IsDisposed, dirty = registered.Document.IsDirty },
            currentRegistered = currentRegistered == null ? null : new { currentRegistered.Generation, currentRegistered.CanonicalPath,
                type = currentRegistered.Document.GetType().FullName, currentRegistered.ViewReferences, currentRegistered.HistoryReferences,
                currentRegistered.IsPublished, currentRegistered.IsDisposed, dirty = currentRegistered.Document.IsDirty },
            errors = output.Where(entry => entry.Channel == "Assets" && (entry.Module == run.MissingPath || entry.Module == run.AlternatePath))
                .Select(entry => new { entry.Sequence, entry.Module, entry.MessageText, entry.ExceptionType }).ToArray(),
            lockHeld = run.Lock != null, checks = run.Checks.ToArray()
        };
    }
}
