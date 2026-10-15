using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineRecoveryWorkspaceCapabilityProbe
{
    private readonly bool missingGeneratedDirtyProducer;
    private RecoveryMissingGeneratedProducerReceipt? missingGeneratedProducerReceipt;
    private RecoveryMissingGeneratedState? missingProducerBeforeEdit, missingProducerDirty, missingWorkspaceProjection;
    private RecoveryMissingGeneratedState? missingRecoveredProjection, missingUndoProjection, missingRedoProjection;
    private object? missingPreRestoreBoundary, MissingGeneratedRecovery;
    private bool missingPreRestoreReadyWritten, missingPreRestoreValidated, protectedSourceDirtyBaseline;
    private string? missingReadyPath, missingResumePath;
    private int missingBoundaryFrame;
    private EditorHistorySnapshot? missingBeforeProducerRestore;
    private Guid missingProducerRestoreTransaction;
    private double missingProducerNavigationDeadline;
    private string? missingBoundaryHistory;

    private bool StepMissingGeneratedScenario()
    {
        if (producer)
        {
            switch (Phase)
            {
                case 140:
                    missingProducerNavigationDeadline = Clock.Elapsed.TotalSeconds + 30;
                    missingProducerBeforeEdit = Game.InspectRecoveryMissingGeneratedState(generatedReadOnlyReceipt!, false);
                    Require(!missingProducerBeforeEdit.ProducerDirty && !missingProducerBeforeEdit.GeneratedDiskAbsent
                        && missingProducerBeforeEdit.CompiledSource == generatedReadOnlyReceipt!.GeneratedText,
                        "Missing-output recovery must start from a genuinely saved existing native visual-script producer and compiler artifact");
                    Next(); return true;
                case 141:
                    Require(Clock.Elapsed.TotalSeconds < missingProducerNavigationDeadline, "Actual visual-script producer navigation exceeded its bounded deadline");
                    if (Click("document-tab/" + Game.RecoveryMissingGeneratedProducerEntry(generatedReadOnlyReceipt!))) Next();
                    return true;
                case 142: if (Click("visual-script/add-property")) Next(); return true;
                case 143:
                    var changed = Game.InspectRecoveryMissingGeneratedState(generatedReadOnlyReceipt!, false);
                    if (!changed.ProducerDirty) return true;
                    Require(changed.PropertyCount == missingProducerBeforeEdit!.PropertyCount + 1
                        && changed.CompiledSource != missingProducerBeforeEdit.CompiledSource
                        && changed.ProducerGeneration == missingProducerBeforeEdit.ProducerGeneration
                        && changed.GeneratedGeneration == missingProducerBeforeEdit.GeneratedGeneration
                        && changed.History.Position == missingProducerBeforeEdit.History.Position + 1,
                        "Actual native Add-property gesture must edit the saved producer, update its real compiler buffer and create one owned history transaction");
                    missingProducerDirty = changed;
                    AssertOriginalDisks(); SeekPhase(60); return true;
                default: return false;
            }
        }
        switch (Phase)
        {
            case 160:
                Require(missingWorkspaceProjection != null, "Separate producer consent requires the prior saved workspace-only compiler projection proof");
                missingBeforeProducerRestore = Game.InspectRecoveryMissingGeneratedState(generatedReadOnlyReceipt!, true).History;
                if (ClickRecovery("recovery/document/" + receipt!.SessionId + "/" + missingGeneratedProducerReceipt!.ProducerPayloadHash, -1)) Next();
                return true;
            case 161:
                var recovered = Game.InspectRecoveryMissingGeneratedState(generatedReadOnlyReceipt!, true);
                if (!recovered.ProducerDirty) return true;
                Require(recovered.ProducerGeneration != missingWorkspaceProjection!.ProducerGeneration
                    && recovered.GeneratedGeneration == missingWorkspaceProjection.GeneratedGeneration
                    && recovered.CompiledSource == missingGeneratedProducerReceipt!.DirtyCompilerText
                    && recovered.ActualNativeProducerLease && recovered.GeneratedCanEditText == false,
                    "Separate real producer payload consent must publish its canonical generation and rebind the existing read-only generated view to that actual native compiler owner");
                Require(recovered.History.Position == missingBeforeProducerRestore!.Position + 1,
                    "Actual producer payload Restore must add one native retained-generation history transaction");
                missingProducerRestoreTransaction = recovered.History.Entries[recovered.History.Position - 1].Id;
                missingRecoveredProjection = recovered;
                if (ClickRecovery("recovery/close", -1)) Next();
                return true;
            case 162:
                Require(Game.CaptureRecoveryAcceptanceHistory() == JsonSerializer.Serialize(missingRecoveredProjection!.History),
                    "Modal dismissal must not mutate the recovered native producer transaction history");
                if (Click("toolbar/undo")) Next(); return true;
            case 163:
                var undone = Game.InspectRecoveryMissingGeneratedState(generatedReadOnlyReceipt!, true);
                if (undone.ProducerGeneration != missingWorkspaceProjection!.ProducerGeneration) return true;
                Require(undone.CompiledSource == missingGeneratedProducerReceipt!.SavedCompilerText
                    && !undone.ProducerDirty && undone.GeneratedGeneration == missingWorkspaceProjection.GeneratedGeneration
                    && undone.History.Position == missingBeforeProducerRestore!.Position
                    && undone.History.Entries[undone.History.Position].Id == missingProducerRestoreTransaction
                    && !undone.History.Entries[undone.History.Position].IsDone && undone.ActualNativeProducerLease,
                    "Actual native Undo must retain the original producer generation, saved compiler projection and current generated-view lease without writing its missing file");
                missingUndoProjection = undone;
                if (Click("toolbar/redo")) Next(); return true;
            case 164:
                var redone = Game.InspectRecoveryMissingGeneratedState(generatedReadOnlyReceipt!, true);
                if (redone.ProducerGeneration != missingRecoveredProjection!.ProducerGeneration) return true;
                Require(redone.CompiledSource == missingGeneratedProducerReceipt!.DirtyCompilerText && redone.ProducerDirty
                    && redone.GeneratedGeneration == missingWorkspaceProjection!.GeneratedGeneration
                    && redone.History.Position == missingRecoveredProjection.History.Position
                    && redone.History.Entries[redone.History.Position - 1].Id == missingProducerRestoreTransaction
                    && redone.History.Entries[redone.History.Position - 1].IsDone && redone.ActualNativeProducerLease,
                    "Actual native Redo must recover the exact producer generation and compiler text without duplicated generated operations or lost owner leases");
                missingRedoProjection = redone;
                MissingGeneratedRecovery = new { ActualCompilerOwned = true, DirtyProducerRecovered = true,
                    GeneratedDiskRemainedAbsent = true, GeneratedPath = generatedReadOnlyReceipt!.GeneratedPath,
                    ProducerAssetId = generatedReadOnlyReceipt.ProducerAssetId, ProducerPath = generatedReadOnlyReceipt.ProducerPath,
                    CompiledSourceSha256 = MissingTextHash(redone.CompiledSource), ProducerDiskSha256 = Hash(generatedReadOnlyReceipt.ProducerPath),
                    RecoveredProducerPayloadHash = missingGeneratedProducerReceipt.ProducerPayloadHash,
                    NativeUndoRedoVerified = true, GeneratedLeaseRebound = true,
                    savedWorkspace = missingWorkspaceProjection, recovered = missingRecoveredProjection,
                    undo = missingUndoProjection, redo = missingRedoProjection, nativeTransaction = missingProducerRestoreTransaction };
                AssertOriginalDisks(); sourceIndex = 0; SeekPhase(165); return true;
            case 165: if (Click("menu/file")) Next(); return true;
            case 166: if (Click("menu/file/recover-changes")) SeekPhase(40); return true;
            default: return false;
        }
    }
    private RecoveryMissingGeneratedProducerReceipt CaptureMissingGeneratedProducerReceipt(EditorRecoveryManifest manifest,
        RecoveryGeneratedReadOnlySourcesReceipt expected)
    {
        var actual = Game.InspectRecoveryMissingGeneratedState(expected, false);
        Require(missingProducerDirty != null && actual.ProducerDirty && actual.CompiledSource == missingProducerDirty.CompiledSource,
            "Crash checkpoint must contain the actual native UI-edited visual-script producer");
        var entry = manifest.Documents.Single(document => Same(document.Path, expected.ProducerPath));
        Require(entry.AssetId == expected.ProducerAssetId && !manifest.Documents.Any(document => Same(document.Path, expected.GeneratedPath)),
            "The actual producer payload must retain its native asset identity, with no independent generated dirty payload");
        return new(entry.PayloadHash, actual.CompiledSource, MissingTextHash(actual.CompiledSource), expected.GeneratedText,
            Hash(expected.ProducerPath), true);
    }
    private bool ValidateMissingGeneratedPreRestoreBoundary()
    {
        if (missingPreRestoreValidated) return true;
        var arguments = Environment.GetCommandLineArgs();
        string Argument(string name)
        {
            var index = Array.IndexOf(arguments, name);
            if (index < 0 || index + 1 >= arguments.Length) throw new ArgumentException(name + " is required");
            var path = Path.GetFullPath(arguments[index + 1]);
            if (!Same(Path.GetDirectoryName(path)!, Artifacts)) throw new InvalidOperationException("The missing-output boundary must remain in this receiver's artifact directory");
            return path;
        }
        missingReadyPath ??= Argument("--recovery-pre-restore-ready");
        missingResumePath ??= Argument("--recovery-pre-restore-resume");
        var expected = generatedReadOnlyReceipt!;
        if (!missingPreRestoreReadyWritten)
        {
            Require(Hash(expected.GeneratedPath) == expected.GeneratedSha256 && Hash(expected.ProducerPath) == expected.ProducerSha256,
                "Actual native project load must retain the saved producer and exact compiler artifact before certified private deletion");
            var certificate = new { Version = 1, ProcessId = Environment.ProcessId,
                ProcessStartedUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                ProjectPath = Game.CaptureRecoveryWorkspaceVerification().ProjectPath,
                expected.GeneratedPath, expected.GeneratedSha256, expected.ProducerAssetId, expected.ProducerPath,
                owner = Game.CaptureRecoveryMissingGeneratedPreRestoreOwner(), frame = Gui.GetFrameCount() };
            missingPreRestoreBoundary = certificate;
            missingBoundaryHistory = Game.CaptureRecoveryAcceptanceHistory();
            var temporary = missingReadyPath + ".pending";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, certificate); stream.Flush(true); }
            File.Move(temporary, missingReadyPath);
            missingBoundaryFrame = Gui.GetFrameCount(); missingPreRestoreReadyWritten = true;
        }
        if (!File.Exists(missingResumePath)) return false;
        using var resume = JsonDocument.Parse(File.ReadAllBytes(missingResumePath));
        var data = resume.RootElement;
        Require(data.GetProperty("Version").GetInt32() == 1 && data.GetProperty("ProcessId").GetInt32() == Environment.ProcessId
            && data.GetProperty("ProcessStartedUtcTicks").GetInt64() == Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks
            && Same(data.GetProperty("ProjectPath").GetString()!, receipt!.ProjectPath)
            && Same(data.GetProperty("GeneratedPath").GetString()!, expected.GeneratedPath)
            && data.GetProperty("GeneratedSha256").GetString() == expected.GeneratedSha256
            && data.GetProperty("ProducerAssetId").GetString() == expected.ProducerAssetId
            && Same(data.GetProperty("ProducerPath").GetString()!, expected.ProducerPath)
            && data.GetProperty("ActualMissing").GetBoolean() && !File.Exists(expected.GeneratedPath) && !Directory.Exists(expected.GeneratedPath)
            && Gui.GetFrameCount() >= missingBoundaryFrame,
            "Resume must certify exact owned loaded receiver/path/compiler baseline and actual missing file before workspace consent");
        missingPreRestoreValidated = true; return true;
    }
    private object VerifyMissingGeneratedSavedWorkspaceProjection()
    {
        var actual = Game.InspectRecoveryMissingGeneratedState(generatedReadOnlyReceipt!, true);
        Require(missingPreRestoreValidated && !actual.ProducerDirty && actual.CompiledSource == missingGeneratedProducerReceipt!.SavedCompilerText,
            "Workspace-only restore must project the saved native producer; it must not silently apply its separate dirty recovery payload or write the missing file");
        Require(Game.CaptureRecoveryAcceptanceHistory() == missingBoundaryHistory,
            "Missing generated workspace-only projection must not create authored history transactions or modify savepoints");
        missingWorkspaceProjection = actual;
        return actual;
    }
    private object VerifyMissingGeneratedRecoveredProjection()
    {
        var actual = Game.InspectRecoveryMissingGeneratedState(generatedReadOnlyReceipt!, true);
        Require(missingRedoProjection != null && actual.ProducerGeneration == missingRedoProjection.ProducerGeneration
            && actual.GeneratedGeneration == missingRedoProjection.GeneratedGeneration && actual.ProducerDirty
            && actual.CompiledSource == missingGeneratedProducerReceipt!.DirtyCompilerText && actual.ActualNativeProducerLease,
            "The real recovered dirty producer and its read-only missing generated projection must retain exact native compiler text and ownership");
        return actual;
    }
    private static string MissingTextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
