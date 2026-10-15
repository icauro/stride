using System.Runtime.CompilerServices;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class SpriteColorPopupCertificate { internal object?[]? Source; }
    private readonly ConditionalWeakTable<object, Dictionary<uint, SpriteColorPopupCertificate>> spriteColorPopupCertificates = new();
    private void DrawCertifiedSpriteColor(object identity, Func<object?[]> read, Func<bool> editable,
        Action<Func<object?, object?>> apply, Func<IDisposable> acquire, Func<int, bool> axisEditable, int valueCount = 1)
    {
        var id = Gui.GetID("##Color");
        var views = spriteColorPopupCertificates.GetValue(identity, _ => new());
        if (!views.TryGetValue(id, out var view)) views.Add(id, view = new());
        var source = read();
        if (Gui.IsPopupOpen("##Color") && view.Source != null && !view.Source.SequenceEqual(source))
        {
            if (Gui.BeginPopup("##Color"))
            {
                try { Gui.CloseCurrentPopup(); }
                finally { Gui.EndPopup(); }
            }
        }
        view.Source = source;
        DrawRetainedColor(identity, typeof(Color), read, editable, create =>
        {
            if (!editable() || !view.Source.SequenceEqual(read())) throw new InvalidOperationException("The sprite property source changed.");
            apply(create);
            view.Source = read();
        }, acquire, axisEditable, valueCount: valueCount);
    }
    private void DrawSpriteColorDraft(AssetMetadataDocument owner, SpriteSettingField field)
    {
        object?[] Read() => ReadSpriteSettingDraft(owner, field);
        bool Editable() => CanEditSpriteSettingDraft(owner, field);
        void Apply(Func<object?, object?> create)
        {
            if (!Editable()) throw new InvalidOperationException("The sprite color owner changed.");
            using var lease = new RetainedPathLease(this, AcquireSpriteSettingDraft(owner, field));
            CommitSpriteSettingDraft(owner, field, create(field.Node.Retrieve())!);
        }
        if (TryDrawSpritePrimitive(typeof(Color), SpritePropertyLabel(owner, field), Read, Editable,
                Apply, () => AcquireSpriteSettingDraft(owner, field))) return;
        DrawCertifiedSpriteColor(field.Node, Read, Editable, Apply,
            () => AcquireSpriteSettingDraft(owner, field), axis => !owner.ExtensionNumericAxisReadOnly(field.Node, axis));
    }

    private bool TryDrawSpritePrimitive(Type type, string label, Func<object?[]> read, Func<bool> editable,
        Action<Func<object?, object?>> apply, Func<IDisposable> acquire, int valueCount = 1)
    {
        var extensions = sessionHistory?.Extensions;
        if (extensions?.HasPrimitiveEditor(type) != true) return false;
        using var lease = new RetainedPathLease(this, acquire());
        var expected = read();
        // The provider receives the real typed value; its callback cannot outlive this render invocation.
        var drawing = true;
        try
        {
            return extensions.TryDrawPrimitive(new EditorPropertyEditorContext(type, expected.Take(valueCount).Any(value => !Equals(value, expected[0])) ? null : expected[0], label, editable(), value =>
            {
                if (!drawing || !editable()) throw new InvalidOperationException("The sprite property provider context changed.");
                if (expected.Take(valueCount).All(current => Equals(current, value))) return;
                ValidatePendingPropertyStrings();
                if (!editable() || !expected.SequenceEqual(read())) throw new InvalidOperationException("The sprite property source changed.");
                apply(_ => value);
                expected = read();
            }, () =>
            {
                if (!drawing || !expected.SequenceEqual(read())) throw new InvalidOperationException("The sprite property provider context changed.");
            }));
        }
        finally { drawing = false; }
    }
}
