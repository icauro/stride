using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly List<ContentReferencePicker> templateReferencePickers = [];
    private bool TryHandleTemplateReferenceFileDrop(string[] files, System.Numerics.Vector2 position)
    {
        for (var index = templateReferencePickers.Count - 1; index >= 0; index--)
            if (templateReferencePickers[index].TryHandleNativeFileDrop(files, position)) return true;
        return false;
    }
    private readonly HashSet<IDisposable> retiredTemplateLeases = new(ReferenceEqualityComparer.Instance);
    private void RetireTemplateReferenceLease(IDisposable lease)
    {
        if (retiredTemplateLeases.Contains(lease)) return;
        try { lease.Dispose(); }
        catch
        {
            retiredTemplateLeases.Add(lease);
            packageCommandCleanup.Add(() => { lease.Dispose(); retiredTemplateLeases.Remove(lease); });
        }
    }
    private void RequestAssetTemplateReference(AssetCreationPanel.Choice choice, AssetId? selected, Func<bool> wizardCurrent, Action<AssetId?> completed)
    {
        var panel = AssetCreation;
        var owner = workspace ?? throw new InvalidOperationException("The template session changed.");
        var history = sessionHistory ?? throw new InvalidOperationException("The template history changed.");
        var initial = AssetCreationBrowser.SelectedFolder ?? throw new InvalidOperationException("The template location changed.");
        ValidateDroppedAssetTarget(owner, initial);
        var target = ResolveAssetTemplateLocation(initial, choice.AssetType);
        var kind = AssetCreationPanel.AssetCreationReferenceKind(choice.Template?.Id) ?? throw new InvalidOperationException("The template has no reference parameter.");
        var packagePath = target.Package.FullPath.ToOSPath();
        var assetNamespace = target.Package.Container?.AssetNamespace ?? target.Package.AssetNamespace;
        var state = history.Registry.GetOrCreate(packagePath, null, () => new PackageMetadataDocument(owner, target.Package, history));
        var revision = state.Document.Revision;
        bool Current(bool includeRevision)
        {
            if (!wizardCurrent() || !ReferenceEquals(workspace, owner) || !ReferenceEquals(sessionHistory, history)
                || !IsCurrentEditableAssetPackage(target.Package) || !SamePath(target.Package.FullPath.ToOSPath(), packagePath)
                || assetNamespace != (target.Package.Container?.AssetNamespace ?? target.Package.AssetNamespace)
                || !ReferenceEquals(authoring?.Find(packagePath), state)
                || assetBrowser.FindFolder(target.Id) is not { } folder || !ReferenceEquals(folder.Package, target.Package)
                || folder.RelativePath != target.RelativePath || folder.IsCode != target.IsCode
                || includeRevision && state.Document.Revision != revision) return false;
            try { choice.Adapter?.Validate(); return true; }
            catch (InvalidOperationException) { return false; }
        }
        IReadOnlyList<IDisposable> Acquire()
        {
            if (!Current(true)) throw new InvalidOperationException("The template reference owner changed.");
            var leases = new List<IDisposable>();
            try
            {
                leases.Add(state.AcquireView());
                if (choice.Adapter != null) leases.Add(choice.Adapter.Retain());
                return leases;
            }
            catch { foreach (var lease in leases) RetireTemplateReferenceLease(lease); throw; }
        }
        var certificate = new ContentReferenceEditCertificate(Current, () => revision = state.Document.Revision, Acquire(), Acquire);
        var message = choice.Template!.Id.ToString().ToUpperInvariant() switch
        {
            "F16AB154-86A6-4020-B410-88FBD87D3CA5" => "Select a cubemap texture to use for this skybox - _cancel to leave the texture empty_",
            "8267F08C-3DC8-48F4-81EA-4888A1CEF9CE" => "Select a material to use for this model - _cancel to leave the material empty_",
            "5A3CD5E3-4318-46AF-A41C-8146D2D21F80" => "Select a model to use to generate the hull - _cancel to leave the model empty_",
            "5A3CD5E3-4328-36AF-A41C-8146D2D21F83" => "Select a model to use for this static mesh - _cancel to leave the model empty_",
            _ => "Select the model to use to generate a convex hull - _cancel to leave the model empty_",
        };
        try
        {
            var picker = panel.ReferenceDialog ??= new(new ContentReferencePickerHost(this, true), panel.PopupIdentity + "/Reference");
            picker.RecordControl = id => { if (Controls.Enabled) Controls.Record(id); };
            if (!templateReferencePickers.Contains(picker)) templateReferencePickers.Add(picker);
            picker.RequestTemplateReference(owner, kind.Types,
                selected, assetBrowser.Packages.First(root => ReferenceEquals(root.Package, target.Package)).Children.First(mount => !mount.IsCode).Id, certificate, item => Services.Commands.TryExecute(() =>
                {
                    if (!Current(true) || !ReferenceEquals(owner.Session.FindAsset(item.Id), item) || item.IsDeleted
                        || !kind.Types.Any(type => type.IsInstanceOfType(item.Asset))) throw new InvalidOperationException("The template reference changed.");
                    panel.RetainTemplateReference(authoring?.Find(item.Id)?.AcquireView());
                    completed(item.Id);
                }), () => { if (wizardCurrent()) { panel.RetainTemplateReference(null); completed(null); } }, message);
        }
        catch { RetireTemplateReferenceLease(certificate); throw; }
    }
}
