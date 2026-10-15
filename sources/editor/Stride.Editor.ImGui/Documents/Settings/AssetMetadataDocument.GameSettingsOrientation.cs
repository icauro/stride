using Stride.Graphics;

namespace Stride.Editor.ImGui;

internal partial class AssetMetadataDocument
{
    private bool gameOrientationInitialized, gameOrientationSaved;
    private RequiredDisplayOrientation gameOrientationBaseline, savedGameOrientation;
    private long gameOrientationSaveVersion;
    private byte[]? gameOrientationRequestHash, loadedGameOrientationHash;
    private RequiredDisplayOrientation loadedGameOrientation;
    private bool gameOrientationDiskLoadPending;
    private void InitializeGameOrientationBaseline()
    {
        if (gameOrientationInitialized || GameSettings == null) return;
        gameOrientationBaseline = GameSettings.TryGet<RenderingSettings>()?.DisplayOrientation ?? RequiredDisplayOrientation.Default;
        gameOrientationInitialized = true;
        gameOrientationRequestHash ??= diskHash.ToArray();
    }
    // This is file provenance, not authored graph state. A retained generation
    // must retry the latest saved request, even when its graph has been undone.
    internal void CopyGameSettingsOrientationProvenanceFrom(AssetMetadataDocument source)
    {
        if (GameSettings == null || source.GameSettings == null || AuthoredAsset.Id != source.AuthoredAsset.Id)
            return;
        source.InitializeGameOrientationBaseline();
        AcceptGameOrientationProvenance(source);
        if (gameOrientationDiskLoadPending && loadedGameOrientationHash != null
            && !loadedGameOrientationHash.SequenceEqual(source.gameOrientationRequestHash ?? source.diskHash))
        {
            // A disk reload acknowledges a new exact file snapshot. Migration
            // constructors have no pending disk snapshot and preserve retries.
            savedGameOrientation = loadedGameOrientation;
            gameOrientationSaved = true;
            gameOrientationSaveVersion++;
            gameOrientationRequestHash = loadedGameOrientationHash.ToArray();
        }
    }
    internal void ReconcilePublishedGameOrientationProvenance()
    {
        if (GameSettings == null) return;
        var owners = RetainedGameOrientationOwners();
        gameOrientationDiskLoadPending = false;
        foreach (var owner in owners) if (!ReferenceEquals(owner, this)) owner.AcceptGameOrientationProvenance(this);
    }
    private void AcceptGameOrientationProvenance(AssetMetadataDocument source)
    {
        gameOrientationInitialized = source.gameOrientationInitialized;
        gameOrientationSaved = source.gameOrientationSaved;
        gameOrientationBaseline = source.gameOrientationBaseline;
        savedGameOrientation = source.savedGameOrientation;
        gameOrientationSaveVersion = source.gameOrientationSaveVersion;
        gameOrientationRequestHash = source.gameOrientationRequestHash?.ToArray();
    }
    private AssetMetadataDocument[] RetainedGameOrientationOwners()
    {
        var state = historyScope.AuthoringState;
        if (state == null) return [this];
        state.Registry.EnsureOwnerThread();
        if (!state.IsPublished || !ReferenceEquals(state.Registry.Find(AuthoredAsset.Id), state))
            throw new InvalidOperationException("Only the canonical game settings generation can synchronize platform orientation.");
        return state.Registry.GetRetainedStates(AuthoredAsset.Id)
            .Select(owner => owner.Document).OfType<AssetMetadataDocument>()
            .Where(owner => owner.GameSettings != null).ToArray();
    }
    internal void MarkGameSettingsOrientationSaved()
    {
        if (GameSettings == null) return;
        var owners = RetainedGameOrientationOwners();
        InitializeGameOrientationBaseline();
        savedGameOrientation = GameSettings.TryGet<RenderingSettings>()?.DisplayOrientation ?? RequiredDisplayOrientation.Default;
        gameOrientationSaved = true; gameOrientationSaveVersion++;
        gameOrientationRequestHash = diskHash.ToArray(); gameOrientationDiskLoadPending = false;
        foreach (var owner in owners) if (!ReferenceEquals(owner, this)) owner.AcceptGameOrientationProvenance(this);
    }
    internal bool TryGetSavedGameOrientationChange(out RequiredDisplayOrientation orientation, out long version)
    {
        orientation = savedGameOrientation; version = gameOrientationSaveVersion;
        return gameOrientationSaved && savedGameOrientation != gameOrientationBaseline;
    }
    internal void CompleteSavedGameOrientationChange(RequiredDisplayOrientation orientation, long version)
    {
        if (!gameOrientationSaved || version != gameOrientationSaveVersion || orientation != savedGameOrientation)
            throw new InvalidOperationException("The saved game orientation changed during platform synchronization.");
        var owners = RetainedGameOrientationOwners();
        gameOrientationBaseline = orientation;
        foreach (var owner in owners) if (!ReferenceEquals(owner, this)) owner.AcceptGameOrientationProvenance(this);
    }
}
