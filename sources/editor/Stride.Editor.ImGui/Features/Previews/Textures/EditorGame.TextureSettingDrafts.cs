using Stride.Assets.Textures;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool IsTextureSettingCurrent(AssetMetadataDocument owner, TextureSettingField field)
        => IsCurrentMetadataControlOwner(owner)
        && sessionHistory?.Registry.Find(owner.AuthoredAsset.Id) is { IsPublished: true } state && ReferenceEquals(state.Document, owner)
        && owner.TextureSettingsFields.Any(current => current.Key == field.Key && ReferenceEquals(current.Node, field.Node));

    private bool CanEditRetainedTextureSetting(AssetMetadataDocument owner, TextureSettingField field)
        => !IsAuthoringPipelineUpdating && IsTextureSettingCurrent(owner, field) && owner.CanEditTextureSettings
        && owner.CanEditAssetSettingNode(field.Node) && AssetMetadataDocument.CanAssignAuthoringNode(field.Node)
        && !owner.IsExtensionPropertyReadOnly(field.Node);

    private object?[] ReadTextureSettingDraft(AssetMetadataDocument owner, TextureSettingField field)
        => [field.Node.Retrieve(), field.Node, owner.EditRevision, owner.TextureSettings?.Type, owner.TextureSettings?.IsSizeInPercentage];

    private IDisposable AcquireTextureSettingDraft(AssetMetadataDocument owner, TextureSettingField field)
    {
        var leases = new List<IDisposable>();
        try
        {
            if (!IsTextureSettingCurrent(owner, field)) throw new InvalidOperationException("The texture setting is no longer current.");
            leases.Add(AcquirePropertyStringOwner(owner));
            if (owner.AcquireExtensionCommandTarget(field.Node) is { } target) leases.Add(target);
            return new PropertyStringOwners(leases, [owner]);
        }
        catch { foreach (var lease in leases) packageCommandCleanup.Add(lease.Dispose); throw; }
    }

    private void CommitTextureSettingDraft(AssetMetadataDocument owner, TextureSettingField field, object value)
    {
        if (!CanEditRetainedTextureSetting(owner, field)) throw new InvalidOperationException("The texture setting is no longer editable.");
        if (Equals(field.Node.Retrieve(), value)) return;
        UseTextureSettingsCommandContext();
        owner.UpdateTextureSetting(field, value);
        owner.EndTextureSettingEdit();
    }

    private void DrawTextureNumberDraft(AssetMetadataDocument owner, TextureSettingField field, string label)
    {
        var absolute = field.Key is nameof(TextureAsset.Width) or nameof(TextureAsset.Height)
            && owner.TextureSettings?.IsSizeInPercentage == false;
        // The native AbsoluteWidth/AbsoluteHeight virtual presenters store the
        // original float member with 0..float.MaxValue and integer precision.
        var range = absolute
            ? new PropertyNumericRange(new PropertyNumericMetadata(0, float.MaxValue, DecimalPlaces: 0), typeof(float))
            : owner.ExtensionNumericRange(field.Node) ?? AssetSettingNumericMetadata.Read(field.Node);
        var caption = absolute ? field.Key == nameof(TextureAsset.Width) ? "Absolute width" : "Absolute height" : label;
        bool Editable() => CanEditRetainedTextureSetting(owner, field);
        var initial = field.Node.Retrieve(); object? display = initial;
        var ranged = !absolute && NativeRangedNumericControl.Matches(field.Type, range);
        var active = false; var complete = false;
        try
        {
            if (ranged)
            {
                using var disabled = ImGuiScopes.Disabled(!Editable());
                complete = NativeRangedNumericControl.Draw(field.Node, field.Type, initial, false, [range], UiScale(1), out display, out _);
                active = Gui.IsItemActive();
            }
            PropertyGrid.Strings.Draw(field.Node, false, Editable, () => ReadTextureSettingDraft(owner, field), text =>
            {
                var number = NativeNumericText.Parse(text, field.Type, range);
                if (number is float single && !float.IsFinite(single)) throw new FormatException("The numeric value must be finite.");
                CommitTextureSettingDraft(owner, field, number);
            }, () => AcquireTextureSettingDraft(owner, field), text =>
            {
                var stepped = text;
                return NativeNumericInput.StepText(field.Type, field.Node.Retrieve(), false, range, ref stepped) ? stepped : null;
            }, format: value => NativeNumericText.Format(value, field.Type, range), controlLabel: caption,
                externalText: ranged && display != null && !Equals(display, initial) ? NativeNumericText.Format(display, field.Type, range) : null,
                externalActive: active, externalCommit: complete);
        }
        catch (Exception error) { statusMessage = error.Message; }
    }

    private void DrawTexturePathDraft(AssetMetadataDocument owner, TextureSettingField field, string label)
    {
        Gui.TextUnformatted(label); Gui.SameLine();
        DrawRetainedPath(owner, field.Node, typeof(UFile), () => CanEditRetainedTextureSetting(owner, field),
            () => ReadTextureSettingDraft(owner, field), value => CommitTextureSettingDraft(owner, field, value), 1,
            () => AcquireTextureSettingDraft(owner, field),
            () => { if (Controls.Enabled) Controls.Record("texture-settings/" + field.Key); });
    }

    private void DrawTextureColorDraft(AssetMetadataDocument owner, TextureSettingField field, string label)
    {
        Gui.TextUnformatted(label); Gui.SameLine();
        DrawRetainedColor(field.Node, typeof(Color), () => ReadTextureSettingDraft(owner, field),
            () => CanEditRetainedTextureSetting(owner, field),
            create => CommitTextureSettingDraft(owner, field, create(field.Node.Retrieve())!),
            () => AcquireTextureSettingDraft(owner, field),
            axis => !owner.ExtensionNumericAxisReadOnly(field.Node, axis),
            () => DrawAssetSettingResetToBase(owner, field.Node),
            Controls.Enabled ? part =>
            {
                Controls.Record("texture-settings/" + field.Key + "/" + part);
                if (part == "hex") Controls.Record("texture-settings/" + field.Key);
            } : null);
    }
}
