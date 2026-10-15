using Stride.Video.Assets;
using Stride.Assets.Media;
using Stride.Core.IO;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool CanEditImportSettingDraft(AssetMetadataDocument owner, AssetImportSettingField field)
        => !IsAuthoringPipelineUpdating && IsCurrentMetadataControlOwner(owner)
        && sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is { IsPublished: true } state && ReferenceEquals(state.Document, owner)
        && owner.CanEditImportSetting(field);
    private object?[] ReadImportSettingDraft(AssetMetadataDocument owner, AssetImportSettingField field)
        => [owner.ReadImportSetting(field), field.Node.Retrieve(), field.Node, field.Key, field.Node.Parent.Retrieve(), owner.Path, owner.EditRevision,
            owner.SessionHistory?.Extensions?.Version, owner.AuthoredAsset is VideoAsset video ? video.IsSizeInPercentage : null];
    private IDisposable AcquireImportSettingDraft(AssetMetadataDocument owner, AssetImportSettingField field)
    {
        var leases = new List<IDisposable>();
        try
        {
            if (!CanEditImportSettingDraft(owner, field)) throw new InvalidOperationException("The import setting is no longer editable.");
            leases.Add(AcquirePropertyStringOwner(owner));
            if (owner.AcquireExtensionCommandTarget(field.Node) is { } target) leases.Add(target);
            return new PropertyStringOwners(leases, [owner]);
        }
        catch { foreach (var lease in leases) packageCommandCleanup.Add(lease.Dispose); throw; }
    }
    private void CommitImportSettingDraft(AssetMetadataDocument owner, AssetImportSettingField field, object? value)
    {
        if (!CanEditImportSettingDraft(owner, field)) throw new InvalidOperationException("The import setting is no longer editable.");
        if (Equals(field.Node.Retrieve(), value)) return;
        UseTextureSettingsCommandContext(); owner.UpdateImportSetting(field, value); owner.EndImportSettingEdit();
    }
    private bool TryDrawImportSettingDraft(AssetMetadataDocument owner, AssetImportSettingField field)
    {
        if (IsPathType(field.Type))
        {
            DrawRetainedPath(owner, field.Node, field.Type, () => CanEditImportSettingDraft(owner, field),
                () => ReadImportSettingDraft(owner, field), value => CommitImportSettingDraft(owner, field, value), 1,
                () => AcquireImportSettingDraft(owner, field),
                () => { if (Controls.Enabled && Gui.IsItemVisible()) Controls.Record("asset-settings/" + field.Key); });
            return true;
        }
        if (field.Type == typeof(string))
        {
            try
            {
                PropertyGrid.Strings.Draw(field.Node, false, () => CanEditImportSettingDraft(owner, field),
                    () => ReadImportSettingDraft(owner, field), value => CommitImportSettingDraft(owner, field, value),
                    () => AcquireImportSettingDraft(owner, field), onCancelled: error => ClearCancelledPropertyDraft(error));
            }
            catch (Exception error) { SetPropertyDraftDiagnostic(error); }
        }
        else if (AssetSettingNumericMetadata.Supports(field.Type)) DrawImportNumberDraft(owner, field);
        else if (IsImportMathType(field.Type)) { DrawImportMathDraft(owner, field); return true; }
        else if (IsTemporalType(field.Type)) { DrawImportTemporalDraft(owner, field); return true; }
        else if (field.Type == typeof(Guid))
            DrawRetainedGuid(field.Node, () => ReadImportSettingDraft(owner, field), () => CanEditImportSettingDraft(owner, field),
                value => CommitImportSettingDraft(owner, field, value), () => AcquireImportSettingDraft(owner, field));
        else if (field.Type == typeof(char))
            DrawRetainedCharacter(field.Node, () => ReadImportSettingDraft(owner, field), () => CanEditImportSettingDraft(owner, field),
                value => CommitImportSettingDraft(owner, field, value), () => AcquireImportSettingDraft(owner, field));
        else if (field.Type == typeof(Stride.Core.Mathematics.AngleSingle))
            DrawRetainedAngle(field.Node, () => ReadImportSettingDraft(owner, field), () => CanEditImportSettingDraft(owner, field),
                value => CommitImportSettingDraft(owner, field, value), () => AcquireImportSettingDraft(owner, field));
        else if (IsPackageColorType(field.Type))
            DrawRetainedColor(field.Node, field.Type, () => ReadImportSettingDraft(owner, field), () => CanEditImportSettingDraft(owner, field),
                transform => CommitImportSettingDraft(owner, field, transform(field.Node.Retrieve())), () => AcquireImportSettingDraft(owner, field),
                axis => !owner.ExtensionNumericAxisReadOnly(field.Node, axis),
                capture: part => { if (Controls.Enabled) Controls.Record("asset-settings/" + field.Key + "/" + part); });
        else return false;
        if (Controls.Enabled && Gui.IsItemVisible()) Controls.Record("asset-settings/" + field.Key);
        return true;
    }
    private void DrawImportNumberDraft(AssetMetadataDocument owner, AssetImportSettingField field)
    {
        var absoluteVideo = owner.AuthoredAsset is VideoAsset { IsSizeInPercentage: false }
            && field.Node.Name is nameof(VideoAsset.Width) or nameof(VideoAsset.Height);
        var range = owner.ImportSettingNumericRange(field);
        var ranged = !absoluteVideo && NativeRangedNumericControl.Matches(field.Node.Type, range);
        var initial = owner.ReadImportSetting(field); object? display = initial;
        var active = false; var complete = false;
        try
        {
            if (ranged)
            {
                using var disabled = ImGuiScopes.Disabled(!CanEditImportSettingDraft(owner, field));
                complete = NativeRangedNumericControl.Draw(field.Node, field.Type, initial, false, [range], UiScale(1), out display, out _);
                active = Gui.IsItemActive();
            }
            PropertyGrid.Strings.Draw(field.Node, false, () => CanEditImportSettingDraft(owner, field),
                () => ReadImportSettingDraft(owner, field), text =>
                {
                    var number = NativeNumericText.Parse(text, field.Type, range);
                    if (number is float single && !float.IsFinite(single) || number is double real && !double.IsFinite(real))
                        throw new FormatException("The numeric value must be finite.");
                    CommitImportSettingDraft(owner, field, number);
                }, () => AcquireImportSettingDraft(owner, field), text =>
                {
                    if (field.Type == typeof(decimal) && !ranged) return null;
                    var stepped = text;
                    return NativeNumericInput.StepText(field.Type, owner.ReadImportSetting(field), false, range, ref stepped) ? stepped : null;
                }, format: value => NativeNumericText.Format(value, field.Type, range),
                    externalText: ranged && display != null && !Equals(display, initial) ? NativeNumericText.Format(display, field.Type, range) : null,
                    externalActive: active, externalCommit: complete,
                    onCancelled: error => ClearCancelledPropertyDraft(error));
        }
        catch (Exception error) { SetPropertyDraftDiagnostic(error); }
    }
}
