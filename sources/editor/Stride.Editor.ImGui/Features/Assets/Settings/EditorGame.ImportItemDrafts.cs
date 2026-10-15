using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanEditImportItemDraft(AssetMetadataDocument owner, AssetImportSettingItem item)
        => !IsAuthoringPipelineUpdating && IsCurrentMetadataControlOwner(owner)
        && sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is { IsPublished: true } state && ReferenceEquals(state.Document, owner)
        && ReferenceEquals(item.Field.Collection, item.Collection) && owner.CanEditImportItem(item);
    private object?[] ReadImportItemDraft(AssetMetadataDocument owner, AssetImportSettingItem item)
        => [owner.ReadImportItem(item), item.Value, item.Collection, item.Index, item.Id, item.Key,
            item.Field.Node, item.Field.Node.Retrieve(), owner.Path, owner.EditRevision, owner.SessionHistory?.Extensions?.Version];
    private IDisposable AcquireImportItemDraft(AssetMetadataDocument owner, AssetImportSettingItem item)
    {
        var leases = new List<IDisposable>();
        try
        {
            if (!CanEditImportItemDraft(owner, item)) throw new InvalidOperationException("The import item is no longer editable.");
            leases.Add(AcquirePropertyStringOwner(owner));
            if (owner.AcquireImportItemCommandTarget(item) is { } target) leases.Add(target);
            return new PropertyStringOwners(leases, [owner]);
        }
        catch { foreach (var lease in leases) packageCommandCleanup.Add(lease.Dispose); throw; }
    }
    private void CommitImportItemDraft(AssetMetadataDocument owner, AssetImportSettingItem item, object? value)
    {
        if (!CanEditImportItemDraft(owner, item)) throw new InvalidOperationException("The import item is no longer editable.");
        if (Equals(item.Value, value)) return;
        UseTextureSettingsCommandContext(); owner.UpdateImportItem(item, value); owner.EndImportSettingEdit();
    }
    private bool TryDrawImportItemDraft(AssetMetadataDocument owner, AssetImportSettingItem item)
    {
        var identity = item.Collection;
        bool Editable() => CanEditImportItemDraft(owner, item);
        object?[] Read() => ReadImportItemDraft(owner, item);
        IDisposable Acquire() => AcquireImportItemDraft(owner, item);
        void Commit(object? value) => CommitImportItemDraft(owner, item, value);
        if (IsPathType(item.Type))
        {
            DrawRetainedPath(owner, identity, item.Type, Editable, Read, value => Commit(value), 1, Acquire,
                () => { if (Controls.Enabled) Controls.Record("asset-settings/" + item.Key); });
            return true;
        }
        if (item.Type == typeof(string))
        {
            try { PropertyGrid.Strings.Draw(identity, false, Editable, Read, value => Commit(value), Acquire,
                onCancelled: error => ClearCancelledPropertyDraft(error)); }
            catch (Exception error) { SetPropertyDraftDiagnostic(error); }
        }
        else if (AssetSettingNumericMetadata.Supports(item.Type)) DrawImportItemNumberDraft(owner, item);
        else if (IsImportMathType(item.Type))
        {
            var ranges = owner.ImportItemAxisNumericRanges(item);
            DrawRetainedImportMath(identity, item.Type, Read, Editable, Commit, Acquire,
                axis => ranges.ElementAtOrDefault(axis), axis => owner.IsImportItemAxisReadOnly(item, axis),
                part => { if (Controls.Enabled) Controls.Record("asset-settings/" + item.Key + "/" + part); });
        }
        else if (IsTemporalType(item.Type))
            DrawRetainedTemporal(identity, item.Type, () => Editable() && owner.ReadImportItem(item) != null, Read,
                transform => Commit(transform(item.Value)), 1, Acquire);
        else if (item.Type == typeof(Guid)) DrawRetainedGuid(identity, Read, Editable, value => Commit(value), Acquire);
        else if (item.Type == typeof(char)) DrawRetainedCharacter(identity, Read, Editable, value => Commit(value), Acquire);
        else if (item.Type == typeof(Stride.Core.Mathematics.AngleSingle)) DrawRetainedAngle(identity, Read, Editable, value => Commit(value), Acquire);
        else if (IsPackageColorType(item.Type))
            DrawRetainedColor(identity, item.Type, Read, Editable, transform => Commit(transform(item.Value)), Acquire,
                axis => !owner.IsImportItemAxisReadOnly(item, axis),
                capture: part => { if (Controls.Enabled) Controls.Record("asset-settings/" + item.Key + "/" + part); });
        else return false;
        if (Controls.Enabled && Gui.IsItemVisible()) Controls.Record("asset-settings/" + item.Key);
        return true;
    }
    private void DrawImportItemNumberDraft(AssetMetadataDocument owner, AssetImportSettingItem item)
    {
        var range = owner.ImportItemNumericRange(item);
        var ranged = NativeRangedNumericControl.Matches(item.Field.ElementType!, range);
        var initial = owner.ReadImportItem(item); object? display = initial;
        var active = false; var complete = false;
        try
        {
            if (ranged)
            {
                using var disabled = ImGuiScopes.Disabled(!CanEditImportItemDraft(owner, item));
                complete = NativeRangedNumericControl.Draw(item.Collection, item.Type, initial, false, [range], UiScale(1), out display, out _);
                active = Gui.IsItemActive();
            }
            PropertyGrid.Strings.Draw(item.Collection, false, () => CanEditImportItemDraft(owner, item),
                () => ReadImportItemDraft(owner, item), text =>
                {
                    var number = NativeNumericText.Parse(text, item.Type, range);
                    if (number is float single && !float.IsFinite(single) || number is double real && !double.IsFinite(real))
                        throw new FormatException("The numeric value must be finite.");
                    CommitImportItemDraft(owner, item, number);
                }, () => AcquireImportItemDraft(owner, item), text =>
                {
                    if (item.Type == typeof(decimal) && !ranged) return null;
                    var stepped = text;
                    return NativeNumericInput.StepText(item.Type, owner.ReadImportItem(item), false, range, ref stepped) ? stepped : null;
                }, format: value => NativeNumericText.Format(value, item.Type, range),
                    externalText: ranged && display != null && !Equals(display, initial) ? NativeNumericText.Format(display, item.Type, range) : null,
                    externalActive: active, externalCommit: complete,
                    onCancelled: error => ClearCancelledPropertyDraft(error));
        }
        catch (Exception error) { SetPropertyDraftDiagnostic(error); }
    }
}
