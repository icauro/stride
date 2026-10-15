using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

/// <summary>Optional actual view state for activating an existing editor and observing its native close.</summary>
public interface IEditorAssetEditorState
{
    bool IsClosed { get; }
    void Activate();
}

internal sealed partial class EditorExtensionSession
{
    private sealed class OwnedAssetEditor(AssetId id, Provider provider, EditorAuthoringState owner, IDisposable viewLease)
    {
        internal readonly AssetId Id = id;
        internal readonly Provider Provider = provider;
        internal readonly EditorAuthoringState Owner = owner;
        internal readonly IDisposable ViewLease = viewLease;
        internal IDisposable? Resource;
        internal bool Initializing = true, CloseRequested, ResourceReleased, ViewReleased, ProviderReleased;
        internal bool IsCurrent => Owner.IsPublished && !Owner.IsDisposed && ReferenceEquals(Owner.Registry.Find(Id), Owner);
    }
    private readonly List<OwnedAssetEditor> ownedAssetEditors = [];
    internal bool HasOwnedAssetEditors => ownedAssetEditors.Count != 0;
    private bool OpenOwnedAssetEditor(AssetItem item, Provider provider, Func<AssetItem, IDisposable> factory)
    {
        foreach (var existing in ownedAssetEditors.Where(editor => editor.Id == item.Id).ToArray())
        {
            if (!existing.CloseRequested && existing.IsCurrent && ReferenceEquals(existing.Provider, provider))
            {
                if (existing.Resource is IEditorAssetEditorState state && state.IsClosed) CloseOwnedAssetEditor(existing);
                else { if (existing.Resource is IEditorAssetEditorState active) active.Activate(); return true; }
            }
            else CloseOwnedAssetEditor(existing);
        }
        var owner = context.GetAssetEditorOwner(item);
        var viewLease = owner.AcquireView(); provider.Leases++;
        var editor = new OwnedAssetEditor(item.Id, provider, owner, viewLease);
        ownedAssetEditors.Add(editor);
        try
        {
            if (provider.Retiring || !provider.Available || IsProviderGenerationUnloading(provider.Type))
                throw new InvalidOperationException("The owned editor generation is no longer available.");
            editor.Resource = factory(item) ?? throw new InvalidOperationException("The owned editor factory returned no resource.");
            editor.Initializing = false;
            if (!editor.IsCurrent || provider.Retiring || !provider.Available || IsProviderGenerationUnloading(provider.Type))
                throw new InvalidOperationException("The asset editor owner changed during initialization.");
            if (editor.CloseRequested) CloseOwnedAssetEditor(editor);
            return true;
        }
        catch (Exception failure)
        {
            editor.Initializing = false; editor.CloseRequested = true;
            try { CloseOwnedAssetEditor(editor); }
            catch (Exception cleanup) { throw new AggregateException("Owned editor initialization and cleanup failed.", failure, cleanup); }
            throw;
        }
    }
    internal void DrainOwnedAssetEditors()
    {
        Check();
        foreach (var editor in ownedAssetEditors) editor.CloseRequested = true;
        RetireOwnedAssetEditors();
        if (ownedAssetEditors.Count != 0) throw new InvalidOperationException("An owned asset editor is still initializing.");
    }
    private void CloseOwnedAssetEditors(AssetId id)
    {
        Check();
        List<Exception>? failures = null;
        foreach (var editor in ownedAssetEditors.Where(editor => editor.Id == id).ToArray())
            try { CloseOwnedAssetEditor(editor); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        if (failures != null) throw new AggregateException("An owned asset editor has not finished closing.", failures);
    }
    private void CloseOwnedAssetEditor(OwnedAssetEditor editor)
    {
        editor.CloseRequested = true;
        if (editor.Initializing) throw new InvalidOperationException("The asset editor is still initializing.");
        if (!editor.ResourceReleased)
        {
            var allowed = context.MutationsAllowed; context.MutationsAllowed = false;
            try { editor.Resource?.Dispose(); editor.ResourceReleased = true; }
            finally { context.MutationsAllowed = allowed; }
        }
        if (!editor.ViewReleased) { editor.ViewLease.Dispose(); editor.ViewReleased = true; }
        if (!editor.ProviderReleased) { editor.Provider.Leases--; editor.ProviderReleased = true; }
        ownedAssetEditors.Remove(editor);
    }
    private void RetireOwnedAssetEditors()
    {
        List<Exception>? failures = null;
        foreach (var editor in ownedAssetEditors.ToArray())
        {
            if (editor.Initializing) continue;
            try
            {
                if (stopped || editor.CloseRequested || editor.Provider.Retiring || IsProviderGenerationUnloading(editor.Provider.Type)
                    || !editor.IsCurrent || editor.Resource is IEditorAssetEditorState { IsClosed: true })
                    CloseOwnedAssetEditor(editor);
            }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        if (failures != null) throw new AggregateException("Owned asset editor cleanup is incomplete; its canonical owners remain retained.", failures);
    }
}
