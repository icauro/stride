using System.Globalization;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private object?[] ReadSpriteCommonNumericDraft(AssetMetadataDocument owner, SpriteCommonSetting setting)
        => [.. setting.Fields.Select(field => field.Node.Retrieve()),
            .. setting.Fields.SelectMany(field => owner.CaptureSpriteSettingDraft(field, false)),
            .. owner.SelectedSpriteIds.Cast<object?>(), owner.Path, sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation,
            sessionHistory?.Extensions?.Version];
    private bool CanEditSpriteCommonNumericDraft(AssetMetadataDocument owner, SpriteCommonSetting setting)
        => !IsAuthoringPipelineUpdating && owner.IsCurrentSpriteCommonSetting(setting)
            && setting.Fields.All(field => CanEditSpriteSettingDraft(owner, field));
    private IDisposable AcquireSpriteCommonNumericDraft(AssetMetadataDocument owner, SpriteCommonSetting setting)
    {
        var leases = new List<IDisposable>();
        try
        {
            if (!owner.IsCurrentSpriteCommonSetting(setting)) throw new InvalidOperationException("The sprite selection changed before editing.");
            leases.Add(AcquirePropertyStringOwner(owner));
            foreach (var field in setting.Fields)
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
    private void DrawSpriteCommonNumericDraft(AssetMetadataDocument owner, SpriteCommonSetting setting)
    {
        var first = setting.First;
        var dimensions = first.Type == typeof(Vector2) ? 2 : first.Type == typeof(Vector4) || first.Type == typeof(Rectangle) ? 4 : 1;
        var type = SpriteCoordinateType(first.Type);
        for (var axis = 0; axis < dimensions; axis++)
        {
            Gui.PushID(axis);
            try
            {
                var coordinate = axis;
                bool Editable() => CanEditSpriteCommonNumericDraft(owner, setting)
                    && (dimensions == 1 || setting.Fields.All(field => !owner.ExtensionNumericAxisReadOnly(field.Node, coordinate)));
                if (dimensions > 1) { Gui.TextUnformatted(first.Type == typeof(Rectangle) ? SpriteRectangleAxes[axis] : SpriteVectorAxes[axis]); Gui.SameLine(); }
                Gui.SetNextItemWidth(-1);
                var initial = SpriteCoordinate(first.Node.Retrieve()!, axis);
                var mixed = setting.Fields.Skip(1).Any(field => !Equals(SpriteCoordinate(field.Node.Retrieve()!, coordinate), initial));
                var row = GetSpriteCommonNumericRow(owner, setting, axis);
                var range = row.Merged;
                var ranged = dimensions == 1 && row.Ranges.All(candidate => NativeRangedNumericControl.Matches(type, candidate));
                object? display = initial; var active = false; var complete = false;
                var feedback = Gui.GetID("##Value");
                try
                {
                    if (ranged)
                    {
                        using var disabled = ImGuiScopes.Disabled(!Editable());
                        complete = NativeRangedNumericControl.Draw(first.Node, type, initial, mixed, row.Ranges, Gui.GetStyle().FontScaleDpi, out display, out _);
                        active = Gui.IsItemActive();
                    }
                    PropertyGrid.Strings.Draw(first.Node, mixed, Editable, () => ReadSpriteCommonNumericDraft(owner, setting), text =>
                    {
                        var parsed = PropertyConversionFeedback.Convert(feedback, () => NativeNumericText.Parse(text, type, range));
                        if (parsed is float single && !float.IsFinite(single) || parsed is double number && !double.IsFinite(number))
                            throw new FormatException("The numeric value must be finite.");
                        if (dimensions > 1 && parsed is float component) parsed = MathF.Round(component, 3);
                        if (!Editable()) throw new InvalidOperationException("The sprite selection is no longer editable.");
                        if (setting.Fields.All(field => Equals(SpriteCoordinate(field.Node.Retrieve()!, coordinate), parsed))) return;
                        UseTextureSettingsCommandContext();
                        owner.UpdateSpriteCommonValue(setting, value => WithSpriteCoordinate(value, first.Type, coordinate, parsed));
                        owner.EndSpriteCommonEdit();
                    }, () => AcquireSpriteCommonNumericDraft(owner, setting),
                        text => NativeNumericInput.StepText(type, SpriteCoordinate(first.Node.Retrieve()!, coordinate), mixed, range, ref text) ? text : null,
                        value => dimensions > 1 && SpriteCoordinate(value!, coordinate) is float component
                            ? MathF.Round(component, 3).ToString(CultureInfo.InvariantCulture)
                            : NativeNumericText.Format(SpriteCoordinate(value!, coordinate), type, range), controlLabel: "##Value",
                        externalText: ranged && display != null && (active || complete) && (mixed || !Equals(initial, display)) ? NativeNumericText.Format(display, type, range) : null,
                        externalActive: active, externalCommit: complete);
                }
                catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
                PropertyConversionFeedback.Draw(feedback);
            }
            finally { Gui.PopID(); }
        }
    }
}
