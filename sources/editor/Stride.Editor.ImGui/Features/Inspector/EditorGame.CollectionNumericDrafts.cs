using System.Globalization;
using Stride.Core.Assets.Quantum;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool UsesCollectionNumericDraft(CollectionItem item) => AssetSettingNumericMetadata.Supports(item.Type)
        || IsPathType(item.Type) || IsTemporalType(item.Type) || item.Type == typeof(char) || item.Type == typeof(Guid) || item.Type == typeof(AngleSingle) || item.Type == typeof(Quaternion) || item.Type == typeof(Vector2) || item.Type == typeof(Vector3) || item.Type == typeof(Vector4)
        || item.Type == typeof(Int2) || item.Type == typeof(Int3) || item.Type == typeof(Int4) || item.Type == typeof(UInt4)
        || item.Type == typeof(Matrix) || item.Type == typeof(Rectangle) || item.Type == typeof(RectangleF);

    private void DrawCollectionNumericDrafts(CollectionItem item)
    {
        var owner = document!;
        if (IsPathType(item.Type))
        {
            DrawRetainedPath(owner, item.Collection, item.Type,
                () => ReferenceEquals(document, owner) && item.Collection.HasId(item.Id) && ReferenceEquals(item.Field.Node.Target, item.Collection)
                    && owner.CanEditCollection(item.Field) && owner.CanEditHierarchyItem(item.Collection, item.Index),
                () => [item.Value, item.Index, item.Id],
                path => { owner.UpdateCollectionItem(item, path); owner.EndCollectionEdit(); },
                1, () => AcquirePropertyStringOwners([owner]));
            return;
        }
        if (IsTemporalType(item.Type))
        {
            DrawRetainedTemporal(item.Collection, item.Type,
                () => ReferenceEquals(document, owner) && item.Collection.HasId(item.Id) && ReferenceEquals(item.Field.Node.Target, item.Collection)
                    && owner.CanEditCollection(item.Field) && owner.CanEditHierarchyItem(item.Collection, item.Index),
                () => [item.Value, item.Index, item.Id],
                transform => { owner.UpdateCollectionItem(item, transform(item.Value)); owner.EndCollectionEdit(); },
                1, () => AcquirePropertyStringOwners([owner]));
            return;
        }
        if (item.Type == typeof(char))
        {
            DrawRetainedCharacter(item.Collection, () => [item.Value, item.Index, item.Id],
                () => ReferenceEquals(document, owner) && item.Collection.HasId(item.Id) && ReferenceEquals(item.Field.Node.Target, item.Collection)
                    && owner.CanEditCollection(item.Field) && owner.CanEditHierarchyItem(item.Collection, item.Index),
                character => { owner.UpdateCollectionItem(item, character); owner.EndCollectionEdit(); }, () => AcquirePropertyStringOwners([owner]));
            return;
        }
        if (item.Type == typeof(Guid))
        {
            DrawRetainedGuid(item.Collection, () => [item.Value, item.Index, item.Id],
                () => ReferenceEquals(document, owner) && item.Collection.HasId(item.Id) && ReferenceEquals(item.Field.Node.Target, item.Collection)
                    && owner.CanEditCollection(item.Field) && owner.CanEditHierarchyItem(item.Collection, item.Index),
                guid => { owner.UpdateCollectionItem(item, guid); owner.EndCollectionEdit(); }, () => AcquirePropertyStringOwners([owner]));
            return;
        }
        if (item.Type == typeof(AngleSingle))
        {
            DrawRetainedAngle(item.Collection, () => [item.Value, item.Index, item.Id],
                () => ReferenceEquals(document, owner) && item.Collection.HasId(item.Id) && ReferenceEquals(item.Field.Node.Target, item.Collection)
                    && owner.CanEditCollection(item.Field) && owner.CanEditHierarchyItem(item.Collection, item.Index),
                angle => { owner.UpdateCollectionItem(item, angle); owner.EndCollectionEdit(); }, () => AcquirePropertyStringOwners([owner]));
            return;
        }
        if (AssetSettingNumericMetadata.Supports(item.Type)) Draw(-1);
        else
        {
            var rotation = item.Type == typeof(Quaternion) ? RotationEditor(item, Gui.GetID("##Rotation")) : null;
            var dimensions = rotation != null ? 3 : CommonValueDimensions(item.Type);
            var tableRequired = UsesExtendedMathTable(item.Type);
            var table = tableRequired && Gui.BeginTable("##Matrix", 4, Hexa.NET.ImGui.ImGuiTableFlags.SizingStretchSame);
            for (var axis = 0; axis < dimensions && (!tableRequired || table); axis++)
            {
                Gui.PushID(axis);
                if (rotation != null) DrawInspectorAxisBadge(axis);
                else if (tableRequired || item.Type == typeof(UInt4)) DrawExtendedMathCoordinateLabel(item.Type, axis, table);
                Gui.SetNextItemWidth(-1); Draw(axis, rotation); Gui.PopID();
            }
            if (table) Gui.EndTable();
        }
        void Draw(int axis, NativeRotationEditorState? rotation = null)
        {
            var type = PropertyCoordinateType(item.Type, axis);
            var range = axis < 0 ? owner.HierarchyItemNumericRange(item) : rotation == null ? item.AxisNumericRanges.ElementAtOrDefault(axis) : null;
            bool Editable() => ReferenceEquals(document, owner) && !IsAuthoringPipelineUpdating
                && item.Field.Component.Entity is { } entity && owner.IsSelected(entity.Id)
                && ReferenceEquals(item.Field.Node.Target, item.Collection)
                && item.Collection.HasId(item.Id)
                && owner.CanEditCollection(item.Field) && owner.CanEditHierarchyItem(item.Collection, item.Index)
                && (axis < 0 || rotation != null || !owner.IsHierarchyItemAxisReadOnly(item, axis));
            object? Coordinate(object? value) => axis < 0 ? value : rotation != null && value is Quaternion quaternion
                ? rotation.Read(quaternion)[axis] : CommonCoordinate(value, axis);
            var value = item.Value;
            var ranged = axis < 0 && NativeRangedNumericControl.Matches(item.DeclaredType, range);
            object? display = value; var sliderActive = false; var sliderCommit = false;
            if (ranged)
            {
                using var disabled = ImGuiScopes.Disabled(!Editable());
                sliderCommit = NativeRangedNumericControl.Draw(item.Collection, type, value, false, [range], UiScale(1), out display, out _);
                sliderActive = Gui.IsItemActive();
            }
            var feedback = Gui.GetID("##Value");
            string Format(object? raw) => Coordinate(raw) is float number && axis >= 0
                ? number.ToString("F" + (range?.DecimalPlaces ?? (rotation != null || UsesExtendedMathTable(item.Type) ? 3 : 6)), CultureInfo.InvariantCulture)
                : Convert.ToString(Coordinate(raw), CultureInfo.InvariantCulture) ?? "";
            try
            {
                PropertyGrid.Strings.Draw(item.Collection, false, Editable, () => [item.Value, item.Index, item.Id], text =>
                {
                    var snapshot = rotation?.Capture();
                    try
                    {
                        var number = PropertyConversionFeedback.Convert(feedback, () => type == typeof(decimal) && !ranged
                            ? (object)decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture)
                            : NativeNumericText.Parse(text, type, range));
                        if (number is float scalar && !float.IsFinite(scalar)) throw new ArgumentException("The coordinate must be finite.");
                        var current = item.Value;
                        var next = axis < 0 ? number : rotation != null && current is Quaternion quaternion
                            ? rotation.ReplaceAxis(quaternion, axis, (float)number) : WithCommonCoordinate(current, item.Type, axis, number);
                        owner.UpdateCollectionItem(item, next); owner.EndCollectionEdit();
                    }
                    catch { if (snapshot != null) rotation!.Restore(snapshot); throw; }
                }, () => AcquirePropertyStringOwners([owner]), text =>
                {
                    if (type == typeof(decimal) && !ranged) return null;
                    return NativeNumericInput.StepText(type, Coordinate(item.Value), false, range, ref text) ? text : null;
                }, Format, externalText: ranged && display != null && !Equals(display, value) ? Format(display) : null,
                    externalActive: sliderActive, externalCommit: sliderCommit,
                    onCancelled: error => ClearCancelledPropertyDraft(error, feedback));
            }
            catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); SetPropertyDraftDiagnostic(error); }
            PropertyConversionFeedback.Draw(feedback);
        }
    }
}
