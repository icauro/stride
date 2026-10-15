using System.Runtime.CompilerServices;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawProceduralVirtualProperty(ProceduralAssetDocument asset, VirtualNodePresenter presenter,
        NativePresenterCategories categories, HashSet<IObjectNode>? ancestors)
    {
        Gui.PushID(RuntimeHelpers.GetHashCode(presenter));
        EditorPresenterTarget? lease = null;
        try
        {
            lease = asset.AcquirePresenterTarget(presenter);
            if (!PresenterPathVisible(presenter) || !categories.Before(presenter, includeMemberCategories: false)) return;
            var label = presenter.DisplayName == presenter.Name ? Stride.Core.Presentation.Core.Utils.SplitCamelCase(presenter.Name) : presenter.DisplayName;
            if (!BeginInspectorPropertyRow(label)) return;
            try
            {
                bool Editable() => !IsAuthoringPipelineUpdating && ReferenceEquals(assetDocument, asset) && asset.CanEditVirtualProperty(presenter);
                var editable = Editable(); var value = asset.ReadVirtualProperty(presenter);
                var type = Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type;
                void Assign(object? replacement)
                {
                    if (!Editable()) throw new InvalidOperationException("The procedural virtual selection changed.");
                    asset.BufferVirtualProperty(presenter, replacement);
                }
                if (presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var supplied))
                {
                    var entries = supplied.ToArray();
                    using var disabled = ImGuiScopes.Disabled(!editable);
                    if (BeginNativeCombo("##Value", NativeAbstractEntryControl.Label(entries, value)))
                    {
                        try { NativeAbstractEntryControl.Choices(entries, value, entry => Execute(() =>
                        {
                            ValidatePendingPropertyStrings();
                            if (!Editable() || !presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var current)
                                || !NativeAbstractEntries.Contains(current.ToArray(), entry)) throw new InvalidOperationException("The native factory entry changed.");
                            var target = asset.AcquirePresenterTarget(presenter); var transferred = false;
                            try
                            {
                                var command = presenter.Commands.FirstOrDefault(command => command.Name == "CreateNewInstance");
                                if (command != null)
                                {
                                    transferred = true;
                                    StartEditorExtensionCommand(asset, target, command, entry, () => new EditorAssetCommandMutation(asset.BeginExtensionCommandMutation()));
                                }
                                else if (!entry.IsMatchingValue(asset.ReadVirtualProperty(presenter)))
                                { Assign(entry.GenerateValue(asset.ReadVirtualProperty(presenter))); asset.EndVirtualEdit(); }
                            }
                            finally { if (!transferred) try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; } }
                        })); }
                        finally { Gui.EndCombo(); }
                    }
                }
                else if (asset.SessionHistory?.Extensions?.HasPrimitiveEditor(presenter.Type) == true)
                    asset.SessionHistory.Extensions.TryDrawPrimitive(new(presenter.Type, value, label, editable, Assign, asset.EndVirtualEdit));
                else if (ContentAssetReference.Supports(presenter.Type))
                    DrawContentReferenceValue(presenter.Type, value, !presenter.MemberAttributes.OfType<Stride.Core.Annotations.NotNullAttribute>().Any()
                            && !presenter.MemberAttributes.OfType<System.Diagnostics.CodeAnalysis.NotNullAttribute>().Any(),
                        replacement => Execute(() => { Assign(replacement); asset.EndVirtualEdit(); }), canEdit: editable);
                else
                {
                    var readonlySelectable = NativeNumericAxes.Names(type).Count > 0 || AssetSettingNumericMetadata.Supports(type) || IsPathType(type) || IsTemporalType(type) || type == typeof(string) || type == typeof(char) || type == typeof(Guid)
                        || type == typeof(Stride.Core.Mathematics.AngleSingle) || IsPackageColorType(type);
                    using var disabled = ImGuiScopes.Disabled(!editable && !readonlySelectable);
                    DrawNativeVirtualScalar(asset, presenter, type, value, editable, asset.VirtualNumericRange(presenter),
                        Editable, () => asset.ReadVirtualProperty(presenter), Assign, asset.EndVirtualEdit,
                        axis => NativeNumericAxes.Names(presenter.Type).ElementAtOrDefault(axis) is { } name
                            && presenter.TryGetChild(name) is { } child ? asset.VirtualNumericRange(child) : null);
                }
                if (Gui.BeginPopupContextItem("Virtual actions"))
                {
                    try
                    {
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, asset.CanCopyVirtualProperty(presenter)))
                            Execute(() => { ValidatePendingPropertyStrings(); Gui.SetClipboardText(asset.CopyVirtualProperty(presenter)); });
                        var clipboard = Gui.GetClipboardTextS() ?? "";
                        var canPaste = !IsAuthoringPipelineUpdating && ReferenceEquals(assetDocument, asset) && asset.CanPasteVirtualProperty(presenter, clipboard);
                        if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste))
                            Execute(() => { ValidatePendingPropertyStrings(); asset.PasteVirtualProperty(presenter, clipboard, false); });
                        if (DrawNativeMenuItem("Replace", "", false, canPaste))
                            Execute(() => { ValidatePendingPropertyStrings(); asset.PasteVirtualProperty(presenter, clipboard, true); });
                        DrawNativeVirtualFlagActions(presenter, editable, () => asset.ReadVirtualProperty(presenter),
                            replacement => { Assign(replacement); asset.EndVirtualEdit(); });
                        DrawProceduralPresenterCommands(asset, presenter);
                    }
                    finally { Gui.EndPopup(); }
                }
            }
            finally { Gui.EndTable(); }
            var valueType = Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type;
            if (NativeNumericAxes.Names(valueType).Count == 0 && valueType != typeof(Stride.Core.Mathematics.Quaternion)
                && !ContentAssetReference.Supports(presenter.Type) && presenter.Children.Count > 0
                && asset.SessionHistory?.Extensions?.HasPrimitiveEditor(presenter.Type) != true)
            {
                var expansion = presenter.AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var rule)
                    ? rule : Stride.Core.ExpandRule.Auto;
                ApplyProceduralExpansion(expansion);
                if (DrawNativeTreeNode("##Children", ImGuiTreeNodeFlags.DefaultOpen, false, out _, caption: label))
                {
                    try
                    {
                        foreach (var child in ProceduralVirtualChildren(presenter).OrderBy(child => NativeRowPath(child), NativeRowPathComparer.Instance))
                        {
                            if (!PresenterPathVisible(child) || !categories.Before(child, includeSelf: false, includeMemberCategories: false)) continue;
                            if (child is VirtualNodePresenter virtualChild)
                            {
                                if (PropertyGrid.ProceduralRows.Consume(asset, virtualChild)) DrawProceduralVirtualProperty(asset, virtualChild, categories, ancestors);
                            }
                            else if (child.GetNodeAccessor() is { Node: IMemberNode member, Index.IsEmpty: true })
                                DrawAssetProperty(asset, asset.PropertyField(member, NativePresenterCategories.Identity(child)), ancestors);
                            else if (child.GetNodeAccessor() is { Node: Stride.Core.Assets.Quantum.IAssetObjectNode collection } accessor
                                && !accessor.Index.IsEmpty && (collection.Indices ?? []).Contains(accessor.Index)
                                && asset.CollectionContext(collection) is { } context)
                            {
                                var item = asset.CollectionItems(context).FirstOrDefault(item => item.Index == accessor.Index);
                                if (item != null)
                                {
                                    var path = ancestors ?? [];
                                    Gui.PushID(item.Key);
                                    try
                                    {
                                        Gui.TextUnformatted(child.DisplayName);
                                        if (!asset.CanEditCollectionItem(item)) DrawProceduralCollectionItemActions(asset, item);
                                        if (!asset.IsCollectionItemCurrent(item)) continue;
                                        DrawProceduralCollectionItem(asset, item, path);
                                        if (asset.CanEditCollectionItem(item)) DrawProceduralCollectionItemActions(asset, item);
                                    }
                                    finally { Gui.PopID(); }
                                }
                            }
                        }
                    }
                    finally { Gui.TreePop(); }
                }
            }
        }
        finally
        {
            try { if (lease != null) lease.Dispose(); }
            catch { packageCommandCleanup.Add(lease!.Dispose); throw; }
            finally { Gui.PopID(); }
        }
    }
    private static IEnumerable<INodePresenter> ProceduralVirtualChildren(INodePresenter presenter)
    {
        foreach (var child in presenter.Children)
            if (NativePresenterCategories.IsCategory(child))
            {
                foreach (var nested in ProceduralVirtualChildren(child)) yield return nested;
            }
            else yield return child;
    }
}
