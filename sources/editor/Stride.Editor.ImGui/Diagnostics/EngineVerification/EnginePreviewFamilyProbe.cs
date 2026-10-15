using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

/// <summary>Native animation, sprite and font capabilities, actual GPU readback, and owner retirement.</summary>
internal sealed class EnginePreviewFamilyProbe : EngineUiCapabilityProbe
{
    private readonly string family;
    private PreviewFamilyFixtureVerification? fixture;
    private EngineOutputSample? baseline;
    private EngineOutputDifference? difference;
    private PreviewFamilyLifetimeVerification? lifetime;
    private double animationStart;
    private int spriteStart;
    private bool retired;
    private bool sceneValidated;
    private PreviewToneMapVerification? exposureFixture;
    private int exposureConfiguredAtRender;
    private bool animationVisibilityStarted;
    private const string ChangedText = "WWWWWWWWWW";
    protected override string Scenario => "E03." + family;
    protected override object Evidence => new { fixture = fixture == null ? null : new
        { fixture.Family, fixture.AssetName, fixture.AssetPath, assetId = fixture.AssetId.ToString(), fixture.SourceRoot }, difference, retired, exposureFixture,
        actualAsset = fixture == null ? null : Game.CaptureFamilyAssetIdentity(fixture.AssetId),
        animationSeconds = Game.VerificationAnimation?.CurrentSeconds,
        animationDuration = Game.VerificationAnimation?.DurationSeconds,
        animationVisibility = family == "animation" ? Game.AnimationPreviewVisibilityEvidence : null,
        animationControls = family == "animation" ? Game.AnimationPreviewControlsEvidence : null,
        fontTextControls = family == "font" ? Game.CaptureFontPreviewTextVerification() : null,
        spriteFrame = Game.VerificationSprite?.CurrentFrame, spriteFrames = Game.VerificationSprite?.FrameCount,
        spriteControls = family == "sprite" ? Game.SpritePreviewControlsEvidence : null,
        fontSample = Game.VerificationFont?.Sample,
        gaps = new[] { "All asset variations, full glyph ranges, animation scrubbing and device recovery are not covered by this representative case." } };
    internal EnginePreviewFamilyProbe(EditorGame game, string artifacts, string family, PreviewFamilyFixtureVerification? preparedFixture = null) : base(game, artifacts)
    { this.family = family; fixture = preparedFixture; if (fixture != null) SeekPhase(2); }

    protected override void Step()
    {
        var io = Gui.GetIO();
        switch (Phase)
        {
            case 0:
                fixture ??= Game.PreparePreviewFamilyFixture(family);
                if (Click("toolbar/open")) Next(); break;
            case 1: if (Click("project/open")) Next(); break;
            case 2:
                if (!sceneValidated)
                {
                    if (Game.Document == null || !EnsureScenePresented()) break;
                    sceneValidated = true;
                }
                if (!Game.VerificationFamilyBrowserRecursive)
                {
                    var control = Game.Controls.Centers.ContainsKey("asset/options/recursive") ? "asset/options/recursive"
                        : Game.Controls.Centers.ContainsKey("asset/options/display") ? "asset/options/display" : "asset/options";
                    Click(control); break;
                }
                if (Click("asset/filter")) Next(); break;
            case 3: SelectAll(); Next(); break;
            case 4: Type(fixture!.AssetName); Next(); break;
            case 5: io.AddKeyEvent(ImGuiKey.ModShift, true); io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 6: io.AddKeyEvent(ImGuiKey.Enter, false); io.AddKeyEvent(ImGuiKey.ModShift, false); Next(); break;
            case 7:
                var actualControl = Game.VerificationFamilyAssetControl(fixture!.AssetId);
                Require(actualControl != null, "The native loaded workspace must contain the exact preview fixture asset ID.");
                if (Click(actualControl!)) Next(); break;
            case 8:
                var previewControl = family == "animation" ? "animation-preview/image"
                    : family == "sprite" ? "sprite-preview/next" : "font-preview/text";
                if (!Game.Controls.Centers.ContainsKey(previewControl))
                {
                    Click("tooltab/assets.preview");
                    break;
                }
                if (Game.VerificationFamilyOwner(family) == null) break;
                AssertNativePreview();
                if (family == "animation")
                {
                    if (exposureFixture == null)
                    {
                        exposureFixture = Game.ConfigureAnimationVerificationExposure();
                        exposureConfiguredAtRender = Game.VerificationAnimationRenderCount;
                        break;
                    }
                    if (Game.VerificationAnimationRenderCount <= exposureConfiguredAtRender) break;
                    if (Click("animation-preview/pause")) Next();
                }
                else Next();
                break;
            case 9:
                AssertNativePreview();
                if (!Game.VerificationFamilyRendered(family)) break;
                if (family == "animation" && Game.VerificationAnimation!.IsPlaying) break;
                baseline = Game.ReadFamilyOutput(family);
                Game.SaveFamilyOutput(family, Path.Combine(Artifacts, Scenario + "-baseline.png"));
                lifetime = Game.CaptureFamilyLifetime(family);
                if (family == "animation")
                { animationStart = Game.VerificationAnimation!.CurrentSeconds; if (Click("animation-preview/play")) Next(); }
                else if (family == "sprite")
                { spriteStart = Game.VerificationSprite!.CurrentFrame; if (Click("sprite-preview/next")) Next(); }
                else if (Click("font-preview/text")) Next();
                break;
            case 10:
                if (family == "font") { SelectAll(); Next(); break; }
                AssertNativePreview();
                if (family == "animation")
                {
                    var animation = Game.VerificationAnimation!;
                    Require(animation.IsPlaying, "Play must enable the actual native playing animation.");
                    if (Math.Abs(animation.CurrentSeconds - animationStart) < .08) break;
                    if (Click("animation-preview/pause")) Next();
                }
                else
                {
                    Require(Game.VerificationSprite!.CurrentFrame == (spriteStart + 1) % Game.VerificationSprite.FrameCount,
                        "Next must advance the real compiled sprite frame.");
                    Next();
                }
                break;
            case 11:
                if (family == "font") { Type(ChangedText); Next(); break; }
                if (family == "animation" && !animationVisibilityStarted && Game.VerificationAnimation!.IsPlaying) break;
                if (family == "animation")
                {
                    if (!animationVisibilityStarted)
                    {
                        if (!CaptureChangedOutput()) break;
                        animationVisibilityStarted = true;
                    }
                    if (Game.StepAnimationPreviewVisibility(control => Click(control), fixture!.AssetPath)) SeekPhase(20);
                }
                else if (CaptureChangedOutput()) { if (family == "sprite") SeekPhase(21); else Next(); }
                break;
            case 20:
                if (Game.StepAnimationPreviewControls(control => Click(control), fixture!.AssetPath)) SeekPhase(12);
                break;
            case 21:
                if (Game.StepSpritePreviewControls(control => Click(control), fixture!.AssetPath)) SeekPhase(12);
                break;
            case 12:
                if (Click("asset/filter")) Next(); break;
            case 13:
                if (family == "font")
                {
                    AssertNativePreview();
                    if (Game.VerificationFont!.Sample != ChangedText || !CaptureChangedOutput()) break;
                    var metrics = Game.VerificationFontGlyphMetrics(ChangedText);
                    Require(float.IsFinite(metrics.X) && float.IsFinite(metrics.Y) && metrics.X > 0 && metrics.Y > 0,
                        "Changed sample must have actual positive native compiled glyph metrics.");
                    Game.StartFontPreviewTextVerification(Artifacts); SeekPhase(22); break;
                }
                SelectAll(); Next(); break;
            case 22:
                if (Game.StepFontPreviewTextVerification(control => Click(control))) SeekPhase(23);
                break;
            case 23:
                if (Click("asset/filter")) SeekPhase(24);
                break;
            case 24:
                SelectAll(); SeekPhase(14); break;
            case 14: Type("GameSettings"); Next(); break;
            case 15: io.AddKeyEvent(ImGuiKey.ModShift, true); io.AddKeyEvent(ImGuiKey.Enter, true); Next(); break;
            case 16: io.AddKeyEvent(ImGuiKey.Enter, false); io.AddKeyEvent(ImGuiKey.ModShift, false); Next(); break;
            case 17: if (Click("asset/GameSettings")) Next(); break;
            case 18: FinishWhenRetired(); break;
        }
    }
    private void SelectAll()
    { var io = Gui.GetIO(); io.AddKeyEvent(ImGuiKey.ModCtrl, true); io.AddKeyEvent(ImGuiKey.A, true); }
    private void Type(string text)
    {
        var io = Gui.GetIO(); io.AddKeyEvent(ImGuiKey.ModCtrl, false); io.AddKeyEvent(ImGuiKey.A, false);
        foreach (var c in text) io.AddInputCharacter(c);
    }
    private void AssertNativePreview()
    {
        if (family == "animation")
        {
            var p = Game.VerificationAnimation!;
            Require(p.SourceContent != null && p.RenderError == null && p.DurationSeconds > .1,
                "Animation must load a real compiled clip, skeleton/model and render without error.");
        }
        else if (family == "sprite")
        {
            var p = Game.VerificationSprite!;
            Require(p.SourceContent != null && p.RenderError == null && p.FrameCount >= 2,
                "Sprite must load a real compiled multi-frame sheet and render without error.");
        }
        else
        {
            var p = Game.VerificationFont!;
            Require(p.SourceContent != null && p.RenderError == null && !p.IsDisposed,
                "Font must use real compiled font content and native glyph rendering without error.");
        }
    }
    private bool CaptureChangedOutput()
    {
        AssertNativePreview();
        if (!Game.VerificationFamilyRendered(family)) return false;
        var output = Game.ReadFamilyOutput(family);
        difference = baseline!.Compare(output, 0, 0, baseline.Width, baseline.Height);
        if (difference.Value.ChangedPixels < Math.Max(16, difference.Value.Pixels / 1000)) return false;
        EngineCapabilityAssertions.VisibleFixtureChange(difference.Value, Math.Max(16, difference.Value.Pixels / 1000), .99);
        Game.SaveFamilyOutput(family, Path.Combine(Artifacts, Scenario + "-changed.png"));
        return true;
    }
    private void FinishWhenRetired()
    {
        if (!Game.FamilyLifetimeSettled(lifetime!)) return;
        retired = true;
        Finish(true, Scenario + " PASS: real compiled preview, UI interaction changes actual GPU output, native owner/target retirement completes.");
    }
    protected override void CleanInput()
    {
        Gui.GetIO().AddKeyEvent(ImGuiKey.ModShift, false);
        Game.CleanAnimationPreviewControlsInput();
        Game.CancelFontPreviewTextVerification();
        Game.CleanSpritePreviewControlsInput();
    }
}
