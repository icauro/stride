using System.Diagnostics;
using Hexa.NET.ImGui;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame { private UiWorkflowTest? workflowTest; }

/// <summary>Condition-based input-path E2E test. Assertions inspect model and persisted output.</summary>
internal sealed partial class UiWorkflowTest
{
    private readonly EditorGame game;
    private readonly bool discardClose;
    private readonly bool planeOnly;
    private readonly bool pickingOnly;
    public UiWorkflowTest(EditorGame game, bool discardClose = false, bool planeOnly = false, bool pickingOnly = false)
    {
        this.game = game; this.discardClose = discardClose; this.planeOnly = planeOnly; this.pickingOnly = pickingOnly;
        game.CloseApprovalPrepared += CaptureApprovedCloseState;
        game.CloseApproved += VerifyApprovedClose;
    }
    private readonly Stopwatch timer = Stopwatch.StartNew();
    private readonly List<string> checks = [];
    private int stage, wait;
    private bool releaseMouse;
    private int releaseMouseButton;
    private int mousePressFrame;
    private int lastWorkflowGuiFrame = -1;
    private double nextDiagnostic;
    private bool cancelledFirstLoad;
    private int cancelledCompiler;
    private SceneDocument? preservedDocument;
    private TextureAssetPreview? preservedPreview;
    private byte[]? closeDiskBackup;
    private Stride.Core.Mathematics.Matrix initialCamera, previousCamera;
    private SceneDrawSummary? visibleSceneDraw, hiddenSceneDraw;
    private bool renameVerified;
    private bool renameCancelVerified;
    private bool renameCloseVerified;
    private bool importedViewVerified;
    private bool componentWorkflowVerified;
    private Guid renameId;
    private int renameRound;
    private int colorPopupRound;
    private bool pickerGestureVerified;
    private ModelAssetPreview? priorModelPreview;
    private Stride.Core.Mathematics.Matrix modelInitialCamera, modelPreviousCamera, modelSceneCamera;
    private object? modelPreviewEvidence;
    private readonly List<object> proceduralPreviewEvidence = [];
    private int proceduralPreviewRound;
    private bool proceduralWorkflowVerified, importedPreviewVerified;
    private const string RenamedEntity = "Sphere renamed – 星 %##";
    public bool Finished { get; private set; }
    public bool Passed { get; private set; }
    public string Result { get; private set; } = "";
    private static string WorkflowResultPath
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS");
            if (string.IsNullOrWhiteSpace(root)) return Path.Combine(AppContext.BaseDirectory, "ui-result.json");
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
            return Path.Combine(root, "result.json");
        }
    }
    internal static string WorkflowTracePath
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_ARTIFACTS");
            if (string.IsNullOrWhiteSpace(root)) return Path.Combine(AppContext.BaseDirectory, "ui-trace.log");
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
            return Path.Combine(root, "ui-trace.log");
        }
    }
    public void WriteResult() => File.WriteAllText(WorkflowResultPath,
        System.Text.Json.JsonSerializer.Serialize(new
        {
            passed = Passed, result = Result, scenario = scenario.ToString(), scenarioScope = ScenarioScope, checks, durationSeconds = timer.Elapsed.TotalSeconds,
            engineDrawCalls = game.EngineDrawCalls, runtime = Environment.Version.ToString(), backend = "Direct3D11",
            adapter = game.GraphicsDevice.Adapter.DriverInfo, dataset = game.MeasurementDataset,
            importedModel = game.ImportedModelEvidence,
            modelPreview = modelPreviewEvidence,
            proceduralPreviews = proceduralPreviewEvidence,
            previewVisibility = previewVisibilityEvidence,
            vectorWorkflows = vectorWorkflowEvidence,
            assetEditing = assetEditingEvidence,
            planeEditing = planeEditingEvidence,
            picking = pickingEvidence,
            cameraProjection = cameraProjectionEvidence,
            hierarchyCreation = hierarchyCreationEvidence,
            operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            sceneDrawSubmission = new { renderHiddenScenes = game.DiagnosticRenderHiddenScenes, visible = visibleSceneDraw, hidden = hiddenSceneDraw,
                timingBoundary = "CPU wall time around Game.Draw and target binding/barrier; excludes ImGui draws, GPU completion and presentation. Warm samples within this process; not a WPF comparison." },
            coverage = planeOnly || pickingOnly
                ? "Synthetic ImGui widget input; native window/input, clipboard, OS dialogs, DPI and IME are not covered in this dedicated run."
                : "Synthetic ImGui widget input and native Windows WM_CLOSE dispatch; physical input, clipboard, OS dialogs, DPI and IME are not covered.",
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    public static string CopyFixture(string sourcePackage)
    {
        sourcePackage = Path.GetFullPath(sourcePackage);
        if (!File.Exists(sourcePackage)) throw new FileNotFoundException("UI test package is missing", sourcePackage);
        byte[]? isolatedNuGetConfig = null;
        var isolatedNuGetConfigPath = Environment.GetEnvironmentVariable("STRIDE_EDITOR_TEST_NUGET_CONFIG");
        if (!string.IsNullOrEmpty(isolatedNuGetConfigPath))
        {
            isolatedNuGetConfigPath = Path.GetFullPath(isolatedNuGetConfigPath);
            if (!File.Exists(isolatedNuGetConfigPath))
                throw new FileNotFoundException("The explicitly configured UI test NuGet.Config is missing.", isolatedNuGetConfigPath);
            isolatedNuGetConfig = File.ReadAllBytes(isolatedNuGetConfigPath);
            using var configStream = new MemoryStream(isolatedNuGetConfig, writable: false);
            using var configReader = System.Xml.XmlReader.Create(configStream, new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            var config = System.Xml.Linq.XDocument.Load(configReader);
            if (config.Root?.Name != System.Xml.Linq.XName.Get("configuration"))
                throw new InvalidDataException("The explicitly configured UI test NuGet.Config must have a configuration root.");
        }
        var source = Path.GetDirectoryName(sourcePackage)!;
        var target = Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI", Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part == "obj")) continue;
            var output = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(file, output);
        }
        if (isolatedNuGetConfig != null)
        {
            // Preserve the caller's isolated absolute feed/cache paths and exact configuration bytes.
            File.WriteAllBytes(Path.Combine(target, "NuGet.Config"), isolatedNuGetConfig);
            return Path.Combine(target, Path.GetFileName(sourcePackage));
        }
        // Restore test projects into a dedicated cache; reference editors can hold DLLs
        // in the user's global cache, and same-version dev-package replacement would fail.
        var packageCache = Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI", "packages");
        var devFeed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stride", "nugetdev");
        new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("configuration",
            new System.Xml.Linq.XElement("config", new System.Xml.Linq.XElement("add",
                new System.Xml.Linq.XAttribute("key", "globalPackagesFolder"), new System.Xml.Linq.XAttribute("value", packageCache))),
            new System.Xml.Linq.XElement("packageSources", new System.Xml.Linq.XElement("clear"),
                new System.Xml.Linq.XElement("add", new System.Xml.Linq.XAttribute("key", "StrideDev"), new System.Xml.Linq.XAttribute("value", devFeed)),
                new System.Xml.Linq.XElement("add", new System.Xml.Linq.XAttribute("key", "nuget.org"), new System.Xml.Linq.XAttribute("value", "https://api.nuget.org/v3/index.json")))))
            .Save(Path.Combine(target, "NuGet.Config"));
        return Path.Combine(target, Path.GetFileName(sourcePackage));
    }

    public void Update()
    {
        if (Finished) return;
        var io = Gui.GetIO(); io.AddFocusEvent(true);
        if (timer.Elapsed.TotalSeconds >= nextDiagnostic)
        {
            File.AppendAllText(WorkflowTracePath, $"Stage {stage}, {game.EngineDiagnostic}, status {game.StatusMessage}\n");
            nextDiagnostic = timer.Elapsed.TotalSeconds + 5;
        }
        try { CheckProgressDeadline(); }
        catch (Exception error) { Fail(error); return; }
        var guiFrame = Gui.GetFrameCount();
        if (guiFrame == lastWorkflowGuiFrame) return;
        lastWorkflowGuiFrame = guiFrame;
        if (releaseMouse)
        {
            if (Gui.GetFrameCount() <= mousePressFrame) return;
            // Native event trickling can defer a press behind pointer/focus
            // events for one frame. Allow that bounded frame before balancing.
            if (!Gui.IsMouseDown((ImGuiMouseButton)releaseMouseButton)
                && Gui.GetFrameCount() <= mousePressFrame + 1) return;
            // A native capture/focus update may already release the button.
            // Still submit our balancing release after its owning GUI frame;
            // do not wait forever for a down state that has already ended.
            RecordAction("release", "button=" + releaseMouseButton + "; nativeDown="
                + Gui.IsMouseDown((ImGuiMouseButton)releaseMouseButton));
            io.AddMouseButtonEvent(releaseMouseButton, false); releaseMouse = false; wait = 2; return;
        }
        if (wait-- > 0) return;
        try
        {
            if (scenario != UiWorkflowScenario.Legacy) { UpdateIndependentScenario(); return; }
            if (planeOnly) { UpdatePlaneAssetWorkflow(); return; }
            if (pickingOnly) { UpdateScenePickingWorkflow(); return; }
            switch (stage)
            {
                case 0: if (Click("toolbar/open")) Next(); break;
                case 1: if (Click("project/open")) Next(); break;
                case 2:
                    if (!cancelledFirstLoad && game.ProjectBuildProcessId is { } process && Click("loading/cancel"))
                    { game.CaptureLoadingCheckpoint(); cancelledCompiler = process; stage = -2; wait = 2; break; }
                    if (cancelledFirstLoad && game.Document != null && game.EngineDrawCalls > 0 && game.UsesCompiledProjectContent)
                    {
                        var imported = game.ImportedModelEvidence;
                        if (imported.FixturePresent && !importedViewVerified)
                        {
                            Assert(imported.SourceIsImportedModel && imported.Meshes > 0 && imported.VertexBufferBytes > 0 && imported.IndexBufferBytes > 0 && imported.MaterialPasses > 0,
                                "Imported fixture must resolve a ModelAsset, real compiled GPU mesh buffers and compiled material passes");
                            if (Click("entity/Imported cube")) { previousCamera = game.SceneCameraView; stage = 300; wait = 2; }
                            break;
                        }
                        if (Click("entity/Sphere")) { checks.Add("Open real package and render compiled skybox and authored engine compositor"); Next(); }
                    }
                    break;
                case 300:
                    Assert(game.Document?.Selected?.Name == "Imported cube" && game.Document.CanEditSelected, "Imported fixture must be selectable as an editable real scene entity");
                    if (Click("camera/focus")) { stage = 301; wait = 2; } break;
                case 301:
                    Assert(game.SceneCameraView != previousCamera && !game.Document!.IsDirty, "Framing an imported mesh must use the editor camera without editing the asset");
                    io.AddMousePosEvent(1100, 760); stage = 302; wait = 2; break;
                case 302:
                    game.CaptureImportedCheckpoint(); stage = 303; wait = 2; break;
                case 303: if (Click("camera/reset")) { stage = 304; wait = 2; } break;
                case 304:
                    Assert(game.SceneCameraView == previousCamera && !game.Document!.IsDirty, "Imported-model focus/reset must preserve the original editor view and document");
                    importedViewVerified = true; checks.Add("Imported FBX compilation, GPU buffers/materials, hierarchy selection and engine-camera framing");
                    stage = 2; wait = 2; break;
                case -2:
                    if (!game.IsProjectLoading)
                    {
                        Assert(game.Document == null && game.StatusMessage == "Project loading cancelled", "Cancelled loading must not publish a workspace");
                        Assert(ProcessExited(cancelledCompiler), "Cancellation must terminate the actual compiler process");
                        checks.Add("Cancel project loading through UI, stop compiler and retry");
                        if (Click("toolbar/open")) { cancelledFirstLoad = true; stage = -1; wait = 2; }
                    }
                    break;
                case -1: if (Click("project/open")) { stage = 2; wait = 2; } break;
                case 3: if (game.Document?.Selected?.Name == "Sphere" && Click("Position/0")) { checks.Add("Select entity through hierarchy"); Next(); } break;
                case 4: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
                case 5:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                    foreach (var character in "2.5") io.AddInputCharacter(character);
                    Next(); break;
                case 6: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
                case 7: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
                case 8: if (Click("entity/Sphere")) Next(); break;
                case 9:
                    AssertPosition(2.5f); Assert(game.Document!.IsDirty && game.Document.CanUndo, "Grouped edit must be dirty and undoable");
                    checks.Add("Edit transform through input widget"); if (Click("toolbar/undo")) Next(); break;
                case 10:
                    AssertPosition(0); Assert(!game.Document!.IsDirty, "Undo must restore clean state");
                    checks.Add("Undo and dirty tracking"); if (Click("toolbar/redo")) Next(); break;
                case 11:
                    AssertPosition(2.5f); checks.Add("Redo"); if (Click("toolbar/save")) Next(); break;
                case 12:
                    Assert(!game.Document!.IsDirty, "Save must mark the document clean");
                    var disk = AssetFileSerializer.Load<SceneAsset>(game.Document.Path).Asset;
                    Assert(disk.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity.Transform.Position.X == 2.5f,
                        "Save must persist the UI edit");
                    Assert(disk.Hierarchy.Parts.Keys.SequenceEqual(game.Document.Asset.Hierarchy.Parts.Keys), "Entity identities must round-trip");
                    checks.Add("Save and serialized identity round-trip"); if (Click("toolbar/reload")) Next(); break;
                case 13: if (Click("entity/Sphere")) Next(); break;
                case 14:
                    AssertPosition(2.5f); Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Reopen must create a clean document");
                    if (!renameVerified)
                    { renameId = game.Document.Selected!.Id; io.AddKeyEvent(ImGuiKey.F2, true); stage = 200; wait = 2; break; }
                    if (!componentWorkflowVerified)
                    { if (Click("entity/Directional light")) { stage = 400; wait = 2; } break; }
                    checks.Add("Reload persisted scene");
                    initialCamera = previousCamera = game.SceneCameraView;
                    if (Click("camera/focus")) { stage = 100; wait = 2; } break;
                case 400: if (Click(LightFieldControl)) { stage = 401; wait = 2; } break;
                case 401: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 402; wait = 2; break;
                case 402:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                    foreach (var character in "10") io.AddInputCharacter(character); stage = 403; wait = 2; break;
                case 403: io.AddKeyEvent(ImGuiKey.Enter, true); stage = 404; wait = 2; break;
                case 404: io.AddKeyEvent(ImGuiKey.Enter, false); stage = 405; wait = 2; break;
                case 405: if (Click("entity/Directional light")) { stage = 406; wait = 2; } break;
                case 406:
                    AssertLight(10); Assert(game.Document!.IsDirty && game.Document.CanUndo, "Component edit must enter document history");
                    if (Click("toolbar/undo")) { stage = 407; wait = 2; } break;
                case 407:
                    AssertLight(20); Assert(!game.Document!.IsDirty, "Component undo must restore the clean state");
                    if (Click("toolbar/redo")) { stage = 408; wait = 2; } break;
                case 408: AssertLight(10); if (Click("toolbar/save")) { stage = 409; wait = 2; } break;
                case 409:
                    var lightDisk = AssetFileSerializer.Load<SceneAsset>(game.Document!.Path).Asset;
                    Assert(lightDisk.Hierarchy.Parts[LightField.Component.Entity.Id].Entity.Get<Stride.Engine.LightComponent>()!.Intensity == 10,
                        "Component save must preserve identity and scalar value");
                    if (Click("toolbar/reload")) { stage = 410; wait = 2; } break;
                case 410: if (Click("entity/Directional light")) { stage = 411; wait = 2; } break;
                case 411:
                    AssertLight(10); Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Component reopen must be clean");
                    io.AddMousePosEvent(1100, 760); stage = 412; wait = 2; break;
                case 412: game.CaptureComponentCheckpoint(); stage = 413; wait = 2; break;
                case 413: if (Click(LightFieldControl)) { stage = 414; wait = 2; } break;
                case 414: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 415; wait = 2; break;
                case 415:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                    foreach (var character in "20") io.AddInputCharacter(character); stage = 416; wait = 2; break;
                case 416: io.AddKeyEvent(ImGuiKey.Enter, true); stage = 417; wait = 2; break;
                case 417: io.AddKeyEvent(ImGuiKey.Enter, false); stage = 418; wait = 2; break;
                case 418: if (Click("toolbar/save")) { stage = 419; wait = 2; } break;
                case 419: if (Click("toolbar/reload")) { stage = 420; wait = 2; } break;
                case 420: if (Click("entity/Directional light")) { stage = 421; wait = 2; } break;
                case 421: AssertLight(20); stage = 450; wait = 2; break;
                case 450: if (ExpandLightPath("Type")) { stage = 451; wait = 2; } break;
                case 451: if (ExpandLightPath("Type.Color")) { stage = 452; wait = 2; } break;
                case 452: if (ExpandLightPath("Type.Color.Value")) { stage = 453; wait = 2; } break;
                case 453: if (Click(LightPathControl("Type.Color.Value.R"))) { stage = 454; wait = 2; } break;
                case 454: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 455; wait = 2; break;
                case 455:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                    foreach (var character in "0.5") io.AddInputCharacter(character); stage = 456; wait = 2; break;
                case 456: io.AddKeyEvent(ImGuiKey.Enter, true); stage = 457; wait = 2; break;
                case 457: io.AddKeyEvent(ImGuiKey.Enter, false); stage = 458; wait = 2; break;
                case 458: if (Click("entity/Directional light")) { stage = 459; wait = 2; } break;
                case 459:
                    AssertRed(0.5f); Assert(game.Document!.IsDirty, "Nested color edit must dirty the scene");
                    if (Click("toolbar/undo")) { stage = 460; wait = 2; } break;
                case 460: AssertRed(1f); Assert(!game.Document!.IsDirty, "Nested color undo must restore clean state");
                    if (Click("toolbar/redo")) { stage = 461; wait = 2; } break;
                case 461: AssertRed(0.5f); if (Click("toolbar/save")) { stage = 462; wait = 2; } break;
                case 462:
                    var colorDisk = AssetFileSerializer.Load<SceneAsset>(game.Document!.Path).Asset;
                    var diskLight = colorDisk.Hierarchy.Parts[LightField.Component.Entity.Id].Entity.Get<Stride.Engine.LightComponent>()!;
                    Assert(((Stride.Rendering.Colors.ColorRgbProvider)((Stride.Rendering.Lights.LightDirectional)diskLight.Type).Color).Value.R == 0.5f,
                        "Nested color save must persist the actual asset value");
                    game.CaptureComponentCheckpoint(); stage = 463; wait = 2; break;
                case 463: if (Click("toolbar/undo")) { stage = 464; wait = 2; } break;
                case 464: AssertRed(1f); if (Click("toolbar/save")) { stage = 465; wait = 2; } break;
                case 465: if (Click("toolbar/reload")) { stage = 466; wait = 2; } break;
                case 466: if (Click("entity/Directional light")) { stage = 467; wait = 2; } break;
                case 467:
                    AssertRed(1f); Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Restored nested color must reopen cleanly");
                    checks.Add("Nested Quantum color editing, boxed runtime propagation, undo/redo, asset save and restored reopen");
                    stage = 500; wait = 2; break;
                case 500: if (ExpandLightPath("Type")) { stage = 501; wait = 2; } break;
                case 501: if (ExpandLightPath("Type.Color")) { stage = 502; wait = 2; } break;
                case 502: if (Click(LightPathControl("Type.Color.Value") + "/swatch")) { stage = 503; wait = 2; } break;
                case 503:
                    if (colorPopupRound == 0 && !pickerGestureVerified)
                    {
                        if (Click(LightPathControl("Type.Color.Value") + "/picker")) { stage = 520; wait = 2; }
                    }
                    else if (Click(LightPathControl("Type.Color.Value") + "/channel/R")) { stage = 504; wait = 2; }
                    break;
                case 520:
                    var picked = SceneDocument.Flatten(game.Document!.GetComponentFields(LightField.Component)).Single(field => field.Name == "Type.Color.Value");
                    Assert(!Equals(picked.Node.Retrieve(), new Stride.Core.Mathematics.Color3(1f)) && game.RuntimeComponentMatches(picked)
                        && game.Document.IsDirty, "Saturation/value picker input must change the authored color and actual engine output");
                    if (Click(LightPathControl("Type.Color.Value") + "/cancel")) { stage = 521; wait = 2; }
                    break;
                case 521:
                    AssertRed(1f); Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Picker gesture Cancel must restore clean data without history");
                    pickerGestureVerified = true; checks.Add("Saturation/value picker gesture updates real engine color and Cancel restores it");
                    stage = 502; wait = 2; break;
                case 504: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 505; wait = 2; break;
                case 505:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                    foreach (var character in colorPopupRound == 2 ? "1" : "0.25") io.AddInputCharacter(character);
                    stage = 506; wait = 2; break;
                case 506: io.AddKeyEvent(ImGuiKey.Enter, true); stage = 507; wait = 2; break;
                case 507: io.AddKeyEvent(ImGuiKey.Enter, false); stage = 508; wait = 2; break;
                case 508:
                    AssertRed(colorPopupRound == 2 ? 1f : 0.25f);
                    if (colorPopupRound == 1) game.CaptureColorCheckpoint();
                    stage = 509; wait = 2; break;
                case 509:
                    if (Click(LightPathControl("Type.Color.Value") + (colorPopupRound == 0 ? "/cancel" : "/done"))) { stage = 510; wait = 2; } break;
                case 510:
                    if (colorPopupRound == 0)
                    {
                        AssertRed(1f); Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Color Cancel must restore clean data without history");
                        colorPopupRound = 1; stage = 502; wait = 2;
                    }
                    else if (colorPopupRound == 1)
                    {
                        AssertRed(0.25f); if (Click("toolbar/undo")) { stage = 511; wait = 2; }
                    }
                    else if (Click("toolbar/save")) { stage = 514; wait = 2; }
                    break;
                case 511: AssertRed(1f); Assert(!game.Document!.IsDirty, "Color popup edits must undo as one operation");
                    if (Click("toolbar/redo")) { stage = 512; wait = 2; } break;
                case 512: AssertRed(0.25f); if (Click("toolbar/save")) { stage = 514; wait = 2; } break;
                case 514: if (Click("toolbar/reload")) { stage = 515; wait = 2; } break;
                case 515: if (Click("entity/Directional light")) { stage = 516; wait = 2; } break;
                case 516:
                    AssertRed(colorPopupRound == 1 ? 0.25f : 1f);
                    Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Color popup save/reopen must preserve clean state");
                    if (colorPopupRound == 1) { colorPopupRound = 2; stage = 500; wait = 2; }
                    else
                    {
                        checks.Add("Color swatch popup, float channel editing, Cancel, Done, grouped undo/redo and saved/restored reopen");
                        stage = 600; wait = 2;
                    }
                    break;
                case 600:
                    var colorBranch = LightPathControl("Type.Color") + "/expand";
                    if (game.Controls.Expanded.TryGetValue(colorBranch, out var colorExpanded) && !colorExpanded || Click(colorBranch)) { stage = 601; wait = 2; }
                    break;
                case 601: if (ExpandLightPath("Type")) { stage = 602; wait = 2; } break;
                case 602: if (ExpandLightPath("Type.Shadow")) { stage = 603; wait = 2; } break;
                case 603: if (Click(LightPathControl("Type.Shadow.Enabled"))) { stage = 604; wait = 2; } break;
                case 604:
                    AssertShadow(false, Stride.Rendering.Lights.LightShadowMapSize.Large);
                    Assert(game.Document!.IsDirty && game.Document.CanUndo, "Checkbox changes must enter history immediately");
                    if (Click("toolbar/undo")) { stage = 605; wait = 2; } break;
                case 605:
                    AssertShadow(true, Stride.Rendering.Lights.LightShadowMapSize.Large); Assert(!game.Document!.IsDirty, "Shadow checkbox undo must restore clean state");
                    if (Click("toolbar/redo")) { stage = 606; wait = 2; } break;
                case 606: AssertShadow(false, Stride.Rendering.Lights.LightShadowMapSize.Large);
                    if (Click(LightPathControl("Type.Shadow.Size"))) { stage = 607; wait = 2; } break;
                case 607:
                    game.CaptureShadowCheckpoint();
                    if (Click(LightPathControl("Type.Shadow.Size") + "/option/Medium")) { stage = 608; wait = 2; } break;
                case 608:
                    AssertShadow(false, Stride.Rendering.Lights.LightShadowMapSize.Medium);
                    if (Click("toolbar/undo")) { stage = 609; wait = 2; } break;
                case 609:
                    AssertShadow(false, Stride.Rendering.Lights.LightShadowMapSize.Large);
                    Assert(game.Document!.IsDirty, "Undoing the enum must retain the independent checkbox edit");
                    if (Click("toolbar/redo")) { stage = 610; wait = 2; } break;
                case 610: AssertShadow(false, Stride.Rendering.Lights.LightShadowMapSize.Medium);
                    if (Click("toolbar/save")) { stage = 611; wait = 2; } break;
                case 611: if (Click("toolbar/reload")) { stage = 612; wait = 2; } break;
                case 612: if (Click("entity/Directional light")) { stage = 613; wait = 2; } break;
                case 613:
                    AssertShadow(false, Stride.Rendering.Lights.LightShadowMapSize.Medium);
                    Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Shadow boolean/enum save and reopen must be clean");
                    if (Click(LightPathControl("Type.Shadow.Enabled"))) { stage = 614; wait = 2; } break;
                case 614:
                    AssertShadow(true, Stride.Rendering.Lights.LightShadowMapSize.Medium);
                    if (Click(LightPathControl("Type.Shadow.Size"))) { stage = 615; wait = 2; } break;
                case 615: if (Click(LightPathControl("Type.Shadow.Size") + "/option/Large")) { stage = 616; wait = 2; } break;
                case 616: AssertShadow(true, Stride.Rendering.Lights.LightShadowMapSize.Large);
                    if (Click("toolbar/save")) { stage = 617; wait = 2; } break;
                case 617: if (Click("toolbar/reload")) { stage = 618; wait = 2; } break;
                case 618: if (Click("entity/Directional light")) { stage = 619; wait = 2; } break;
                case 619:
                    AssertShadow(true, Stride.Rendering.Lights.LightShadowMapSize.Large);
                    Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Restored shadow settings must reopen cleanly");
                    checks.Add("Nested shadow checkbox and enum dropdown, independent undo/redo, runtime sync, save/reopen and fixture restoration");
                    if (Click("entity/Sphere")) { stage = 422; wait = 2; } break;
                case 422:
                    Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Restored component edit must reopen cleanly");
                    componentWorkflowVerified = true; checks.Add("Quantum component scalar editing, runtime sync, grouped undo/redo and save/reopen");
                    stage = 950; wait = 2; break;
                case 200: io.AddKeyEvent(ImGuiKey.F2, false); stage = 201; wait = 2; break;
                case 201:
                    if (game.Controls.Centers.ContainsKey("rename/name"))
                    { io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 202; wait = 2; }
                    break;
                case 202:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                    foreach (var rune in (renameRound == 0 ? RenamedEntity : "Sphere").EnumerateRunes()) io.AddInputCharacter((uint)rune.Value);
                    stage = 203; wait = 2; break;
                case 203:
                    if (renameRound == 1)
                    { io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.S, true); stage = 216; wait = 2; break; }
                    if (!renameCancelVerified && renameRound == 0)
                    { io.AddKeyEvent(ImGuiKey.Escape, true); stage = 211; wait = 2; break; }
                    if (!renameCloseVerified && renameRound == 0)
                    { game.CaptureRenameCheckpoint(); io.AddMousePosEvent(1100, 760); stage = 213; wait = 2; break; }
                    if (renameRound == 0) { game.CaptureRenameCheckpoint(); io.AddMousePosEvent(1100, 760); }
                    io.AddKeyEvent(ImGuiKey.Enter, true); stage = 204; wait = 2; break;
                case 204: io.AddKeyEvent(ImGuiKey.Enter, false); stage = 205; wait = 2; break;
                case 205:
                    Assert(game.Document?.Selected?.Id == renameId && game.Document.Selected.Name == (renameRound == 0 ? RenamedEntity : "Sphere")
                        && game.SelectedEngineNameMatches, "Rename must preserve selection identity and update the runtime entity name");
                    if (renameRound == 0) { if (Click("toolbar/undo")) { stage = 206; wait = 2; } }
                    else if (Click("toolbar/save")) { stage = 208; wait = 2; }
                    break;
                case 206:
                    Assert(game.Document?.Selected?.Name == "Sphere" && !game.Document.IsDirty, "Rename undo must restore original name and clean state");
                    if (Click("toolbar/redo")) { stage = 207; wait = 2; } break;
                case 207:
                    Assert(game.Document?.Selected?.Name == RenamedEntity && game.Document.IsDirty, "Rename redo must restore the new name");
                    checks.Add("Inline F2 rename, Unicode/literal labels, stable entity identity, undo/redo and runtime synchronization");
                    if (Click("toolbar/save")) { stage = 208; wait = 2; } break;
                case 208:
                    var renamedDisk = AssetFileSerializer.Load<SceneAsset>(game.Document!.Path).Asset;
                    Assert(!game.Document.IsDirty && renamedDisk.Hierarchy.Parts[renameId].Entity.Name == (renameRound == 0 ? RenamedEntity : "Sphere"),
                        "Rename save must persist the name under the original entity ID");
                    if (Click("toolbar/reload")) { stage = 209; wait = 2; } break;
                case 209: if (Click("entity/id/" + renameId)) { stage = 210; wait = 2; } break;
                case 210:
                    Assert(game.Document?.Selected?.Name == (renameRound == 0 ? RenamedEntity : "Sphere") && !game.Document.CanUndo
                        && !game.Document.IsDirty && game.SelectedEngineNameMatches, "Renamed entity must reopen cleanly by ID with its runtime name");
                    if (renameRound == 0)
                    { if (Click("hierarchy/rename")) { renameRound = 1; stage = 201; wait = 2; } }
                    else { renameVerified = true; checks.Add("Entity rename save/reopen and toolbar restoration preserve identity"); stage = 14; wait = 2; }
                    break;
                case 211: io.AddKeyEvent(ImGuiKey.Escape, false); stage = 212; wait = 2; break;
                case 212:
                    Assert(game.Document?.Selected?.Name == "Sphere" && !game.Document.IsDirty && !game.Document.CanUndo,
                        "Escape must cancel the rename draft without creating scene edits or history");
                    if (Click("hierarchy/rename"))
                    { renameCancelVerified = true; checks.Add("Escape cancels inline rename without dirtying the scene"); stage = 201; wait = 2; }
                    break;
                case 213: RequestNativeClose(); stage = 214; wait = 2; break;
                case 214:
                    if (game.ClosePromptOpen && Click("close/cancel")) { stage = 215; wait = 2; } break;
                case 215:
                    Assert(!game.IsExiting && game.Document?.Selected?.Name == RenamedEntity && game.Document.IsDirty && game.SelectedEngineNameMatches,
                        "Closing with a pending name draft must commit it, prompt, and preserve it when closing is cancelled");
                    renameCloseVerified = true; checks.Add("Native close during inline rename preserves the draft through the unsaved prompt");
                    stage = 205; wait = 2; break;
                case 216:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.S, false);
                    checks.Add("Ctrl+S commits and saves an active inline name draft"); stage = 208; wait = 2; break;
                case 100:
                    Assert(game.SceneCameraView != previousCamera, "Focus selected must change the actual engine camera view");
                    previousCamera = game.SceneCameraView;
                    if (game.Controls.Centers.TryGetValue("scene/image", out var scenePoint))
                    { io.AddMousePosEvent(scenePoint.X, scenePoint.Y); io.AddMouseWheelEvent(0, 1); stage = 101; wait = 2; }
                    break;
                case 101:
                    Assert(game.SceneCameraView != previousCamera, "Wheel dolly over the viewport must move the engine camera");
                    previousCamera = game.SceneCameraView; io.AddMouseButtonEvent(1, true); stage = 102; wait = 2; break;
                case 102:
                    var lookPoint = game.Controls.Centers["scene/image"];
                    io.AddMousePosEvent(lookPoint.X + 25, lookPoint.Y + 12); stage = 103; wait = 2; break;
                case 103: io.AddMouseButtonEvent(1, false); stage = 104; wait = 2; break;
                case 104:
                    Assert(game.SceneCameraView != previousCamera, "Right-drag look must rotate the engine camera");
                    if (Click("camera/reset")) { stage = 105; wait = 2; } break;
                case 105:
                    Assert(game.SceneCameraView == initialCamera, "Reset must restore the editor camera's default view");
                    Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Camera navigation must not edit scene data or history");
                    checks.Add("Engine camera focus, wheel dolly, right-drag look and reset preserve document state");
                    previousCamera = game.SceneCameraView;
                    var panPoint = game.Controls.Centers["scene/image"];
                    io.AddMousePosEvent(panPoint.X, panPoint.Y); io.AddMouseButtonEvent(2, true); stage = 106; wait = 2; break;
                case 106:
                    var panMove = game.Controls.Centers["scene/image"];
                    io.AddMousePosEvent(panMove.X + 30, panMove.Y - 15); stage = 107; wait = 2; break;
                case 107: io.AddMouseButtonEvent(2, false); stage = 108; wait = 2; break;
                case 108:
                    Assert(game.SceneCameraView != previousCamera, "Middle-drag pan must move the engine view");
                    previousCamera = game.SceneCameraView;
                    var orbitPoint = game.Controls.Centers["scene/image"];
                    io.AddMousePosEvent(orbitPoint.X, orbitPoint.Y); io.AddKeyEvent(ImGuiKey.ModAlt, true); io.AddMouseButtonEvent(0, true);
                    stage = 109; wait = 2; break;
                case 109:
                    var orbitMove = game.Controls.Centers["scene/image"];
                    io.AddMousePosEvent(orbitMove.X + 25, orbitMove.Y + 10); stage = 110; wait = 2; break;
                case 110: io.AddKeyEvent(ImGuiKey.ModAlt, false); io.AddMouseButtonEvent(0, false); stage = 111; wait = 2; break;
                case 111:
                    Assert(game.SceneCameraView != previousCamera, "Alt-left-drag orbit must change the engine view");
                    previousCamera = game.SceneCameraView;
                    io.AddMouseButtonEvent(1, true); io.AddKeyEvent(ImGuiKey.W, true); stage = 112; wait = 2; break;
                case 112: io.AddMouseButtonEvent(1, false); io.AddKeyEvent(ImGuiKey.W, false); stage = 113; wait = 2; break;
                case 113:
                    Assert(game.SceneCameraView != previousCamera, "W while navigating must move the engine camera");
                    if (Click("camera/reset")) { stage = 114; wait = 2; } break;
                case 114:
                    Assert(game.SceneCameraView == initialCamera && !game.Document!.IsDirty && !game.Document.CanUndo,
                        "Pan/orbit/fly and reset must leave scene data and history unchanged");
                    checks.Add("Engine camera middle-drag pan, Alt-left orbit and navigation-key movement");
                    previousCamera = game.SceneCameraView; io.AddMousePosEvent(1100, 760); io.AddMouseWheelEvent(0, 1);
                    stage = 115; wait = 2; break;
                case 115:
                    Assert(game.SceneCameraView == previousCamera, "Wheel input outside the scene viewport must not move its camera");
                    checks.Add("Camera wheel input is scoped to the scene viewport");
                    game.CaptureSceneCheckpoint(); game.SceneDrawMeasurements.Begin(); stage = 116; wait = 0; break;
                case 116:
                    if (game.SceneDrawMeasurements.Count >= 60)
                    { visibleSceneDraw = game.SceneDrawMeasurements.End(); Assert(visibleSceneDraw.DrawCalls > 0, "Visible scene must draw"); stage = 15; wait = 2; }
                    break;
                case 15: if (Click("asset/filter")) Next(); break;
                case 16: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
                case 17:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                    foreach (var character in "Skybox texture") io.AddInputCharacter(character);
                    Next(); break;
                case 18: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
                case 19: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
                case 20: if (Click("asset/Skybox texture")) Next(); break;
                case 21:
                    if (game.TexturePreview is { RenderCount: > 0 })
                    { game.SceneDrawMeasurements.Begin(); stage = 117; wait = 0; }
                    break;
                case 117:
                    if (game.SceneDrawMeasurements.Count >= 60)
                    {
                        hiddenSceneDraw = game.SceneDrawMeasurements.End();
                        Assert(game.DiagnosticRenderHiddenScenes ? hiddenSceneDraw.DrawCalls > 0 : hiddenSceneDraw.DrawCalls == 0,
                            "Hidden scene drawing must follow the selected diagnostic/default policy");
                        checks.Add(game.DiagnosticRenderHiddenScenes ? "Diagnostic baseline keeps hidden scene drawing" : "Hidden scene tab submits zero scene draw calls");
                        if (Click("preview/actual-size")) { checks.Add("Select real compiled texture and render through shared WPF/ImGui preview renderer"); stage = 22; wait = 2; }
                    }
                    break;
                case 22:
                    Assert(game.TexturePreview?.Scale == 1, "Actual size must set 1:1 scale");
                    if (Click("preview/zoom-in")) Next(); break;
                case 23:
                    Assert(game.TexturePreview?.Scale == 1.5f, "Zoom in must follow the reference zoom factors");
                    if (Click("preview/zoom-out")) Next(); break;
                case 24:
                    Assert(game.TexturePreview?.Scale == 1, "Zoom out must restore 1:1");
                    checks.Add("Texture preview actual-size and zoom controls");
                    if (Click("preview/mip")) Next(); break;
                case 25: if (Click("preview/mip/1")) Next(); break;
                case 26:
                    Assert(game.TexturePreview?.Mip == 1 && game.TexturePreview.RenderCount > 1, "Mip selection must redraw the engine preview");
                    checks.Add("Texture preview mip selection"); if (Click("preview/fit")) Next(); break;
                case 27:
                    Assert(game.TexturePreview is { Scale: > 0 } && game.TexturePreview.Offsets == Stride.Core.Mathematics.Vector2.Zero,
                        "Fit must set a valid centered preview scale");
                    Assert(!game.Document!.IsDirty && !game.Document.CanUndo, "Preview navigation must not edit the scene");
                    checks.Add("Texture preview fit and document isolation");
                    if (Click("preview/cube")) Next(); break;
                case 28: if (Click("preview/cube/0")) Next(); break;
                case 29:
                    Assert(game.TexturePreview?.CubeMode == 0, "Cube preview must select the right face");
                    if (Click("preview/cube")) Next(); break;
                case 30: if (Click("preview/cube/6")) Next(); break;
                case 31:
                    Assert(game.TexturePreview?.CubeMode == 6, "Cube preview must restore the full cross");
                    checks.Add("Texture cube face and full-cross selection"); if (Click("preview/fit")) Next(); break;
                case 32: io.AddMousePosEvent(1100, 760); Next(); break;
                case 33:
                    Assert(!game.Document!.IsDirty, "Cube navigation must not edit the scene");
                    if (Click("asset/Skybox texture")) Next(); break;
                case 34:
                    if (game.TexturePreview is { RenderCount: > 0, Mip: 0 })
                    {
                        checks.Add("Reload texture preview and dispose the prior GPU/content resources");
                        previousCamera = game.SceneCameraView;
                        if (Click("menu/view")) { stage = 118; wait = 2; }
                    }
                    break;
                case 118: if (Click("menu/view/scene")) { stage = 119; wait = 2; } break;
                case 119:
                    if (game.EngineDrawCalls > 0)
                    {
                        Assert(game.SceneCameraView == previousCamera && !game.Document!.IsDirty, "Resuming scene drawing must preserve camera and document state");
                        if (Click("asset/Skybox texture")) { stage = 120; wait = 2; }
                    }
                    break;
                case 120:
                    if (game.TexturePreview is { RenderCount: > 0 } && (game.DiagnosticRenderHiddenScenes ? game.EngineDrawCalls > 0 : game.EngineDrawCalls == 0))
                    { checks.Add("Scene tab resumes rendering and pauses again without changing camera/document state"); StartPreviewVisibilityCheck(game.ImportedModelEvidence.FixturePresent ? 700 : 750); }
                    break;
                case 700: if (Click("asset/filter")) { stage = 701; wait = 2; } break;
                case 701: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 702; wait = 2; break;
                case 702:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                    foreach (var character in "Imported cube") io.AddInputCharacter(character); stage = 703; wait = 2; break;
                case 703: io.AddKeyEvent(ImGuiKey.Enter, true); stage = 704; wait = 2; break;
                case 704: io.AddKeyEvent(ImGuiKey.Enter, false); stage = 705; wait = 2; break;
                case 705: if (Click("asset/Imported cube")) { stage = 706; wait = 2; } break;
                case 706:
                    if (game.ModelPreview is { RenderCount: > 10, DrawCalls: > 0, RenderAgeSeconds: >= 5 } modelAsset)
                    {
                        Assert(modelAsset.MeshCount > 0 && modelAsset.VertexBytes > 0 && modelAsset.MaterialPassCount > 0 && game.TexturePreview == null,
                            "Imported model preview must use real GPU meshes/materials and replace the texture preview");
                        Assert(modelAsset.ModelFitsCamera, "Initial model preview must fit its actual world-space bounds inside the engine camera frustum");
                        modelInitialCamera = modelPreviousCamera = modelAsset.CameraMatrix; modelSceneCamera = game.SceneCameraView;
                        modelPreviewEvidence = new { assetUrl = modelAsset.Name, meshes = modelAsset.MeshCount, vertexBytes = modelAsset.VertexBytes,
                            materialPasses = modelAsset.MaterialPassCount, drawCalls = modelAsset.DrawCalls,
                            framingRadius = modelAsset.FramingBounds.Radius, initialDistance = modelAsset.Camera.Distance,
                            renderedWarmupSeconds = modelAsset.RenderAgeSeconds, minimumHdrWarmupSeconds = 5 };
                        var modelPoint = game.Controls.Centers["model-preview/image"];
                        io.AddMousePosEvent(modelPoint.X, modelPoint.Y); io.AddMouseWheelEvent(0, 1); stage = 707; wait = 2;
                    }
                    break;
                case 707:
                    Assert(game.ModelPreview!.CameraMatrix != modelPreviousCamera, "Model preview wheel must dolly the actual engine camera");
                    modelPreviousCamera = game.ModelPreview.CameraMatrix;
                    var modelOrbit = game.Controls.Centers["model-preview/image"];
                    io.AddMousePosEvent(modelOrbit.X, modelOrbit.Y); io.AddMouseButtonEvent(0, true); stage = 708; wait = 2; break;
                case 708:
                    var modelOrbitMove = game.Controls.Centers["model-preview/image"];
                    io.AddMousePosEvent(modelOrbitMove.X + 50, modelOrbitMove.Y + 20); stage = 709; wait = 2; break;
                case 709: io.AddMouseButtonEvent(0, false); stage = 710; wait = 2; break;
                case 710:
                    Assert(game.ModelPreview!.CameraMatrix != modelPreviousCamera, "Model preview left drag must orbit its engine camera");
                    Assert(game.ModelPreview.ModelFitsCamera, "The fixture's dolly/orbit checkpoint must keep the full model visible");
                    io.AddMousePosEvent(1100, 760); stage = 711; wait = 2; break;
                case 711: game.CaptureModelPreviewCheckpoint(); if (Click("model-preview/reset")) { stage = 712; wait = 2; } break;
                case 712:
                    Assert(game.ModelPreview!.CameraMatrix == modelInitialCamera, "Model reset must restore the shared WPF framing/default angle");
                    modelPreviousCamera = game.ModelPreview.CameraMatrix;
                    var modelPan = game.Controls.Centers["model-preview/image"];
                    io.AddMousePosEvent(modelPan.X, modelPan.Y); io.AddMouseButtonEvent(1, true); stage = 713; wait = 2; break;
                case 713:
                    var modelPanMove = game.Controls.Centers["model-preview/image"];
                    io.AddMousePosEvent(modelPanMove.X + 35, modelPanMove.Y + 10); stage = 714; wait = 2; break;
                case 714: io.AddMouseButtonEvent(1, false); stage = 715; wait = 2; break;
                case 715:
                    Assert(game.ModelPreview!.CameraMatrix != modelPreviousCamera, "Model right drag must pan its camera");
                    if (Click("model-preview/reset")) { stage = 716; wait = 2; } break;
                case 716:
                    Assert(game.ModelPreview!.CameraMatrix == modelInitialCamera && game.SceneCameraView == modelSceneCamera
                        && !game.Document!.IsDirty && !game.Document.CanUndo, "Model navigation/reset must preserve authored scene data, history and scene camera");
                    StartPreviewVisibilityCheck(762); break;
                case 762:
                    priorModelPreview = game.ModelPreview;
                    if (Click("asset/Imported cube")) { stage = 717; wait = 2; } break;
                case 717:
                    if (game.ModelPreview is { RenderCount: > 0 })
                    {
                        Assert(priorModelPreview!.IsDisposed && !ReferenceEquals(priorModelPreview, game.ModelPreview), "Model reopen must dispose the previous scene/GPU/content resources");
                        priorModelPreview = game.ModelPreview;
                        if (Click("asset/filter")) { stage = 718; wait = 2; }
                    }
                    break;
                case 718: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 719; wait = 2; break;
                case 719:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                    foreach (var character in "Skybox texture") io.AddInputCharacter(character); stage = 720; wait = 2; break;
                case 720: io.AddKeyEvent(ImGuiKey.Enter, true); stage = 721; wait = 2; break;
                case 721: io.AddKeyEvent(ImGuiKey.Enter, false); stage = 722; wait = 2; break;
                case 722: if (Click("asset/Skybox texture")) { stage = 723; wait = 2; } break;
                case 723:
                    if (game.TexturePreview is { RenderCount: > 0 })
                    {
                        Assert(game.ModelPreview == null && priorModelPreview!.IsDisposed && game.SceneCameraView == modelSceneCamera && !game.Document!.IsDirty,
                            "Switching back to textures must dispose the model preview and preserve the scene");
                        if (game.ImportedModelEvidence.FixturePresent && !importedPreviewVerified)
                        {
                            checks.Add("Compiled imported model asset preview, GPU meshes/materials, shared WPF camera/lighting, orbit/pan/dolly/reset, reopen/disposal and texture switching");
                            importedPreviewVerified = true;
                        }
                        priorModelPreview = null; stage = proceduralWorkflowVerified ? assetEditorWorkflowVerified ? 35 : 1100 : 750; wait = 2;
                    }
                    break;
                case 750:
                    if (proceduralPreviewRound == 0) modelSceneCamera = game.SceneCameraView;
                    if (Click("asset/filter")) { stage = 751; wait = 2; } break;
                case >= 800 and <= 813: UpdatePreviewVisibilityCheck(); break;
                case >= 900 and <= 926: UpdateVectorWorkflow(); break;
                case >= 950 and <= 979: UpdateNumericRangeWorkflow(); break;
                case >= 1100 and <= 1142: UpdateAssetEditorWorkflow(); break;
                case 751: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); stage = 752; wait = 2; break;
                case 752:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                    foreach (var character in proceduralPreviewRound == 0 ? "Sphere" : "Ground") io.AddInputCharacter(character);
                    stage = 753; wait = 2; break;
                case 753: io.AddKeyEvent(ImGuiKey.Enter, true); stage = 754; wait = 2; break;
                case 754: io.AddKeyEvent(ImGuiKey.Enter, false); stage = 755; wait = 2; break;
                case 755: if (Click("asset/" + (proceduralPreviewRound == 0 ? "Sphere" : "Ground"))) { stage = 756; wait = 2; } break;
                case 756:
                    if (game.ModelPreview is { IsProcedural: true, RenderCount: > 10, DrawCalls: > 0, RenderAgeSeconds: >= 5 } proceduralAsset)
                    {
                        var expectedName = proceduralPreviewRound == 0 ? "Sphere" : "Ground";
                        Assert(proceduralAsset.Name.EndsWith("/" + expectedName, StringComparison.Ordinal) && proceduralAsset.MeshCount > 0
                            && proceduralAsset.VertexBytes > 0 && proceduralAsset.MaterialPassCount > 0 && proceduralAsset.ModelFitsCamera,
                            "Procedural preview must load the expected compiled GPU model/material and frame its world bounds");
                        Assert(game.SelectedAssetPath!.EndsWith(expectedName + ".sdpromodel", StringComparison.OrdinalIgnoreCase), "The previewed procedural asset must remain selected in the browser");
                        if (priorModelPreview != null) Assert(priorModelPreview.IsDisposed, "Changing procedural assets must dispose the previous model preview");
                        modelInitialCamera = proceduralAsset.CameraMatrix;
                        if (proceduralPreviewRound == 0)
                        {
                            RecordProceduralPreview(proceduralAsset);
                            io.AddMousePosEvent(1100, 760); stage = 757; wait = 2;
                        }
                        else if (Click("model-preview/backdrop")) { stage = 760; wait = 2; }
                    }
                    break;
                case 760: if (Click("model-preview/backdrop/Light")) { stage = 761; wait = 2; } break;
                case 761:
                    if (game.ModelPreview is { RenderAgeSeconds: >= 5 } contrastedPlane)
                    {
                        Assert(contrastedPlane.Backdrop == ModelPreviewBackdrop.Light && contrastedPlane.ModelFitsCamera
                            && !game.Document!.IsDirty && !game.Document.CanUndo, "Backdrop changes must affect only the preview compositor and retain framing/document state");
                        RecordProceduralPreview(contrastedPlane);
                        io.AddMousePosEvent(1100, 760); stage = 757; wait = 2;
                    }
                    break;
                case 757:
                    game.CaptureProceduralPreviewCheckpoint(proceduralPreviewRound != 0);
                    var proceduralPoint = game.Controls.Centers["model-preview/image"];
                    io.AddMousePosEvent(proceduralPoint.X, proceduralPoint.Y); io.AddMouseWheelEvent(0, 1); stage = 758; wait = 2; break;
                case 758:
                    Assert(game.ModelPreview!.CameraMatrix != modelInitialCamera, "Procedural preview wheel must move the actual engine camera");
                    if (Click("model-preview/reset")) { stage = 759; wait = 2; } break;
                case 759:
                    Assert(game.ModelPreview!.CameraMatrix == modelInitialCamera && game.ModelPreview.ModelFitsCamera && game.SceneCameraView == modelSceneCamera
                        && !game.Document!.IsDirty && !game.Document.CanUndo, "Procedural reset must restore framing and preserve authored scene/camera/history");
                    priorModelPreview = game.ModelPreview;
                    if (proceduralPreviewRound == 0) { proceduralPreviewRound = 1; stage = 750; wait = 2; }
                    else
                    {
                        proceduralWorkflowVerified = true;
                        checks.Add("Compiled procedural sphere and plane previews, GPU meshes/materials, browser selection, framing, dolly/reset, scene isolation and preview disposal");
                        if (Click("asset/filter")) { stage = 718; wait = 2; }
                    }
                    break;
                case 35:
                    preservedDocument = game.Document; preservedPreview = game.TexturePreview;
                    if (Click("toolbar/open")) Next(); break;
                case 36: if (Click("project/open")) Next(); break;
                case 37:
                    if (game.ProjectBuildProcessId is { } replacementProcess && Click("loading/cancel"))
                    { cancelledCompiler = replacementProcess; Next(); }
                    break;
                case 38:
                    if (!game.IsProjectLoading)
                    {
                        Assert(ProcessExited(cancelledCompiler), "Replacement compiler must terminate after cancellation");
                        Assert(ReferenceEquals(game.Document, preservedDocument) && ReferenceEquals(game.TexturePreview, preservedPreview),
                            "Cancelling a replacement must preserve the existing document and preview");
                        AssertPosition(2.5f);
                        checks.Add("Cancel replacement project and preserve current scene/preview");
                        io.AddMousePosEvent(1100, 760); Next();
                    }
                    break;
                case 39:
                    if (Click("Position/0")) Next(); break;
                case 40: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
                case 41:
                    io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                    foreach (var character in "3.5") io.AddInputCharacter(character);
                    Next(); break;
                case 42: io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
                case 43: io.AddKeyEvent(ImGuiKey.Enter, false); Next(); break;
                case 44: if (Click("entity/Sphere")) Next(); break;
                case 45:
                    AssertPosition(3.5f); Assert(game.Document!.IsDirty, "Close test requires an unsaved edit");
                    RequestNativeClose(); Next(); break;
                case 46:
                    if (game.ClosePromptOpen) { game.CaptureCloseCheckpoint(); Next(); } break;
                case 47: if (Click("close/cancel")) Next(); break;
                case 48:
                    Assert(!game.ClosePromptOpen && !game.IsExiting && game.Document!.IsDirty, "Cancel close must keep unsaved work open");
                    AssertPosition(3.5f); checks.Add("Native window close prompts and Cancel preserves unsaved edits");
                    RequestNativeClose(); Next(); break;
                case 49:
                    if (game.ClosePromptOpen)
                    {
                        if (discardClose) { if (Click("close/discard")) Next(); break; }
                        closeDiskBackup = File.ReadAllBytes(game.Document!.Path);
                        File.AppendAllText(game.Document.Path, "\n# External fixture edit during close\n");
                        if (Click("close/save")) Next();
                    }
                    break;
                case 50:
                    Assert(game.ClosePromptOpen && !game.IsExiting && game.Document!.IsDirty, "Save failure must keep editor and close prompt open");
                    AssertPosition(3.5f); checks.Add("Save failure during close preserves dirty scene and permits retry");
                    File.WriteAllBytes(game.Document.Path, closeDiskBackup!);
                    if (Click("close/save")) Next(); break;
                case 51: break; // Save/close approval is observed by VerifyApprovedClose before shutdown.
            }
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }
    private bool Click(string id, int button = 0)
    {
        if (!game.Controls.Centers.TryGetValue(id, out var point))
        {
            lastMissingControl = id;
            if (id.StartsWith("Position/", StringComparison.Ordinal) || id.StartsWith("component/", StringComparison.Ordinal))
                TryClickToolTab("editor.properties");
            else if (id.StartsWith("asset/", StringComparison.Ordinal))
                TryClickToolTab("assets.browser");
            else pendingClickId = null;
            return false;
        }
        var io = Gui.GetIO();
        // NewFrame resolves hover/window ownership before processing button
        // activation. Moving and pressing in one Update can activate the prior
        // hovered surface when project publication changed the layout.
        if (pendingClickId != id || pendingClickPoint != point || pendingClickButton != button || System.Numerics.Vector2.DistanceSquared(io.MousePos, point) > 2f)
        {
            pendingClickId = id; pendingClickPoint = point; pendingClickButton = button;
            io.AddMousePosEvent(point.X, point.Y);
            RecordAction("aim", id, point.X, point.Y, io.MousePos.X, io.MousePos.Y);
            return false;
        }
        pendingClickId = null;
        io.AddMouseButtonEvent(button, true);
        mousePressFrame = Gui.GetFrameCount();
        lastSubmittedControl = id;
        RecordAction(button == 1 ? "right-click" : "click", id);
        releaseMouseButton = button; releaseMouse = true; return true;
    }
    private void Next()
    {
        File.AppendAllText(WorkflowTracePath, $"Stage {stage} complete: {game.StatusMessage}\n");
        Console.WriteLine($"UI stage {stage} complete: {game.StatusMessage}"); stage++; wait = 2;
    }
    private void AssertPosition(float expected)
    {
        Assert(game.Document?.Selected != null && Math.Abs(game.Document.Selected.Transform.Position.X - expected) < 0.0001f,
            $"Expected position X={expected}, actual={game.Document?.Selected?.Transform.Position.X}");
        Assert(game.SelectedEngineTransformMatches, "The compiled runtime entity must match the document transform by entity identity");
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private ComponentField LightField => game.Document!.GetComponentFields(game.Document.Selected!.Get<Stride.Engine.LightComponent>()!)
        .Single(entry => entry.Name == nameof(Stride.Engine.LightComponent.Intensity));
    private string LightFieldControl => "component/" + LightField.Component.Id + "/" + LightField.Name;
    private string LightPathControl(string path) => "component/" + LightField.Component.Id + "/" + path;
    private void RecordProceduralPreview(ModelAssetPreview preview) => proceduralPreviewEvidence.Add(new
    {
        assetUrl = preview.Name, meshes = preview.MeshCount, vertexBytes = preview.VertexBytes, materialPasses = preview.MaterialPassCount,
        drawCalls = preview.DrawCalls, framingRadius = preview.FramingBounds.Radius, backdrop = preview.Backdrop.ToString(), renderedWarmupSeconds = preview.RenderAgeSeconds,
    });
    private void AssertShadow(bool enabled, Stride.Rendering.Lights.LightShadowMapSize size)
    {
        var fields = SceneDocument.Flatten(game.Document!.GetComponentFields(LightField.Component)).ToArray();
        var enabledField = fields.Single(field => field.Name == "Type.Shadow.Enabled");
        var sizeField = fields.Single(field => field.Name == "Type.Shadow.Size");
        Assert(Equals(enabledField.Node.Retrieve(), enabled) && Equals(sizeField.Node.Retrieve(), size)
            && game.RuntimeComponentMatches(enabledField) && game.RuntimeComponentMatches(sizeField), "Shadow settings must match authored and runtime light values");
    }
    private bool ExpandLightPath(string path)
    {
        var control = LightPathControl(path) + "/expand";
        return game.Controls.Expanded.TryGetValue(control, out var open) && open || Click(control);
    }
    private void AssertRed(float expected)
    {
        var fields = SceneDocument.Flatten(game.Document!.GetComponentFields(LightField.Component)).ToArray();
        var red = fields.Single(field => field.Name == "Type.Color.Value.R");
        Assert(Equals(red.Node.Retrieve(), expected) && game.RuntimeComponentMatches(red), "Nested red channel must match the authored and engine values");
        Assert(Equals(fields.Single(field => field.Name == "Type.Color.Value.G").Node.Retrieve(), 1f)
            && Equals(fields.Single(field => field.Name == "Type.Color.Value.B").Node.Retrieve(), 1f), "Nested red edit must preserve the other color channels");
    }
    private void AssertLight(float expected)
    {
        var field = LightField;
        var value = field.Node.Retrieve();
        var runtimeMatches = game.RuntimeComponentMatches(field);
        Assert(Equals(value, expected) && runtimeMatches,
            $"Light intensity must equal {expected}; authored={value}, runtimeMatchesAuthored={runtimeMatches}, "
            + $"entity={field.Component.Entity.Id}, component={field.Component.Id}, field={field.Name}");
    }
    private void VerifyApprovedClose()
    {
        try
        {
            var approved = approvedCloseState ?? throw new InvalidOperationException("Native close approval did not preserve its pre-teardown state.");
            Assert(scenario == UiWorkflowScenario.U05DocumentClose ? approved.Stage is 10013 or 10014 : approved.Stage is 50 or 51,
                "Editor must close only after the final approved close action");
            Assert(approved.WasDirty == discardClose, "Close action must preserve correct save/discard dirty state");
            Assert(approved.SphereX == 3.5f, "Close approval must preserve the authored UI edit before retiring the graph");
            var disk = AssetFileSerializer.Load<SceneAsset>(approved.Path).Asset;
            Assert(disk.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity.Transform.Position.X ==
                (scenario == UiWorkflowScenario.U05DocumentClose ? discardClose ? independentOriginalX : 3.5f : discardClose ? 2.5f : 3.5f),
                "Approved close must persist the UI edit only when Save was selected");
            checks.Add(discardClose ? "Discard and close leaves the saved scene unchanged" : "Save and close persists the scene before approved shutdown");
            Passed = Finished = true;
            Result = "UI E2E PASS: " + string.Join("; ", checks) + ". Fixture copy: " + approved.Path;
            if (scenario != UiWorkflowScenario.Legacy) Result += ". " + ScenarioScope;
        }
        catch (Exception error) { Fail(error); }
    }
    private ApprovedCloseState? approvedCloseState;
    private void CaptureApprovedCloseState()
    {
        var source = game.Document ?? throw new InvalidOperationException("Native close approval has no authored scene.");
        approvedCloseState = new(source.Path, source.IsDirty,
            source.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Sphere").Entity.Transform.Position.X, stage);
    }
    private sealed record ApprovedCloseState(string Path, bool WasDirty, float SphereX, int Stage);
    private void RequestNativeClose()
    {
        RecordAction("native-close", "WM_CLOSE");
        Assert(PostMessage(game.Window.NativeWindow.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero), "Could not dispatch WM_CLOSE to the editor window");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    private static bool ProcessExited(int id)
    {
        try { using var process = Process.GetProcessById(id); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
}
