using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class UIEditorPanel
{
    private NativePresenterCategories CreateUIPresenterCategories(UIDocument source, IReadOnlyList<IReadOnlyList<Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter>> groups)
        => new NativePresenterCategories(presenter =>
        {
            var group = groups.FirstOrDefault(group => ReferenceEquals(group[0], presenter));
            if (group == null || !Gui.BeginPopupContextItem("Category actions")) return;
            try { host.DrawCombinedVirtualCommands(source, group); }
            finally { Gui.EndPopup(); }
        }, presenter => groups.FirstOrDefault(group => ReferenceEquals(group[0], presenter)) is { } group
            ? group.Any(node => node.IsVisible) : presenter.IsVisible);
    private void DrawNativeExtensionRows(UIDocument source, IReadOnlyList<IReadOnlyList<Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter>> groups, NativePresenterCategories categories)
    {
        var ids = selected.ToArray();
        foreach (var presenters in groups)
        {
            var first = presenters[0];
            if (!categories.Before(first, includeMemberCategories: false)) continue;
            var keys = new Stack<string>();
            for (Stride.Core.Presentation.Quantum.Presenters.INodePresenter? current = first; current?.Parent != null; current = current.Parent)
                keys.Push(current.CombineKey);
            var identity = "virtual/" + string.Join("/", keys.Select(key => key.Length + ":" + key));
            using var targets = source.GuardUIRenderTarget(source.AcquireCombinedVirtualTarget(presenters));
            using var row = new PropertyRowScope(identity, first.DisplayName);
            if (!row.DrawContents) continue;
            var values = presenters.Select(source.ReadExtensionVirtualProperty).ToArray();
            var entries = source.CommonUIVirtualEntries(presenters);
            if (first.Type == typeof(string) && entries == null)
                host.DrawRetainedStringProperty(source, first, values.Skip(1).Any(value => !Equals(value, values[0])),
                    () => presenters.All(source.CanEditExtensionVirtualProperty),
                    () => presenters.Select(source.ReadExtensionVirtualProperty).ToArray(),
                    replacement => source.TransformExtensionVirtualProperties(presenters, _ => replacement),
                    () => source.AcquireCombinedVirtualTarget(presenters));
            else DrawUIValue(identity, first.DisplayName, first.Type, values[0], values,
                presenters.All(source.CanEditExtensionVirtualProperty), !first.Type.IsValueType || Nullable.GetUnderlyingType(first.Type) != null,
                transform => source.TransformExtensionVirtualProperties(presenters, transform), source.ExtensionNumericRange(first), entries,
                entry => QueueUIPropertyCommand(source, ids, () => host.SelectUIVirtualFactoryEntry(source, presenters, entry)),
                numericRangeProvider: () => NativeNumericInput.Merge(Nullable.GetUnderlyingType(first.Type) ?? first.Type, presenters.Select(source.ExtensionNumericRange)),
                axisNumericRangeProvider: axis => NativeNumericInput.Merge(PropertyCoordinateTypeForUI(first.Type), presenters.Select(presenter => source.UIAxisNumericRanges(presenter).ElementAtOrDefault(axis))),
                axisCanEditProvider: axis => presenters.All(presenter => !source.IsUIAxisReadOnly(presenter, axis)),
                rangedRangesProvider: () => presenters.Select(source.ExtensionNumericRange).ToArray(),
                valueTarget: VirtualValueTarget(source, presenters));
            if (!Gui.BeginPopupContextItem("Virtual property actions")) continue;
            try
            {
                if (ContentAssetReference.Supports(first.Type)) host.DrawReferenceFetchActions(values);
                if (host.DrawMenuItem("Copy", "", false, presenters.Count == 1 && source.CanCopyVirtualProperty(first)))
                    QueueUIPropertyCommand(source, ids, () => Gui.SetClipboardText(source.CopyVirtualProperty(first)), writable: false);
                var text = Gui.GetClipboardTextS() ?? "";
                var canPaste = presenters.All(presenter => source.CanPasteVirtualProperty(presenter, text));
                if (host.DrawMenuItem("Paste", "", false, canPaste)) Paste(false);
                if (host.DrawMenuItem("Replace", "", false, canPaste)) Paste(true);
                host.DrawCombinedVirtualCommands(source, presenters);
                void Paste(bool replace) => QueueUIPropertyCommand(source, ids, () => source.BatchCollectionEdits(() =>
                { foreach (var presenter in presenters) source.PasteVirtualProperty(presenter, text, replace); }, replace ? "Replace property" : "Paste property"));
            }
            finally { Gui.EndPopup(); }
        }
    }
}
