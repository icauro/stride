namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class FontRetirementPresentation(ProjectWorkspace? workspace, EditorSessionHistory? history)
    {
        internal readonly ProjectWorkspace? Workspace = workspace;
        internal readonly EditorSessionHistory? History = history;
        internal readonly List<InstalledFont> Sources = [];
        internal SpriteFontAssetPreview? Preview;
        internal IDisposable? PreviewSourceLease;
        internal AssetMetadataDocument? Requested, Document;
        internal string? Path;
        internal string Text = "", ValidatedText = "";
        internal long RetryAt;
        internal string? Failure;
    }
    private FontRetirementPresentation? retiredFontPresentation;

    private void EnableFontRetirement(EditorGpuRetirement retirement)
    {
        // Request is sticky, preparation is retried before any completion marker.
        retirement.RetireFonts = true;
        retirement.FontsPrepared = false;
        DeferEditorGpuMarker(retirement);
        retirement.Stage = GpuRetirementStage.Initial;
        PrepareFontRetirement();
        retirement.FontsPrepared = true;
    }
    private void PrepareFontRetirement()
    {
        StopFontSettingsUpdate();
        var state = retiredFontPresentation ??= new(workspace, sessionHistory);
        foreach (var source in refreshedFonts.Values.Distinct())
            if (!state.Sources.Contains(source)) state.Sources.Add(source);
        if (fontPreview is { } preview)
        {
            if (state.Preview == null)
            {
                // Acquire before detaching the only active preview owner. Private
                // compiled output must outlive its native caches and any failed transition.
                state.PreviewSourceLease = preview.SourceContent.AcquireLease();
                state.Preview = preview; state.Requested = requestedFontDocument;
                state.Document = fontPreviewDocument; state.Path = assetPreviewPath;
                state.Text = fontPreviewText; state.ValidatedText = validatedFontPreviewText;
            }
            preview.Visible = false;
            preview.PrepareFontRetirement();
            RetireEditorPreview(preview);
            fontPreview = null;
        }
        foreach (var source in refreshedFonts.Values.Concat(retiredFonts.Select(value => value.Owner)).Distinct())
        {
            _ = source.RuntimeFont; // Preserve the actual instance for old consumer certificates.
            source.Content.RetireGraphicsResources();
        }
    }
    private void RestoreRetiredFontPresentation()
    {
        if (retiredFontPresentation is not { } state || Environment.TickCount64 < state.RetryAt) return;
        try { RestoreRetiredFontPresentationCore(); }
        catch (Exception error)
        {
            state.RetryAt = Environment.TickCount64 + 250;
            var failure = error.GetType().FullName + ": " + error.Message;
            if (state.Failure != failure) { state.Failure = failure; RecordAssetPreviewRenderFailure("font", error); }
        }
    }
    private void RestoreRetiredFontPresentationCore()
    {
        var state = retiredFontPresentation;
        if (state == null || IsEditorGpuRetirementPending || IsEditorGraphicsRecovering
            || IsEditorGraphicsUnavailable || RecoveryTransitionBusy || closeRequested || !CanStartAuthoringPreviewUpdate) return;
        if (!ReferenceEquals(state.Workspace, workspace) || !ReferenceEquals(state.History, sessionHistory))
        { ForgetRetiredFontPresentation(); return; }
        RebindRetiredFontSources();
        if (fontPreview == null && state.Preview is { } preview && renderer != null
            && string.Equals(assetPreviewPath, state.Path, StringComparison.OrdinalIgnoreCase)
            && state.Document != null && documents.AuthoredDocuments.Any(owner => ReferenceEquals(owner, state.Document)))
        {
            CompiledFontLease? lease = null;
            if (refreshedFonts.TryGetValue(preview.SourceUrl, out var installed)
                && ReferenceEquals(installed.Content.Bundle, preview.SourceContent)) lease = new(installed);
            try { fontPreview = preview.Recreate(renderer, lease); }
            catch { lease?.Dispose(); throw; }
            requestedFontDocument = state.Requested; fontPreviewDocument = state.Document;
            fontPreviewText = state.Text; validatedFontPreviewText = state.ValidatedText;
        }
        ForgetRetiredFontPresentation();
    }
    private void RebindRetiredFontSources()
    {
        EnsureEditorGpuRetirementCompleted();
        if (retiredFontPresentation is not { } state || !ReferenceEquals(state.Workspace, workspace)
            || !ReferenceEquals(state.History, sessionHistory)) return;
        foreach (var source in state.Sources)
            if (refreshedFonts.Values.Any(current => ReferenceEquals(current, source)))
                source.Content.RebindGraphicsResourcesAfterRecovery();
    }
    private void EnsureInstalledFontGraphicsRetired()
    {
        if (fontPreview != null || refreshedFonts.Values.Concat(retiredFonts.Select(value => value.Owner))
            .Any(source => source.Content.HasGraphicsResources))
            throw new InvalidOperationException("Installed font resources must enter retirement before the terminal GPU certificate.");
    }
    private void ForgetRetiredFontPresentation()
    {
        if (retiredFontPresentation is not { } state) return;
        state.PreviewSourceLease?.Dispose(); state.PreviewSourceLease = null;
        retiredFontPresentation = null;
    }
}
