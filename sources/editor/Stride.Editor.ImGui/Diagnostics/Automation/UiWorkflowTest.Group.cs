using System.Security.Cryptography;
using System.Text.Json;
using Stride.Assets.Entities;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class UiWorkflowTest
{
    private sealed class InspectorGroup(string artifacts)
    {
        internal readonly string Artifacts = Path.GetFullPath(artifacts);
        internal readonly List<object> Cases = [];
        internal SceneDocument? Scene;
        internal object? Viewport;
        internal string? BaselineAsset, BaselineHierarchy;
        internal bool ResetVerified;
    }
    private InspectorGroup? inspectorGroup;

    internal void ConfigureInspectorGroup(string identity, string artifacts)
    {
        if (identity != "U03.group") throw new ArgumentException("Only U03.group is supported.");
        SelectScenario(UiWorkflowScenario.U03Inspector);
        inspectorGroup = new(artifacts);
        Directory.CreateDirectory(inspectorGroup.Artifacts);
    }

    private static string NativeSceneFingerprint(string path)
    {
        var loaded = AssetFileSerializer.Load<SceneAsset>(path);
        using var bytes = new MemoryStream();
        AssetFileSerializer.Save(bytes, loaded.Asset, loaded.YamlMetadata);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    private void CaptureInspectorGroupBaseline()
    {
        if (inspectorGroup is not { BaselineAsset: null } group) return;
        Assert(game.Document != null && !game.Document.IsDirty && !game.Document.CanUndo,
            "Inspector group requires a clean canonical baseline with no pre-existing undo history");
        group.Scene = game.Document;
        group.Viewport = game.WorkflowGroupViewport;
        group.BaselineAsset = NativeSceneFingerprint(group.Scene!.Path);
        group.BaselineHierarchy = JsonSerializer.Serialize(game.CaptureInspectionSnapshot(true).Hierarchy);
    }

    private void WriteInspectorGroupCase()
    {
        if (inspectorGroup is not { } group) return;
        var id = scenario == UiWorkflowScenario.U03Inspector ? "U03" : "U03.references";
        WriteResult();
        var directory = Path.Combine(group.Artifacts, id);
        Directory.CreateDirectory(directory);
        File.Copy(WorkflowResultPath, Path.Combine(directory, "result.json"), true);
        group.Cases.Add(new { id, scenario = scenario.ToString(), passed = Passed,
            durationSeconds = timer.Elapsed.TotalSeconds, result = Result,
            capture = "final." + id + ".png", teardownCertificate = "teardown-result.json" });
        game.CaptureInspectorGroupCase(id);
        File.WriteAllText(Path.Combine(group.Artifacts, "group-result.json"), JsonSerializer.Serialize(new
        { schema = 1, group = "U03.group", passed = group.Cases.Count == 2 && Passed,
            initialProjectLoads = 1, baselineResetVerified = group.ResetVerified,
            teardown = "pending shared terminal certificate", teardownCertificate = "teardown-result.json", cases = group.Cases },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private bool ContinueInspectorGroup()
    {
        if (inspectorGroup == null) return false;
        WriteInspectorGroupCase();
        if (scenario == UiWorkflowScenario.U03ReferencesCollections)
        {
            Result = "UI U03.group PASS: independent inspector and reference/collection cases; baseline restored; one shared terminal teardown required.";
            return false;
        }
        Passed = Finished = false;
        stage = 12000;
        return true;
    }

    private void ReportInspectorGroupFailure()
    {
        if (inspectorGroup is not { } group) return;
        if (group.Cases.Count == 0 || scenario == UiWorkflowScenario.U03ReferencesCollections) WriteInspectorGroupCase();
        File.WriteAllText(Path.Combine(group.Artifacts, "group-result.json"), JsonSerializer.Serialize(new
        { schema = 1, group = "U03.group", passed = false, result = Result,
            initialProjectLoads = group.Scene == null ? 0 : 1, baselineResetVerified = group.ResetVerified,
            teardownCertificate = "teardown-result.json", cases = group.Cases }));
    }

    private void UpdateInspectorGroupReset()
    {
        var group = inspectorGroup ?? throw new InvalidOperationException("Inspector group state is missing.");
        Assert(ReferenceEquals(game.Document, group.Scene) && ReferenceEquals(game.WorkflowGroupViewport, group.Viewport),
            "Grouped cases must retain the same canonical scene and native viewport owner");
        switch (stage)
        {
            case 12000:
                if (game.Document!.CanUndo) { Click("toolbar/undo"); break; }
                stage = 12001; break;
            case 12001:
                if (game.Document!.IsDirty) { if (Click("toolbar/save")) stage = 12002; break; }
                stage = 12002; break;
            case 12002:
                if (game.Document!.IsDirty || game.WorkflowGroupAuthoringBusy) break;
                var state = game.CaptureInspectionSnapshot(true);
                Assert(!game.Document.CanUndo && NativeSceneFingerprint(game.Document.Path) == group.BaselineAsset
                    && JsonSerializer.Serialize(state.Hierarchy) == group.BaselineHierarchy
                    && state.Viewport.HasRuntimeScene && game.EntityReferencesMatch,
                    "Actual Undo/Save must restore the independently reloaded baseline asset, runtime hierarchy and canonical references before the next case");
                group.ResetVerified = true;
                File.WriteAllText(Path.Combine(group.Artifacts, "baseline-reset.json"), JsonSerializer.Serialize(new
                { passed = true, state, independentlyLoadedAsset = group.BaselineAsset, sameSceneOwner = true, sameViewportOwner = true }));
                game.CloseApprovalPrepared -= CaptureApprovedCloseState;
                game.CloseApproved -= VerifyApprovedClose;
                var next = new UiWorkflowTest(game) { inspectorGroup = group };
                next.SelectScenario(UiWorkflowScenario.U03ReferencesCollections);
                next.stage = 11000;
                game.ReplaceInspectorGroupController(next);
                break;
        }
    }
}

internal sealed partial class EditorGame
{
    internal object? WorkflowGroupViewport => InspectionViewport;
    internal bool WorkflowGroupAuthoringBusy => IsAuthoringPipelineUpdating;
    internal void ReplaceInspectorGroupController(UiWorkflowTest next) => workflowTest = next;
    internal void CaptureInspectorGroupCase(string id) => captureCheckpointSuffix = "." + id + ".png";
}
