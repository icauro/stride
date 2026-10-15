using Stride.Core.Mathematics;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawSpriteCommonColor(AssetMetadataDocument owner, SpriteCommonSetting setting, bool editable)
    {
        object?[] Read() => ReadSpriteCommonNumericDraft(owner, setting);
        bool Editable() => editable && CanEditSpriteCommonNumericDraft(owner, setting);
        void Apply(Func<object?, object?> create)
        {
            if (!Editable()) throw new InvalidOperationException("The sprite color selection changed.");
            using var lease = new RetainedPathLease(this, AcquireSpriteCommonNumericDraft(owner, setting));
            if (setting.Fields.All(field => Equals(field.Node.Retrieve(), create(field.Node.Retrieve())))) return;
            UseTextureSettingsCommandContext();
            owner.UpdateSpriteCommonValue(setting, value => create(value)!);
            owner.EndSpriteCommonEdit();
        }
        if (TryDrawSpritePrimitive(typeof(Color), SpritePropertyLabel(owner, setting.First),
                Read, Editable, Apply, () => AcquireSpriteCommonNumericDraft(owner, setting), setting.Fields.Length)) return;
        DrawCertifiedSpriteColor(setting.First.Node, Read, Editable, Apply,
            () => AcquireSpriteCommonNumericDraft(owner, setting),
            axis => setting.Fields.All(field => !owner.ExtensionNumericAxisReadOnly(field.Node, axis)),
            valueCount: setting.Fields.Length);
    }
}
