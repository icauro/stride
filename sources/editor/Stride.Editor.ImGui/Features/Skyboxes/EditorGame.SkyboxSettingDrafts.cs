using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSkyboxNumberDraft(AssetMetadataDocument owner, SkyboxSettingField field)
    {
        bool Editable() => !IsAuthoringPipelineUpdating && IsCurrentMetadataControlOwner(owner)
            && sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is { IsPublished: true } state && ReferenceEquals(state.Document, owner)
            && owner.SkyboxSettingsFields.Contains(field) && owner.CanEditSkyboxSettings && field.CanAssign
            && owner.CanEditAssetSettingNode(field.Node) && !owner.IsSkyboxSettingReadOnly(field);
        object?[] Read() => [owner.ReadSkyboxSetting(field), field.Node.Retrieve(), field.Node, owner.EditRevision,
            owner.SkyboxSettings, owner.SessionHistory?.Extensions?.Version];
        IDisposable Acquire()
        {
            var leases = new List<IDisposable>();
            try
            {
                if (!Editable()) throw new InvalidOperationException("The skybox setting is no longer editable.");
                leases.Add(AcquirePropertyStringOwner(owner));
                if (owner.AcquireExtensionCommandTarget(field.Node) is { } target) leases.Add(target);
                return new PropertyStringOwners(leases, [owner]);
            }
            catch { foreach (var lease in leases) packageCommandCleanup.Add(lease.Dispose); throw; }
        }
        var range = owner.SkyboxSettingRange(field);
        var ranged = NativeRangedNumericControl.Matches(field.Type, range);
        var initial = owner.ReadSkyboxSetting(field); object? display = initial;
        var active = false; var complete = false;
        try
        {
            if (ranged)
            {
                using var disabled = ImGuiScopes.Disabled(!Editable());
                complete = NativeRangedNumericControl.Draw(field.Node, field.Type, initial, false, [range], UiScale(1), out display, out _);
                active = Gui.IsItemActive();
            }
            PropertyGrid.Strings.Draw(field.Node, false, Editable, Read, text =>
            {
                var number = NativeNumericText.Parse(text, field.Type, range);
                if (number is float single && !float.IsFinite(single) || number is double real && !double.IsFinite(real))
                    throw new FormatException("The numeric value must be finite.");
                if (!Editable()) throw new InvalidOperationException("The skybox setting is no longer editable.");
                if (Equals(field.Node.Retrieve(), number)) return;
                UseTextureSettingsCommandContext(); owner.UpdateSkyboxSetting(field, number); owner.EndSkyboxSettingEdit();
            }, Acquire, text =>
            {
                if (field.Type == typeof(decimal) && !ranged) return null;
                var stepped = text;
                return NativeNumericInput.StepText(field.Type, owner.ReadSkyboxSetting(field), false, range, ref stepped) ? stepped : null;
            }, format: value => NativeNumericText.Format(value, field.Type, range),
                externalText: ranged && display != null && !Equals(display, initial) ? NativeNumericText.Format(display, field.Type, range) : null,
                externalActive: active, externalCommit: complete);
        }
        catch (Exception error) { statusMessage = error.Message; }
        if (Controls.Enabled) Controls.Record("skybox-settings/" + field.Node.Name);
    }
}
