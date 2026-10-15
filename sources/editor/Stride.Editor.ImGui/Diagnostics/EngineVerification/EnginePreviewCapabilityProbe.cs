using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Independent E03 texture/model cases: UI controls, actual compiled resources and displayed GPU output.</summary>
internal sealed class EnginePreviewCapabilityProbe : EngineUiCapabilityProbe
{
    private readonly bool model;
    private SceneDocument? scene;
    private Matrix sceneCamera, previewCamera;
    private EngineOutputSample? baseline, last;
    private EngineOutputDifference? changed, restored;
    private int stable, renderCount;
    private bool textureChangeVerified;
    private PreviewToneMapVerification? exposureFixture;
    private int exposureConfiguredAtRender;
    private double nextSample;
    protected override string Scenario => model ? "E03.model" : "E03.texture";
    protected override object Evidence => new { changed, restored, exposureFixture, actualModel = model ? Game.ModelPreview?.Name : null,
        actualTexture = model ? null : Game.TexturePreview?.Name,
        actualRenderCount = model ? Game.ModelPreview?.RenderCount : Game.TexturePreview?.RenderCount,
        actualImageControlVisible = Game.Controls.Centers.ContainsKey(model ? "model-preview/image" : "preview/image"),
        gaps = new[] { "Animation, sprite and font have separate cases; other asset variations are outside this case." },
        tolerance = new { rgb = 3, stableFraction = .001, restoredFraction = .005, needsBaselineReview = true } };
    internal EnginePreviewCapabilityProbe(EditorGame game, string artifacts, bool model, bool alreadyLoaded = false) : base(game, artifacts)
    { this.model = model; if (alreadyLoaded) SeekPhase(2); }

    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0: if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (scene == null)
                {
                    if (Game.Document == null || !EnsureScenePresented()) break;
                    scene = Game.Document; sceneCamera = Game.VerificationSceneCamera;
                }
                if (Click("asset/filter")) Next(); break;
            case 3: io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); Next(); break;
            case 4:
                io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
                foreach (var c in model ? "Imported cube" : "Skybox texture") io.AddInputCharacter(c);
                Next(); break;
            case 5: io.AddKeyEvent(ImGuiKey.ModShift, true); io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 6: io.AddKeyEvent(ImGuiKey.Enter, false); io.AddKeyEvent(ImGuiKey.ModShift, false); Next(); break;
            case 7: if (Click(model ? "asset/Imported cube" : "asset/Skybox texture")) Next(); break;
            case 8:
                AssertSceneUnchanged();
                if (!Game.Controls.Centers.ContainsKey(model ? "model-preview/image" : "preview/image"))
                {
                    Click("tooltab/assets.preview");
                    break;
                }
                if (model)
                {
                    if (Game.ModelPreview is not { RenderCount: > 0 } preview) break;
                    Require(!preview.IsDisposed && preview.RenderError == null && preview.SourceContent != null,
                        "Model preview must borrow actual compiled project content without render errors.");
                    Require(preview.MeshCount > 0 && preview.VertexBytes > 0 && preview.MaterialPassCount > 0 && preview.ModelFitsCamera,
                        "Real compiled model GPU meshes/materials must fit the preview camera.");
                    if (exposureFixture == null)
                    {
                        exposureFixture = preview.ConfigureVerificationExposure();
                        exposureConfiguredAtRender = preview.RenderCount;
                        ResetSampling(); break;
                    }
                    if (preview.RenderCount <= exposureConfiguredAtRender) break;
                    if (!StableOutput(out var sample)) break;
                    baseline = sample; previewCamera = preview.CameraMatrix; renderCount = preview.RenderCount;
                    Save("baseline");
                    if (!Game.Controls.Centers.TryGetValue("model-preview/image", out var point)) break;
                    if (!Aim("model-preview/image-wheel", point)) break;
                    io.AddMouseWheelEvent(0, 1); Next();
                }
                else
                {
                    if (Game.TexturePreview is not { RenderCount: > 0 } texture) break;
                    Require(!texture.IsDisposed && texture.RenderError == null && texture.Dimension == TextureDimension.TextureCube
                        && texture.Width > 0 && texture.Height > 0 && texture.MipCount > 1,
                        "The compiled fixture must provide a real cubemap with mip levels.");
                    if (Click("preview/actual-size")) Next();
                }
                break;
            case 9:
                AssertSceneUnchanged();
                if (model)
                {
                    var preview = Game.ModelPreview!;
                    if (preview.RenderCount <= renderCount || preview.CameraMatrix == previewCamera) break;
                    if (!StableOutput(out var sample)) break;
                    AssertVisibleChange(sample); Save("changed");
                    if (Click("model-preview/reset")) { ResetSampling(); Next(); }
                }
                else
                {
                    var preview = Game.TexturePreview!;
                    if (preview.Scale != 1 || preview.RenderCount <= 0) break;
                    baseline = Game.ReadPreviewVerificationOutput(false); renderCount = preview.RenderCount;
                    Save("baseline");
                    if (Click("preview/zoom-in")) Next();
                }
                break;
            case 10:
                AssertSceneUnchanged();
                if (model)
                {
                    Require(Game.ModelPreview!.RenderError == null, "Model reset failed to render.");
                    if (Game.ModelPreview.CameraMatrix != previewCamera || !StableOutput(out var sample)) break;
                    if (!AssertRestored(sample)) break;
                    Save("restored"); Finish(true, "E03 model PASS: real compiled meshes/materials, UI camera dolly changes displayed GPU output; reset restores camera/output and preserves authored scene.");
                }
                else
                {
                    var preview = Game.TexturePreview!;
                    if (!textureChangeVerified)
                    {
                        if (preview.Scale != 1.5f || preview.RenderCount <= renderCount) break;
                        Require(preview.RenderError == null, "Texture zoom failed to render.");
                        AssertVisibleChange(Game.ReadPreviewVerificationOutput(false)); Save("changed");
                        renderCount = preview.RenderCount;
                        textureChangeVerified = true;
                    }
                    if (Click("preview/zoom-out")) Next();
                }
                break;
            case 11:
                AssertSceneUnchanged();
                var textureAfter = Game.TexturePreview!;
                if (textureAfter.Scale != 1 || textureAfter.RenderCount <= renderCount) break;
                Require(textureAfter.RenderError == null, "Texture zoom restoration failed to render.");
                if (!AssertRestored(Game.ReadPreviewVerificationOutput(false))) break;
                Save("restored"); Finish(true, "E03 texture PASS: compiled cubemap/mips, UI zoom changes actual displayed GPU output; reverse zoom restores output and preserves authored scene.");
                break;
        }
    }
    protected override void CleanInput() => Gui.GetIO().AddKeyEvent(ImGuiKey.ModShift, false);
    private void AssertSceneUnchanged()
        => Require(ReferenceEquals(Game.Document, scene) && scene != null && !scene.IsDirty && !scene.CanUndo
            && Game.VerificationSceneCamera == sceneCamera, "Preview controls must retain the scene document, clean history and its independent camera.");
    private bool StableOutput(out EngineOutputSample sample)
    {
        sample = null!;
        if (Clock.Elapsed.TotalSeconds < nextSample) return false;
        nextSample = Clock.Elapsed.TotalSeconds + .25;
        sample = Game.ReadPreviewVerificationOutput(model);
        var isStable = last != null && last.Width == sample.Width && last.Height == sample.Height && last.Format == sample.Format
            && last.Compare(sample, 0, 0, sample.Width, sample.Height).ChangedFraction <= .001;
        last = sample; stable = isStable ? stable + 1 : 0; return stable >= 3;
    }
    private void ResetSampling() { last = null; stable = 0; }
    private void AssertVisibleChange(EngineOutputSample sample)
    {
        changed = baseline!.Compare(sample, 0, 0, baseline.Width, baseline.Height);
        EngineCapabilityAssertions.VisibleFixtureChange(changed.Value, Math.Max(16, changed.Value.Pixels / 1000), .99);
    }
    private bool AssertRestored(EngineOutputSample sample)
    { restored = baseline!.Compare(sample, 0, 0, baseline.Width, baseline.Height); return restored.Value.ChangedFraction <= .005; }
    private void Save(string suffix) => Game.SavePreviewVerificationOutput(model, Path.Combine(Artifacts, Scenario + "-" + suffix + ".png"));
}
