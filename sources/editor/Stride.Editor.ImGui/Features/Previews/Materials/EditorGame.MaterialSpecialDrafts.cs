using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool SupportsMaterialSpecialDraft(Type type)
        => type == typeof(Int2) || type == typeof(Int3) || type == typeof(Int4) || type == typeof(Quaternion)
        || UsesExtendedMathTable(type) || type == typeof(Guid) || type == typeof(char) || IsTemporalType(type)
        || type == typeof(AngleSingle) || IsPathType(type);
    private bool TryDrawMaterialSpecialDraft(AssetMetadataDocument owner, MaterialSettingField field, string label)
    {
        if (!SupportsMaterialSpecialDraft(field.Type)) return false;
        var nullableStruct = Nullable.GetUnderlyingType(field.Node.Type) is { } nullable && !nullable.IsPrimitive && !nullable.IsEnum;
        using var nullableRow = NativeNullableRow.Begin("##NullableMaterial", nullableStruct);
        if (nullableStruct)
        {
            DrawMaterialNullableSpecialPresence(owner, field);
            nullableRow.ValueColumn();
            if (owner.ReadMaterialSetting(field) == null)
            {
                Gui.TextUnformatted(EditorLocalization.Text("(null)"));
                return true;
            }
        }
        if (!label.StartsWith("##", StringComparison.Ordinal)) { Gui.TextUnformatted(label); Gui.SameLine(); }
        bool Editable() => CanEditMaterialSettingDraft(owner, field);
        object?[] Read() => ReadMaterialSettingDraft(owner, field);
        IDisposable Acquire() => AcquireMaterialSettingDraft(owner, field);
        void Commit(object? value) => CommitMaterialSettingDraft(owner, field, value);
        if (IsPathType(field.Type)) DrawRetainedPath(owner, field.Node, field.Type, Editable, Read, value => Commit(value), 1, Acquire);
        else if (field.Type == typeof(Guid)) DrawRetainedGuid(field.Node, Read, Editable, value => Commit(value), Acquire);
        else if (field.Type == typeof(char)) DrawRetainedCharacter(field.Node, Read, Editable, value => Commit(value), Acquire,
            assignGlyph: value => Commit(value == '\0' && Nullable.GetUnderlyingType(field.Node.Type) != null ? null : value));
        else if (field.Type == typeof(AngleSingle)) DrawRetainedAngle(field.Node, Read, Editable, value => Commit(value), Acquire);
        else if (IsTemporalType(field.Type)) DrawRetainedTemporal(field.Node, field.Type,
            () => Editable() && Read()[0] != null, Read, transform => Commit(transform(Read()[0])), 1, Acquire);
        else
        {
            var ranges = owner.ExtensionAxisNumericRanges(field.Node);
            DrawRetainedImportMath(field.Node, field.Type, Read, Editable, Commit, Acquire,
                axis => ranges.ElementAtOrDefault(axis), axis => owner.ExtensionNumericAxisReadOnly(field.Node, axis));
        }
        return true;
    }
}
