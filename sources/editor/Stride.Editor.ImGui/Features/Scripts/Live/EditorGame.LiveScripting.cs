using Stride.Core.Diagnostics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private LiveScriptSession? liveScriptSession;
    private ProjectWorkspace? liveScriptWorkspace;
    private DesktopProjectBuildResult? pendingLiveScriptStart;
    private Task? liveScriptOperation;
    private CancellationTokenSource? liveScriptCancellation;
    private string? liveScriptVersion;
    private bool liveAfterUserBuild;
    private bool IsLiveScripting => liveScriptSession != null;

    private void StartLiveScriptingBuild()
    {
        if (!CanStartUserProjectBuild || !CanRunUserProject || buildTarget?.Platform != Stride.Core.PlatformType.Windows) return;
        StopLiveScripting();
        StartUserProjectBuild(false);
        liveAfterUserBuild = true;
    }

    private void PumpLiveScripting()
    {
        if (liveScriptWorkspace != null && !ReferenceEquals(liveScriptWorkspace, workspace)) StopLiveScripting();
        PumpLiveExecutableChanges();
        if (liveScriptOperation?.IsCompleted == true)
        {
            var completed = liveScriptOperation; liveScriptOperation = null;
            liveScriptCancellation?.Dispose(); liveScriptCancellation = null;
            try { completed.GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                outputLog.Append("Live scripting", error is OperationCanceledException ? LogMessageType.Info : LogMessageType.Error,
                    "Live scripting", error.ToString());
                StopLiveScripting();
            }
        }
        if (liveScriptOperation != null) return;
        if (liveScriptSession?.HasExited == true) { StopLiveScripting(); return; }
        if (workspace == null || sessionHistory == null || IsUserProjectBuilding || projectLoading != null
            || IsAuthoringPipelineUpdating || IsAssetPreviewUpdating || requestedAssemblyOutputs.Count != 0 || assemblySnapshotTask != null
            || changedAssemblyPaths.Count != 0 || sessionHistory.Stack.TransactionInProgress || sessionHistory.Stack.RollInProgress
            || sessionHistory.IsCapturingOperations || sessionHistory.IsCompensating || closeRequested || closePromptOpen || exitApproved) return;
        if (pendingLiveScriptStart == null && liveScriptSession == null) return;
        try
        {
            if (pendingLiveScriptStart is { } launch) InstallLiveExecutable(launch);
            var owners = workspace.Session.Packages.Where(package => !package.IsReadOnly).SelectMany(package => package.LoadedAssemblies)
                .Where(owner => owner.ProjectReference?.Location != null && owner.Assembly != null).ToArray();
            var version = string.Join(";", owners.OrderBy(owner => owner.ProjectReference.Location.ToOSPath(), StringComparer.OrdinalIgnoreCase)
                .Select(owner => owner.ProjectReference.Location.ToOSPath() + ":" + owner.Assembly.ManifestModule.ModuleVersionId))
                + ";executable:" + liveExecutableAssembly?.Project + ":" + liveExecutableAssembly?.ModuleId;
            if (pendingLiveScriptStart == null && version == liveScriptVersion) return;
            var starting = pendingLiveScriptStart;
            var session = liveScriptSession ?? new LiveScriptSession();
            liveScriptSession = session; liveScriptWorkspace = workspace;
            if (starting != null && sourceReloadPromptOpen)
            {
                sourceReloadPromptOpen = false;
                acceptedSourceReloadRevision = sourceReloadRevision;
            }
            var modules = owners.Select(owner => session.CaptureVersion(owner.ProjectReference.Location.ToOSPath(), owner.Path,
                    owner.Assembly.ManifestModule.ModuleVersionId)).GroupBy(assembly => assembly.Project, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Select(assembly => assembly.ModuleId).Distinct().Count() == 1 ? group.First()
                    : throw new InvalidOperationException("The live project has conflicting loaded framework generations.")).ToList();
            if (liveExecutableAssembly is { } executable && !modules.Any(assembly => SamePath(assembly.Project, executable.Project)))
                modules.Add(executable);
            foreach (var assembly in owners.Select(owner => owner.Assembly).Distinct())
            {
                var entry = workspace.Session.AssemblyContainer.LoadedAssemblies.SingleOrDefault(entry => ReferenceEquals(entry.Assembly, assembly));
                if (entry?.AcquireArtifactLifetime() is { } lease) session.RetainArtifact(lease);
            }
            var probes = workspace.Session.AssemblyContainer.LoadedAssemblies.SelectMany(entry =>
                    ((IEnumerable<string>?)entry.Dependencies?.Values ?? Enumerable.Empty<string>()).Append(entry.Path))
                .Concat(modules.Select(module => module.Path)).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var logger = new LoggerResult();
            logger.MessageLogged += (_, args) => outputLog.Append("Live scripting", args.Message.Type, "Live scripting", args.Message.ToString());
            liveScriptCancellation = new(); var token = liveScriptCancellation.Token;
            liveScriptVersion = version; pendingLiveScriptStart = null;
            liveScriptOperation = Task.Run(async () =>
            {
                if (starting != null) await session.Start(Path.GetDirectoryName(starting.TargetPath)!, modules.ToArray(), probes, logger, token, starting.GraphicsApi);
                else await session.Update(modules.ToArray(), probes, token);
            }, token);
        }
        catch (Exception error)
        {
            outputLog.Append("Live scripting", LogMessageType.Error, "Live scripting", error.ToString());
            StopLiveScripting();
        }
    }

    private void StopLiveScripting()
    {
        ClearLiveExecutable();
        liveScriptSession?.Dispose(); liveScriptSession = null;
        DetachBuildTask(liveScriptOperation, liveScriptCancellation);
        liveScriptOperation = null; liveScriptCancellation = null;
        liveScriptWorkspace = null; pendingLiveScriptStart = null; liveScriptVersion = null; liveAfterUserBuild = false;
    }
}
