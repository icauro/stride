using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Owns recovery cadence and decisions; restoration is explicitly delegated to canonical document publication.</summary>
internal sealed class EditorRecoveryPanel(IEditorRecoveryHost host, string? journalRoot = null, bool captureNativeButtons = false)
{
    private readonly Dictionary<Guid, RecoveryWorkspaceButtonReceipt> nativeWorkspaceButtons = [];
    internal RecoveryWorkspaceButtonReceipt? InspectWorkspaceButton(Guid session) => nativeWorkspaceButtons.GetValueOrDefault(session);
    internal RecoveryDocumentActionReceipt? LastDocumentAction { get; private set; }
    internal RecoveryWorkspaceActionReceipt? LastWorkspaceAction { get; private set; }
    private readonly EditorRecoveryStore store = new(host.ProjectPath, journalRoot);
    private IReadOnlyList<EditorRecoveryCandidate>? candidates;
    private Task? checkpoint;
    private DateTime nextCheckpoint = DateTime.MinValue;
    private bool openDialog;
    private readonly HashSet<(Guid Session, string Path)> restored = [];
    private readonly Dictionary<(Guid Session, string Path), string> failures = [];
    internal bool HasRecoverableCandidates => candidates?.Count > 0;
    public void Tick()
    {
        if (candidates == null)
        {
            try { candidates = store.Discover(); openDialog = candidates.Count > 0; }
            catch (Exception error) { candidates = []; host.ReportFailure(error); }
        }
        if (checkpoint is { IsCompleted: true })
        {
            if (checkpoint.Exception is { } error) host.ReportFailure(error.GetBaseException());
            checkpoint = null;
        }
        if (DateTime.UtcNow < nextCheckpoint || checkpoint != null) return;
        Checkpoint();
    }
    public void Checkpoint()
    {
        if (checkpoint != null) return;
        nextCheckpoint = DateTime.UtcNow.AddSeconds(30);
        try
        {
            var records = host.CaptureDocuments(); var assemblies = host.LoadedAssemblies(); var presentation = host.CaptureWorkspace();
            checkpoint = Task.Run(() => store.CheckpointAsync(records, assemblies, presentation));
        }
        catch (Exception error) { host.ReportFailure(error); }
    }
    public void Draw()
    {
        if (captureNativeButtons) nativeWorkspaceButtons.Clear();
        if (openDialog) { Gui.OpenPopup("Recover changes"); openDialog = false; }
        var scale = Math.Max(1f, Gui.GetFontSize() / 16f);
        var available = Gui.GetMainViewport().WorkSize;
        var maximum = new NVector2(Math.Max(1, available.X - 32 * scale), Math.Max(1, available.Y - 32 * scale));
        Gui.SetNextWindowSize(new NVector2(Math.Min(640 * scale, maximum.X), Math.Min(480 * scale, maximum.Y)), ImGuiCond.FirstUseEver);
        Gui.SetNextWindowSizeConstraints(new NVector2(Math.Min(320 * scale, maximum.X), Math.Min(160 * scale, maximum.Y)), maximum);
        using var modal = ImGuiScopes.Modal("Recover changes", ImGuiWindowFlags.None);
        if (!modal.DrawContents) return;
        var bodyHeight = Math.Max(1f, Gui.GetContentRegionAvail().Y - Gui.GetFrameHeightWithSpacing());
        var visible = Gui.BeginChild("##RecoveryBody", new NVector2(0, bodyHeight), ImGuiChildFlags.None);
        try
        {
            if (visible)
            {
                foreach (var candidate in candidates ?? [])
                {
                    Gui.PushID(candidate.Manifest.SessionId.ToString());
                    Gui.TextUnformatted(candidate.Manifest.CapturedUtc.ToLocalTime().ToString("g"));
                    if (candidate.Manifest.Workspace is { } presentation)
                    {
                        var dependencyFailure = host.WorkspaceDependencyFailure(presentation);
                        Gui.BeginDisabled(dependencyFailure != null);
                        if (Gui.Button("Restore##Workspace"))
                        {
                            var frame = Gui.GetFrameCount(); var itemId = Gui.GetItemID();
                            Exception? failure = null; var succeeded = false;
                            try { host.RestoreWorkspace(presentation); succeeded = true; }
                            catch (Exception error) { failure = error; host.ReportFailure(error); }
                            finally
                            {
                                if (captureNativeButtons) LastWorkspaceAction = new(candidate.Manifest.SessionId, itemId,
                                    frame, true, succeeded, failure?.GetType().FullName, failure?.Message);
                            }
                        }
                        if (captureNativeButtons)
                        {
                            var item = Gui.GetCurrentContext().LastItemData;
                            var clip = Gui.GetCurrentContext().CurrentWindow.ClipRect;
                            var itemMinimum = Gui.GetItemRectMin(); var itemMaximum = Gui.GetItemRectMax();
                            var viewport = Gui.GetWindowViewport();
                            nativeWorkspaceButtons[candidate.Manifest.SessionId] = new(candidate.Manifest.SessionId, Gui.GetItemID(),
                                (item.ItemFlags & (ImGuiItemFlags)ImGuiItemFlagsPrivate.Disabled) != 0,
                                Math.Max(itemMinimum.X, clip.Min.X), Math.Max(itemMinimum.Y, clip.Min.Y),
                                Math.Min(itemMaximum.X, clip.Max.X), Math.Min(itemMaximum.Y, clip.Max.Y),
                                Gui.IsItemVisible(), viewport.ID, viewport.Pos.X, viewport.Pos.Y, viewport.Size.X, viewport.Size.Y, Gui.GetFrameCount());
                        }
                        host.RecordControl("recovery/workspace/" + candidate.Manifest.SessionId);
                        Gui.EndDisabled();
                        if (dependencyFailure != null) host.DrawSelectableText("##WorkspaceFailure", dependencyFailure);
                    }
                    foreach (var entry in candidate.Manifest.Documents)
                    {
                        Gui.PushID(entry.Path); var key = (candidate.Manifest.SessionId, entry.Path);
                        host.DrawSelectableText("##Path", entry.Path);
                        if (!EditorRecoveryStore.DiskMatches(entry)) Gui.TextUnformatted("Changed on disk");
                        if (restored.Contains(key)) Gui.TextUnformatted("Restored");
                        else if (Gui.Button("Restore"))
                        {
                            var hostRestoreInvoked = false;
                            try
                            {
                                var record = store.Read(candidate, entry);
                                hostRestoreInvoked = true;
                                host.Restore(record); restored.Add(key); failures.Remove(key);
                                LastDocumentAction = new(candidate.Manifest.SessionId, entry.PayloadHash, entry.Path, Gui.GetFrameCount(), true, null, null);
                            }
                            catch (Exception error)
                            {
                                LastDocumentAction = new(candidate.Manifest.SessionId, entry.PayloadHash, entry.Path, Gui.GetFrameCount(), hostRestoreInvoked, error.GetType().FullName, error.Message);
                                failures[key] = error.Message; host.ReportFailure(error);
                            }
                        }
                        host.RecordControl("recovery/document/" + candidate.Manifest.SessionId + "/" + entry.PayloadHash);
                        if (failures.TryGetValue(key, out var failure)) host.DrawSelectableText("##Failure", failure);
                        Gui.PopID();
                    }
                    Gui.PopID();
                }
            }
        }
        finally { Gui.EndChild(); }
        host.RecordControl("recovery/body");
        if (Gui.Button(EditorLocalization.Widget("Close", "Button"))) Gui.CloseCurrentPopup();
        host.RecordControl("recovery/close");
    }
    public void RequestDialog() => openDialog = true;
    internal bool TryReadNewAssetDependency(EditorRecoveryOpenDocument descriptor, out EditorRecoveryRecord? record)
    {
        record = null;
        var assetId = descriptor.GeneratedByAssetId ?? descriptor.AssetId;
        if (assetId == null || descriptor.PackagePath == null) return false;
        var matches = (candidates ?? []).Where(candidate => candidate.Manifest is { Version: 1, Completed: false }
            && string.Equals(candidate.Manifest.ProjectPath, host.ProjectPath, StringComparison.OrdinalIgnoreCase)
            && candidate.Manifest.Workspace?.Documents.Contains(descriptor) == true)
            .SelectMany(candidate => candidate.Manifest.Documents.Where(entry => entry.AssetId == assetId
                && entry.BaselineHash.Length == 0 && entry.PackagePath != null
                && string.Equals(entry.PackagePath, descriptor.PackagePath, StringComparison.OrdinalIgnoreCase))
                .Select(entry => (Candidate: candidate, Entry: entry))).ToArray();
        if (matches.Length == 0) return false;
        if (matches.Length != 1) throw new InvalidDataException("The recovery dependency has ambiguous journal ownership.");
        record = store.Read(matches[0].Candidate, matches[0].Entry);
        return true;
    }
    public Task FlushTransitionAsync()
    {
        var records = host.CaptureDocuments(); var assemblies = host.LoadedAssemblies(); var presentation = host.CaptureWorkspace();
        return Flush(records, assemblies);
        async Task Flush(IReadOnlyList<EditorRecoveryRecord> snapshots, string[] modules)
        {
            if (checkpoint != null) await checkpoint.ConfigureAwait(false);
            await store.CheckpointAsync(snapshots, modules, presentation).ConfigureAwait(false);
        }
    }
    public Task CompleteCheckpointAsync() => store.CompleteAsync();
    public async Task CompleteAsync(bool discardPendingChanges)
    {
        // Capture on the caller's editor thread before yielding; serializer/Quantum objects never cross into the writer task.
        var records = host.CaptureDocuments(); var assemblies = host.LoadedAssemblies(); var presentation = host.CaptureWorkspace();
        var complete = discardPendingChanges || !host.HasPendingChanges;
        if (checkpoint != null) await checkpoint.ConfigureAwait(false);
        await store.CheckpointAsync(records, assemblies, presentation).ConfigureAwait(false);
        if (complete) await store.CompleteAsync().ConfigureAwait(false);
    }
}

internal sealed record RecoveryDocumentActionReceipt(Guid Session, string PayloadHash, string Path, int Frame,
    bool HostRestoreInvoked, string? ErrorType, string? Error);
internal sealed record RecoveryWorkspaceActionReceipt(Guid Session, uint ItemId, int Frame,
    bool HostRestoreInvoked, bool HostRestoreReturned, string? ErrorType, string? Error);

internal sealed record RecoveryWorkspaceButtonReceipt(Guid Session, uint ItemId, bool Disabled,
    float MinimumX, float MinimumY, float MaximumX, float MaximumY, bool Visible,
    uint ViewportId, float ViewportX, float ViewportY, float ViewportWidth, float ViewportHeight, int Frame);
