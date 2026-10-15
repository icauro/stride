using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool IsMaterialSettingDraftCurrent(AssetMetadataDocument owner, MaterialSettingField field)
        => IsCurrentMetadataControlOwner(owner) && owner.IsCurrentMaterialField(field)
        && sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is { IsPublished: true } state && ReferenceEquals(state.Document, owner);
    private bool CanEditMaterialSettingDraft(AssetMetadataDocument owner, MaterialSettingField field, int axis = -1)
        => !IsAuthoringPipelineUpdating && IsMaterialSettingDraftCurrent(owner, field) && owner.CanEditMaterialSettings
        && field.CanAssign && owner.CanEditAssetSettingNode(field.Node) && !owner.IsMaterialSettingReadOnly(field)
        && (axis < 0 || !owner.ExtensionNumericAxisReadOnly(field.Node, axis));
    private object?[] ReadMaterialSettingDraft(AssetMetadataDocument owner, MaterialSettingField field)
        => [owner.ReadMaterialSetting(field), field.Node.Retrieve(), field.Node, field.Key, field.Node.Parent.Retrieve(), owner.EditRevision,
            owner.SessionHistory?.Extensions?.Version];
    private IDisposable AcquireMaterialSettingDraft(AssetMetadataDocument owner, MaterialSettingField field)
    {
        var leases = new List<IDisposable>();
        try
        {
            if (!IsMaterialSettingDraftCurrent(owner, field)) throw new InvalidOperationException("The material setting is no longer current.");
            leases.Add(AcquirePropertyStringOwner(owner));
            if (owner.AcquireExtensionCommandTarget(field.Node) is { } target) leases.Add(target);
            return new PropertyStringOwners(leases, [owner]);
        }
        catch { foreach (var lease in leases) packageCommandCleanup.Add(lease.Dispose); throw; }
    }
    private void CommitMaterialSettingDraft(AssetMetadataDocument owner, MaterialSettingField field, object? value)
    {
        if (!CanEditMaterialSettingDraft(owner, field)) throw new InvalidOperationException("The material setting is no longer editable.");
        if (Equals(field.Node.Retrieve(), value)) return;
        UseTextureSettingsCommandContext();
        owner.UpdateMaterialSetting(field, value);
        owner.EndMaterialSettingEdit();
    }
    private bool TryDrawMaterialSettingDraft(AssetMetadataDocument owner, MaterialSettingField field, string label)
    {
        if (TryDrawMaterialSpecialDraft(owner, field, label)) return true;
        using var nullableRow = NativeNullableRow.Begin("##NullableMaterial", Nullable.GetUnderlyingType(field.Node.Type) is { } nullable && nullable.IsValueType && !nullable.IsPrimitive && !nullable.IsEnum);
        if (Nullable.GetUnderlyingType(field.Node.Type) is { } nullableType && !nullableType.IsPrimitive && !nullableType.IsEnum)
        {
            DrawMaterialNullableSpecialPresence(owner, field); nullableRow.ValueColumn();
            if (owner.ReadMaterialSetting(field) == null) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); return true; }
        }
        var names = NativeNumericAxes.Names(field.Type);
        var vector = field.Type == typeof(Vector2) || field.Type == typeof(Vector3) || field.Type == typeof(Vector4);
        if (field.Type != typeof(string) && !AssetSettingNumericMetadata.Supports(field.Type) && !vector) return false;
        if (field.Type == typeof(string))
        {
            try
            {
                PropertyGrid.Strings.Draw(field.Node, false, () => CanEditMaterialSettingDraft(owner, field),
                    () => ReadMaterialSettingDraft(owner, field), value => CommitMaterialSettingDraft(owner, field, value),
                    () => AcquireMaterialSettingDraft(owner, field), controlLabel: label);
            }
            catch (Exception error) { statusMessage = error.Message; }
            return true;
        }
        if (!vector) { DrawMaterialNumberDraft(owner, field, label); return true; }
        if (!label.StartsWith("##", StringComparison.Ordinal)) { Gui.TextUnformatted(label); Gui.SameLine(); }
        if (!Gui.BeginTable("##Coordinates", names.Count, Hexa.NET.ImGui.ImGuiTableFlags.SizingStretchSame)) return true;
        try
        {
            for (var axis = 0; axis < names.Count; axis++)
            {
                Gui.TableNextColumn(); Gui.PushID(names[axis]);
                try { Gui.TextUnformatted(names[axis]); Gui.SameLine(); Gui.SetNextItemWidth(-1); DrawMaterialNumberDraft(owner, field, "##Value", axis); }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.EndTable(); }
        return true;
    }
    private void DrawMaterialNumberDraft(AssetMetadataDocument owner, MaterialSettingField field, string label, int axis = -1)
    {
        var type = axis < 0 ? field.Type : PropertyCoordinateType(field.Type, axis);
        var range = axis < 0 ? owner.MaterialSettingRange(field) : owner.ExtensionAxisNumericRanges(field.Node).ElementAtOrDefault(axis);
        if (axis >= 0 && range?.DecimalPlaces == null)
            range = new PropertyNumericRange(new PropertyNumericMetadata(range?.Minimum, range?.Maximum,
                range?.DeclaredSmallStep, range?.DeclaredLargeStep, 3), type);
        object? Coordinate(object? value) => axis < 0 ? value : CommonCoordinate(value, axis);
        bool Editable() => CanEditMaterialSettingDraft(owner, field, axis);
        var initial = owner.ReadMaterialSetting(field); object? display = initial;
        var ranged = axis < 0 && NativeRangedNumericControl.Matches(field.Node.Type, range);
        var active = false; var complete = false;
        try
        {
            if (ranged)
            {
                using var disabled = ImGuiScopes.Disabled(!Editable());
                complete = NativeRangedNumericControl.Draw(field.Node, type, initial, false, [range], UiScale(1), out display, out _);
                active = Gui.IsItemActive();
            }
            PropertyGrid.Strings.Draw(field.Node, false, Editable, () => ReadMaterialSettingDraft(owner, field), text =>
            {
                var number = axis < 0 && string.IsNullOrWhiteSpace(text) && Nullable.GetUnderlyingType(field.Node.Type) != null
                    ? null : NativeNumericText.Parse(text, type, range);
                if (number is float single && !float.IsFinite(single) || number is double real && !double.IsFinite(real))
                    throw new FormatException("The numeric value must be finite.");
                var proposed = axis < 0 ? number : WithCommonCoordinate(field.Node.Retrieve(), field.Type, axis, number);
                CommitMaterialSettingDraft(owner, field, proposed);
            }, () => AcquireMaterialSettingDraft(owner, field), text =>
            {
                if (type == typeof(decimal) && !ranged) return null;
                var stepped = text;
                return NativeNumericInput.StepText(type, Coordinate(owner.ReadMaterialSetting(field)), false, range, ref stepped) ? stepped : null;
            }, format: value => NativeNumericText.Format(Coordinate(value), type, range), controlLabel: label,
                externalText: ranged && display != null && !Equals(display, initial) ? NativeNumericText.Format(display, type, range) : null,
                externalActive: active, externalCommit: complete);
            if (Controls.Enabled) Controls.Record("material-settings/" + field.Key + (axis < 0 ? "" : "/" + axis));
        }
        catch (Exception error) { statusMessage = error.Message; }
    }
}
