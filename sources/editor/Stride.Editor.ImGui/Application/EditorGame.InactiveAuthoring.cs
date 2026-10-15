using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool pumpingInactiveAuthoring;
    protected override void OnInactiveTick()
    {
        CancelSceneCameraNavigation();
        base.OnInactiveTick();
        if (smokeTest) return;
        Gui.SetCurrentContext(guiContext);
        PumpSpriteSourceCleanup();
        PumpRetiredAssetThumbnailServices();
        PumpAnimationPreviewScrub();
        PumpExternalAssetPreview();
        PumpEditorGpuRetirement(submittedBoundary: true);
        PumpScriptGlyphImages(submittedBoundary: true);
        PumpRetiredEditorPreviews(submittedBoundary: true);
        PumpSceneDocumentViewDrain();
        CancelSceneLightProbeCommands();
        if (IsEditorGraphicsRecovering) { PumpEditorGraphicsRecovery(); return; }
        if (renderer == null) return;
        pumpingInactiveAuthoring = true;
        try
        {
            PumpCancelledProjectCreation();
            PumpEditorExtensionCommands();
            PumpPackageDependencyRestore(); PumpPackageDependencyRemovalCheck();
            PumpAddedSolutionProjects(); PumpEditorExtensions(); PumpEffectLogs(); PumpProjectIdeOpen();
            PumpPackagePlatformUpdate();
            PumpPackageMetadataSave();
            PumpSessionAuthoringCleanup();
            PumpDocumentCloseRequests();
            if (!IsPackageDependencyBusy)
            {
                // Finish actual authored operations before accepting the next
                // script reload. Preview-start eligibility is suppressed here.
                PumpAssetImportUpdate(); PumpCompositorSettingsUpdate(); PumpImportSettingsUpdate();
                PumpTextureSettingsUpdate(); PumpMaterialSettingsUpdate(); PumpSkyboxSettingsUpdate(); PumpFontSettingsUpdate();
                PumpAnimatedMediaPreviews();
                PumpCatalogPreviewUpdate();
            }
            PumpUserProjectBuild();
        }
        finally { pumpingInactiveAuthoring = false; }
    }
}
