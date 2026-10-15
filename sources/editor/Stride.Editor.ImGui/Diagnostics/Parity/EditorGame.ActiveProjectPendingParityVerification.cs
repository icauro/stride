using System.Diagnostics;
using System.Reflection;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed class ActiveProjectPendingParityRun
{
    internal required ActiveProjectParityRun Owners;
    internal required ComponentField Field;
    internal required EntityHierarchyDocument Document;
    internal required ulong Before, After;
    internal required Guid[] AppliedPrefix;
    internal required int Position;
    internal object? BeforeEditJournal;
    internal Guid? Edit;
    internal Task<string>? CancelledTask;
    internal Process? Compiler;
    internal CompilerRunIdentity? CompilerRun;
    internal CancellationToken CompilerCancellation;
    internal string? CancelledOutput;
    internal bool Cancelled, CancellationCertified, EditUndone, Retired;
    internal readonly List<object> Checks = [];
    internal string Control => "component/" + Field.Component.Id + "/Counter64";
    internal CompiledProjectContent? ReturnedContent => Owners.ReturnedContent;
}

internal sealed partial class EditorGame
{
    private ActiveProjectPendingParityRun? activeProjectPendingParity;
    internal bool CanBeginActiveProjectPendingParity => workspace != null && document != null && !document.IsDirty
        && !IsAuthoringPipelineUpdating && !IsActiveProjectSwitching && !IsUserProjectBuilding && assetPreviewBuild == null
        && !RecoveryTransitionBusy && InspectionViewport?.RuntimeScene?.RootScene != null;

    internal ActiveProjectPendingParityRun BeginActiveProjectPendingParityVerification()
    {
        if (activeProjectPendingParity != null || !CanBeginActiveProjectPendingParity)
            throw new InvalidOperationException("The native pending-switch case requires its idle, clean private scene.");
        var owner = document!;
        var sphere = owner.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity;
        SelectCompositeHierarchyEntity(owner, sphere, false);
        var script = sphere.Components.Single(component => component.GetType().Name == "InspectorTypes");
        var field = owner.GetComponentFields(script).Single(candidate => candidate.Name == "Counter64");
        if (field.Type != typeof(ulong) || !owner.CanEditComponentValue(field))
            throw new InvalidOperationException("The actual native Counter64 field is not editable.");
        var before = (ulong)field.Node.Retrieve()!;
        var owners = BeginActiveProjectParityVerification();
        return activeProjectPendingParity = new() { Owners = owners, Document = owner, Field = field,
            Before = before, After = before == ulong.MaxValue ? before - 1 : before + 1,
            BeforeEditJournal = CaptureActiveProjectParityHistory(owners.History),
            Position = owners.History.History.Position, AppliedPrefix = owners.History.History.Entries
                .Take(owners.History.History.Position).Select(entry => entry.Id).ToArray() };
    }

    internal object InspectActiveProjectPendingDraft(ActiveProjectPendingParityRun run)
    {
        var draft = CaptureActiveProjectPendingDraft(run);
        return new { draft.Control, draft.Text, draft.Edited, draft.OwnedLease, draft.Pending, draft.Error,
            authored = run.Field.Node.Retrieve(), runtime = ReadRuntimeComponentValueVerification(run.Field),
            compilerSubmitted = activeProjectBuild != null, phase = activeProjectLoadState?.Phase };
    }

    private (uint? Control, string? Text, bool Edited, bool OwnedLease, bool Pending, string? Error)
        CaptureActiveProjectPendingDraft(ActiveProjectPendingParityRun run)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var table = typeof(PropertyStringEditor).GetField("drafts", flags)!.GetValue(PropertyGrid.Strings)!;
        object?[] arguments = [run.Field.Node, null];
        if (!(bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, arguments)!
            || arguments[1] is not System.Collections.IDictionary values || values.Count != 1)
            return (null, null, false, false, false, null);
        var control = values.Keys.Cast<uint>().Single(); var draft = values[control]!;
        object? Read(string name) => draft.GetType().GetField(name, flags)!.GetValue(draft);
        return (control, (string?)Read("Text"), (bool)Read("Edited")!,
            Read("Lease") is IPropertyDraftOwnerLease lease && lease.ContainsOwner(run.Document),
            PropertyGrid.Strings.IsPending(run.Field.Node, control), (string?)Read("LastValidationError"));
    }

    internal bool ActiveProjectPendingDraftReady(ActiveProjectPendingParityRun run, string text)
    {
        var draft = CaptureActiveProjectPendingDraft(run);
        return draft.Pending && draft.Edited && draft.OwnedLease && draft.Text == text;
    }

    internal void VerifyInvalidActiveProjectPendingSwitch(ActiveProjectPendingParityRun run)
    {
        if (!ActiveProjectPendingDraftReady(run, "not-a-number"))
            throw new InvalidOperationException("The actual invalid native widget draft is not retained.");
        var rejected = false;
        try { StartActiveProjectSwitch(run.Owners.Second); }
        catch (Exception error) when (error is FormatException or OverflowException) { rejected = true; }
        var draft = CaptureActiveProjectPendingDraft(run);
        if (!rejected || !draft.Pending || !draft.Edited || !draft.OwnedLease || draft.Error == null
            || activeProjectBuild != null || activeProjectTarget != null || activeProjectCancellation != null
            || !ReferenceEquals(projectContent, run.Owners.BaselineContent)
            || !ReferenceEquals(workspace!.Session.CurrentProject, run.Owners.First)
            || !Equals(run.Field.Node.Retrieve(), run.Before))
            throw new InvalidOperationException("Invalid pending native numeric text did not refuse compiler submission without mutation.");
        AssertActiveProjectParityOwners(run.Owners);
        run.Checks.Add(new { contract = "invalid-pending-refused", actualDraft = InspectActiveProjectPendingDraft(run), noProducer = true });
    }

    internal bool ActiveProjectPendingDraftCancelled(ActiveProjectPendingParityRun run)
        => !PropertyGrid.Strings.HasPending && CaptureActiveProjectPendingDraft(run).Error == null
            && Equals(run.Field.Node.Retrieve(), run.Before);

    internal void SubmitValidActiveProjectPendingSwitch(ActiveProjectPendingParityRun run)
    {
        if (!ActiveProjectPendingDraftReady(run, run.After.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            throw new InvalidOperationException("The actual valid native widget draft is not retained before the structural command.");
        StartActiveProjectSwitch(run.Owners.Second);
        var task = activeProjectBuild ?? throw new InvalidOperationException("Valid draft completion did not submit the actual compiler request.");
        run.Owners.Requests.Add(task); run.CancelledTask = task;
        var snapshot = run.Owners.History.History;
        var entries = snapshot.Entries.ToArray();
        if (snapshot.Position != run.Position + 1 || entries.Length != run.Position + 1
            || !entries.Take(run.Position).Select(entry => entry.Id).SequenceEqual(run.AppliedPrefix)
            || entries[^1].Name != "Edit Counter64" || !entries[^1].IsDone
            || !Equals(run.Field.Node.Retrieve(), run.After) || PropertyGrid.Strings.HasPending)
            throw new InvalidOperationException("Pending numeric completion did not publish exactly its own native transaction before submission.");
        run.Edit = entries[^1].Id;
        var transaction = run.Owners.History.Stack.RetrieveAllTransactions().Single(entry => entry.Id == run.Edit);
        var participants = run.Owners.History.GetParticipants(transaction);
        if (participants.Length != 1 || !ReferenceEquals(participants[0], run.Owners.ViewState))
            throw new InvalidOperationException("The actual pending edit has another canonical history participant.");
        RebaseActiveProjectPendingHistory(run);
        run.Checks.Add(new { contract = "valid-pending-committed-before-request", transaction = run.Edit,
            appliedPrefix = run.AppliedPrefix, participant = participants[0].Generation, value = run.After,
            request = activeProjectTarget!.Path, beforeEditJournal = run.BeforeEditJournal,
            journal = CaptureActiveProjectParityHistory(run.Owners.History) });
    }

    private void RebaseActiveProjectPendingHistory(ActiveProjectPendingParityRun run)
    {
        run.Owners.HistoryIds = VerificationHistoryTransactions.ToArray();
        run.Owners.HistoryPosition = run.Owners.History.History.Position;
        run.Owners.HistoryBefore = CaptureActiveProjectParityHistory(run.Owners.History);
        var protectedStates = run.Owners.Views.Select(entry => entry.State).OfType<EditorAuthoringState>()
            .Concat(run.Owners.History.Stack.RetrieveAllTransactions().SelectMany(run.Owners.History.GetParticipants)).ToHashSet();
        // The exact certified new native edit legitimately truncates old redo.
        // Only those former redo-only participants may lose history retention;
        // original editor entries and the complete applied prefix remain protected.
        run.Owners.StateBaselines = run.Owners.StateBaselines.Select(baseline => baseline with
        {
            ProtectedOwner = protectedStates.Contains(baseline.State),
            HistoryReferences = baseline.State.HistoryReferences,
            ViewReferences = baseline.State.ViewReferences,
            Dirty = ReferenceEquals(baseline.State, run.Owners.ViewState) ? run.Document.IsDirty : baseline.Dirty
        }).ToArray();
    }

    internal bool StepCancelActiveProjectPendingCompiler(ActiveProjectPendingParityRun run)
    {
        if (run.CancellationCertified) return true;
        if (!run.Cancelled)
        {
            if (!Equals(ReadRuntimeComponentValueVerification(run.Field), run.After)) return false;
            if (!ReferenceEquals(activeProjectBuild, run.CancelledTask) || run.CancelledTask!.IsCompleted)
                throw new InvalidOperationException("The real compiler completed before cancellation acceptance could be observed.");
            var state = Volatile.Read(ref activeProjectLoadState);
            if (state is not { Phase: "Compiling assets", ProcessId: { } pid, CompilerRun: { } identity, LogDirectory: { } output }) return false;
            if (!identity.Source.StartsWith("assets:", StringComparison.Ordinal)
                || !SamePath(identity.Source["assets:".Length..], run.Owners.Second.FullPath.ToOSPath()))
                throw new InvalidOperationException("The observed compiler process is not the actual second-project asset request.");
            var process = Process.GetProcessById(pid);
            if (process.HasExited) { process.Dispose(); return false; }
            run.Compiler = process; run.CompilerRun = identity; run.CancelledOutput = output;
            run.CompilerCancellation = activeProjectCancellation!.Token;
            if (run.CompilerCancellation.IsCancellationRequested)
                throw new InvalidOperationException("The real compiler token was already cancelled before this request.");
            run.Checks.Add(new { contract = "real-asset-compiler-cancel-request", process = pid, start = process.StartTime.ToUniversalTime(),
                run = identity, output, target = activeProjectTarget!.Path });
            StopActiveProjectSwitch(); run.Cancelled = true; return false;
        }
        if (!run.CancelledTask!.IsCompleted || run.Compiler?.HasExited != true) return false;
        if (run.CancelledTask.IsFaulted) _ = run.CancelledTask.Exception;
        if (!run.CompilerCancellation.IsCancellationRequested || !run.CancelledTask.IsCanceled)
            throw new InvalidOperationException("The observed real asset compiler did not terminate through native request cancellation.");
        if (activeProjectBuild != null || activeProjectTarget != null || activeProjectCancellation != null
            || !ReferenceEquals(workspace!.Session.CurrentProject, run.Owners.First)
            || !ReferenceEquals(projectContent, run.Owners.BaselineContent))
            throw new InvalidOperationException("The genuinely cancelled compiler adopted content or changed native project ownership.");
        if (Directory.Exists(run.CancelledOutput)) return false;
        var archive = OwnedProjectBuildOutput.FailedOutputLogDirectory(run.CancelledOutput!);
        if (!File.Exists(Path.Combine(archive, "assets.log")) || !File.Exists(Path.Combine(archive, "project.log")))
            throw new InvalidOperationException("Cancelled native compiler output retired without preserving its actual project/asset logs.");
        run.Owners.CertifiedCancelledCompilerRetirement = new
        {
            task = run.CancelledTask.Status.ToString(), cancellationRequested = run.CompilerCancellation.IsCancellationRequested,
            processId = run.Compiler!.Id, processExited = run.Compiler.HasExited,
            compilerRun = run.CompilerRun, ownedOutput = run.CancelledOutput, ownedOutputDeleted = true,
            noAdoption = true, archivedProjectLog = Path.Combine(archive, "project.log"),
            archivedAssetLog = Path.Combine(archive, "assets.log")
        };
        AssertActiveProjectParityOwners(run.Owners);
        run.CancellationCertified = true;
        run.Checks.Add(new { contract = "cancelled-compiler-retired", task = run.CancelledTask.Status.ToString(),
            processExited = true, ownedOutputDeleted = true, noAdoption = true, archivedLogs = archive });
        return true;
    }

    internal object? StepActiveProjectPendingReturn(ActiveProjectPendingParityRun run)
    {
        if (!run.CancellationCertified) throw new InvalidOperationException("Actual compiler cancellation retirement is not certified.");
        var evidence = StepActiveProjectParityVerification(run.Owners);
        if (evidence == null) return null;
        if (!RetireActiveProjectPendingParityVerification(run)) return null;
        AssertActiveProjectParityOwners(run.Owners);
        if (run.Document.IsDirty || !Equals(run.Field.Node.Retrieve(), run.Before)
            || !Equals(ReadRuntimeComponentValueVerification(run.Field), run.Before)) return null;
        return new { passed = true, checks = run.Checks.ToArray(), switching = evidence,
            journal = CaptureActiveProjectParityHistory(run.Owners.History), nativeSavepointRestored = true,
            scope = "Actual invalid/valid Counter64 widget drafts, native structural-command validation, genuine asset compiler cancellation and physical output retirement, native switch/return and guarded Undo. Physical input and stale membership/path branches remain separate." };
    }

    internal bool RetireActiveProjectPendingParityVerification(ActiveProjectPendingParityRun run)
    {
        if (run.Retired) return true;
        PropertyGrid.Strings.AbandonPending(run.Document);
        if (!RetireActiveProjectParityVerification(run.Owners)) return false;
        if (!run.EditUndone && run.Edit != null)
        {
            var snapshot = run.Owners.History.History;
            if (snapshot.Position != run.Position + 1 || snapshot.Entries.Count != run.Position + 1
                || snapshot.Entries[^1].Id != run.Edit || !snapshot.Entries[^1].IsDone
                || !snapshot.Entries.Take(run.Position).Select(entry => entry.Id).SequenceEqual(run.AppliedPrefix))
                throw new InvalidOperationException("Pending-switch cleanup refuses to undo another actor's transaction.");
            run.Owners.History.Undo(); run.EditUndone = true; RebaseActiveProjectPendingHistory(run);
        }
        if (run.Compiler != null && !run.Compiler.HasExited) return false;
        if (run.Cancelled && Directory.Exists(run.CancelledOutput)) return false;
        run.Compiler?.Dispose(); run.Compiler = null; run.Retired = true;
        if (ReferenceEquals(activeProjectPendingParity, run)) activeProjectPendingParity = null;
        return true;
    }

    internal bool TryDrainActiveProjectPendingParityVerification()
        => activeProjectPendingParity == null || RetireActiveProjectPendingParityVerification(activeProjectPendingParity);
}
