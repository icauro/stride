using System.Globalization;
using Hexa.NET.ImGui;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool IsFontSettingCurrent(AssetMetadataDocument owner, FontSettingField field)
        => IsCurrentMetadataControlOwner(owner)
        && sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is { IsPublished: true } state && ReferenceEquals(state.Document, owner)
        && owner.IsCurrentFontSettingField(field);

    private bool CanEditRetainedFontSetting(AssetMetadataDocument owner, FontSettingField field)
        => !IsAuthoringPipelineUpdating && IsFontSettingCurrent(owner, field) && owner.CanEditFontSetting(field);

    private object?[] ReadFontSettingDraft(AssetMetadataDocument owner, FontSettingField field)
        => [field.Node.Retrieve(), field.Node.Parent.Retrieve(), field.Node, owner.EditRevision, owner.Path, field.Key,
            field.Node.Retrieve() is DateTime date ? date.Kind : null,
            sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation, sessionHistory?.Extensions?.Version];

    private IDisposable AcquireFontSettingDraft(AssetMetadataDocument owner, FontSettingField field)
    {
        var leases = new List<IDisposable>();
        try
        {
            if (!IsFontSettingCurrent(owner, field)) throw new InvalidOperationException("The font setting is no longer current.");
            leases.Add(AcquirePropertyStringOwner(owner));
            if (owner.AcquireExtensionCommandTarget(field.Node) is { } target) leases.Add(target);
            return new PropertyStringOwners(leases, [owner]);
        }
        catch (Exception failure)
        {
            var cleanup = new PropertyStringOwners(leases, [owner]);
            try { cleanup.Dispose(); }
            catch (Exception release) { packageCommandCleanup.Add(cleanup.Dispose); throw new AggregateException(failure, release); }
            throw;
        }
    }

    private void CommitFontSettingDraft(AssetMetadataDocument owner, FontSettingField field, object? value)
    {
        if (!CanEditRetainedFontSetting(owner, field)) throw new InvalidOperationException("The font setting is no longer editable.");
        if (ContentAssetReference.AreEqual(field.Node.Retrieve(), value)) return;
        UseTextureSettingsCommandContext();
        owner.UpdateFontSetting(field, value);
        owner.EndFontSettingEdit();
    }

    private bool DrawRetainedFontSetting(AssetMetadataDocument owner, FontSettingField field, string label)
    {
        bool Editable() => CanEditRetainedFontSetting(owner, field);
        object?[] Read() => ReadFontSettingDraft(owner, field);
        IDisposable Acquire() => AcquireFontSettingDraft(owner, field);
        void Commit(object? value) => CommitFontSettingDraft(owner, field, value);
        if (field.Type == typeof(UFile))
        {
            Gui.TextUnformatted(label); Gui.SameLine();
            DrawRetainedPath(owner, field.Node, field.Type, Editable, Read, path => Commit(path), 1, Acquire);
            return true;
        }
        if (field.Type == typeof(char))
        {
            Gui.TextUnformatted(label); Gui.SameLine();
            DrawRetainedCharacter(field.Node, Read, Editable, character => Commit(character), Acquire);
            return true;
        }
        if (field.Type == typeof(Vector2) || field.Type == typeof(Rectangle))
        {
            Gui.TextUnformatted(label);
            DrawFontCoordinateDrafts(owner, field, Editable, Read, Commit, Acquire);
            return true;
        }
        if (field.Type == typeof(string))
        {
            try { PropertyGrid.Strings.Draw(field.Node, false, Editable, Read, text => Commit(text), Acquire, controlLabel: label); }
            catch (Exception error) { statusMessage = error.Message; }
            return true;
        }
        if (!AssetSettingNumericMetadata.Supports(field.Type)) return false;
        var range = owner.FontNumericRange(field);
        var initial = field.Node.Retrieve(); object? display = initial;
        var ranged = NativeRangedNumericControl.Matches(field.Type, range);
        var active = false; var complete = false;
        var feedback = Gui.GetID(label);
        try
        {
            if (ranged)
            {
                using var disabled = ImGuiScopes.Disabled(!Editable());
                complete = NativeRangedNumericControl.Draw(field.Node, field.Type, initial, false, [range], UiScale(1), out display, out _);
                active = Gui.IsItemActive();
            }
            PropertyGrid.Strings.Draw(field.Node, false, Editable, Read,
                text => Commit(PropertyConversionFeedback.Convert(feedback, () => field.Type == typeof(decimal) && range == null
                    ? (object)decimal.Parse(text, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture)
                    : NativeNumericText.Parse(text, field.Type, range))), Acquire,
                text => field.Type == typeof(decimal) && range == null ? null
                    : NativeNumericInput.StepText(field.Type, field.Node.Retrieve(), false, range, ref text) ? text : null,
                format: value => NativeNumericText.Format(value, field.Type, range), controlLabel: label,
                externalText: ranged && display != null && !Equals(display, initial) ? NativeNumericText.Format(display, field.Type, range) : null,
                externalActive: active, externalCommit: complete);
        }
        catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
        PropertyConversionFeedback.Draw(feedback);
        return true;
    }

    private void DrawFontCoordinateDrafts(AssetMetadataDocument owner, FontSettingField field, Func<bool> canEdit,
        Func<object?[]> read, Action<object?> commit, Func<IDisposable> acquire)
    {
        var names = NativeNumericAxes.Names(field.Type);
        if (!Gui.BeginTable("##FontCoordinates", names.Count, ImGuiTableFlags.SizingStretchSame)) return;
        try
        {
            for (var axis = 0; axis < names.Count; axis++)
            {
                Gui.TableNextColumn(); Gui.PushID(axis);
                try
                {
                    var coordinate = axis;
                    var scalarType = field.Type == typeof(Rectangle) ? typeof(int) : typeof(float);
                    var range = owner.ExtensionAxisNumericRanges(field.Node).ElementAtOrDefault(axis);
                    bool Editable() => canEdit() && !owner.ExtensionNumericAxisReadOnly(field.Node, coordinate);
                    if (names[axis] is "X" or "Y") DrawInspectorAxisBadge(axis); else Gui.TextUnformatted(names[axis]);
                    Gui.SetNextItemWidth(-1);
                    var feedback = Gui.GetID("##Axis");
                    try
                    {
                        PropertyGrid.Strings.Draw(field.Node, false, Editable, read, text =>
                        {
                            var number = PropertyConversionFeedback.Convert(feedback, () => NativeNumericText.Parse(text, scalarType, range));
                            // The stock Vector2Editor template sets DecimalPlaces="3".
                            if (number is float single) number = MathF.Round(single, 3);
                            commit(ReplaceVirtualMathAxis(field.Node.Retrieve()!, coordinate, Convert.ToDouble(number, CultureInfo.InvariantCulture)));
                        }, acquire,
                            text => NativeNumericInput.StepText(scalarType, ReadVirtualMathAxis(field.Node.Retrieve(), coordinate), false, range, ref text) ? text : null,
                            value => ReadVirtualMathAxis(value, coordinate) is float single
                                ? MathF.Round(single, 3).ToString(CultureInfo.InvariantCulture)
                                : NativeNumericText.Format(ReadVirtualMathAxis(value, coordinate), scalarType, range), controlLabel: "##Axis");
                    }
                    catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
                    PropertyConversionFeedback.Draw(feedback);
                }
                finally { Gui.PopID(); }
            }
        }
        finally { Gui.EndTable(); }
    }
}
