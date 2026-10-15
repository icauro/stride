using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawProceduralCollection(ProceduralAssetDocument asset, ProceduralCollectionContext context, HashSet<IObjectNode>? ancestors)
    {
        ancestors ??= [];
        if (!ancestors.Add(context.Node)) return;
        try
        {
            var collectionPresenter = context.Parent is { } parent ? asset.CollectionItemPresenter(parent) : null;
            var expansion = collectionPresenter != null && collectionPresenter.AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var rule)
                ? rule : asset.PropertyExpansion(context.Origin.Node);
            ApplyProceduralExpansion(expansion);
            var open = DrawNativeTreeNode("##Collection", ImGuiTreeNodeFlags.DefaultOpen, false, out _, caption: context.Origin.Label);
            var header = Gui.GetCurrentContext().LastItemData;
            if (!open) return;
            try
            {
                foreach (var item in asset.CollectionItems(context))
                {
                    if (asset.CollectionItemPresenter(item) is { } presenter && !EditorPresenterItemMetadata.IsVisible(presenter)) continue;
                    Gui.PushID(item.Key);
                    try
                    {
                        if (context.Node.Descriptor is not DictionaryDescriptor keyDictionary || !NativeDictionaryKey.SupportsText(keyDictionary.KeyType))
                            Gui.TextUnformatted(asset.CollectionItemPresenter(item)?.DisplayName ?? "[" + item.Index + "]");
                        if (context.Node.Descriptor is DictionaryDescriptor dictionary && NativeDictionaryKey.SupportsText(dictionary.KeyType))
                        {
                            Gui.PushID("Key");
                            try { DrawProceduralDictionaryKey(asset, item); } finally { Gui.PopID(); }
                            if (!asset.IsCollectionItemCurrent(item)) continue;
                        }
                        if (!asset.CanEditCollectionItem(item)) DrawProceduralCollectionItemActions(asset, item);
                        if (!asset.IsCollectionItemCurrent(item)) continue;
                        DrawProceduralCollectionItem(asset, item, ancestors);
                        if (asset.CanEditCollectionItem(item)) DrawProceduralCollectionItemActions(asset, item);
                    }
                    catch (Exception error) { assetEditError = error.Message; statusMessage = error.Message; }
                    finally { Gui.PopID(); }
                }
                if (context.Node.Descriptor is DictionaryDescriptor) DrawProceduralDictionaryAdd(asset, context);
                else
                {
                    using var disabled = ImGuiScopes.Disabled(!asset.CanAddCollection(context));
                    var defaultAdd = asset.CollectionNativeEntries(context) == null;
                    if (Gui.Button("Add"))
                    {
                        if (defaultAdd) AddProceduralDefaultCollectionItem(asset, context, "");
                        else Gui.OpenPopup("AddItem");
                    }
                    if (!defaultAdd && Gui.BeginPopup("AddItem"))
                    {
                        try
                        {
                            NativeAbstractEntryControl.Choices(asset.CollectionEntries(context), null,
                                entry => SelectProceduralCollectionEntry(asset, context, entry, ""));
                        }
                        finally { Gui.EndPopup(); }
                    }
                }
            }
            finally { Gui.TreePop(); Gui.GetCurrentContext().LastItemData = header; }
        }
        finally { ancestors.Remove(context.Node); }
    }
    private void DrawProceduralCollectionItem(ProceduralAssetDocument asset, ProceduralCollectionItem item, HashSet<IObjectNode> ancestors)
    {
        var type = item.Type; var value = item.Value;
        var entries = asset.CollectionItemEntries(item);
        if (entries.Count != 0)
        {
            using var disabled = ImGuiScopes.Disabled(!asset.CanEditCollectionItem(item));
            if (BeginNativeCombo("##Type", NativeAbstractEntryControl.Label(entries, value)))
            {
                try { NativeAbstractEntryControl.Choices(entries, value, entry => SelectProceduralCollectionItemEntry(asset, item, entry)); }
                finally { Gui.EndCombo(); }
            }
            if (!asset.IsCollectionItemCurrent(item)) return;
            value = item.Value;
        }
        bool Editable() => ReferenceEquals(assetDocument, asset) && asset.CanEditCollectionItem(item);
        void Commit(object? next)
        {
            ValidatePendingPropertyStrings();
            if (!Editable()) throw new InvalidOperationException("The procedural collection selection changed.");
            asset.UpdateCollectionItem(item, value, next);
        }
        var objectPresence = !type.IsValueType && type != typeof(string) && !ContentAssetReference.Supports(type)
            && entries.Count == 0 && ObjectFactoryRegistry.CanCreateInstance(type);
        var nullable = Nullable.GetUnderlyingType(item.DeclaredType) != null || objectPresence;
        using var nullableRow = NativeNullableRow.Begin("##Nullable", nullable);
        if (nullable)
        {
            using var disabled = ImGuiScopes.Disabled(!Editable());
            if (DrawNativeNullablePresence(value != null, out var create, item.AllowsNull))
                Execute(() => Commit(create ? CreateNullableValue(type) : null));
            nullableRow.ValueColumn();
            if (item.Value == null) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); return; }
            value = item.Value;
        }
        if (type == typeof(string))
            PropertyGrid.Strings.Draw(item.Context.Node, false, Editable, () => [item.Value],
                text => asset.UpdateCollectionItem(item, item.Value, text), () => AcquirePropertyStringOwner(asset));
        else if (AssetSettingNumericMetadata.Supports(type)) DrawProceduralCollectionNumber(asset, item);
        else if (IsTemporalType(type))
        {
            object?[] ReadTemporal()
            {
                var baseline = new List<object?> { item.Value, item.Context.Node, item.Index, item.Key };
                for (var parent = item.Context.Parent; parent != null; parent = parent.Context.Parent)
                { baseline.Add(parent.Context.Node); baseline.Add(parent.Index); baseline.Add(parent.Key); }
                return baseline.ToArray();
            }
            DrawRetainedTemporal(item.Context.Node, type, Editable, ReadTemporal, transform => Commit(transform(item.Value)),
                1, () => AcquirePropertyStringOwner(asset));
        }
        else if (type == typeof(char))
        {
            object?[] ReadCharacter()
            {
                var baseline = new List<object?> { item.Value, item.Context.Node, item.Index, item.Key };
                for (var parent = item.Context.Parent; parent != null; parent = parent.Context.Parent)
                { baseline.Add(parent.Context.Node); baseline.Add(parent.Index); baseline.Add(parent.Key); }
                return baseline.ToArray();
            }
            DrawRetainedCharacter(item.Context.Node, ReadCharacter, Editable,
                character => Commit(character), () => AcquirePropertyStringOwner(asset));
        }
        else if (type == typeof(Guid))
        {
            object?[] ReadGuid()
            {
                var baseline = new List<object?> { item.Value, item.Context.Node, item.Index, item.Key };
                for (var parent = item.Context.Parent; parent != null; parent = parent.Context.Parent)
                { baseline.Add(parent.Context.Node); baseline.Add(parent.Index); baseline.Add(parent.Key); }
                return baseline.ToArray();
            }
            DrawRetainedGuid(item.Context.Node, ReadGuid, Editable,
                guid => Commit(guid), () => AcquirePropertyStringOwner(asset));
        }
        else if (type == typeof(Stride.Core.Mathematics.AngleSingle))
        {
            object?[] ReadAngle()
            {
                var baseline = new List<object?> { item.Value, item.Context.Node, item.Index, item.Key };
                for (var parent = item.Context.Parent; parent != null; parent = parent.Context.Parent)
                { baseline.Add(parent.Context.Node); baseline.Add(parent.Index); baseline.Add(parent.Key); }
                return baseline.ToArray();
            }
            DrawRetainedAngle(item.Context.Node, ReadAngle, Editable,
                angle => Commit(angle), () => AcquirePropertyStringOwner(asset));
        }
        else if (DrawNativeScalarTemplate(item.Context.Node, type, value, false, Editable(), next => Execute(() => Commit(next)))) { }
        else if (type == typeof(bool))
        {
            using var disabled = ImGuiScopes.Disabled(!Editable()); var selected = value is true;
            if (DrawNativeCheckbox("##Value", ref selected)) Execute(() => Commit(selected));
        }
        else if (type.IsEnum) DrawProceduralCollectionEnum(asset, item);
        else if (type == typeof(Stride.Core.Mathematics.Quaternion)) DrawProceduralCollectionRotation(asset, item);
        else if (type == typeof(Stride.Core.Mathematics.Color) || type == typeof(Stride.Core.Mathematics.Color3) || type == typeof(Stride.Core.Mathematics.Color4))
        {
            object?[] ReadColor()
            {
                var baseline = new List<object?> { item.Value, item.Context.Node, item.Index, item.Key };
                for (var parent = item.Context.Parent; parent != null; parent = parent.Context.Parent)
                { baseline.Add(parent.Context.Node); baseline.Add(parent.Index); baseline.Add(parent.Key); }
                return baseline.ToArray();
            }
            DrawRetainedColor(item.Context.Node, type, ReadColor, Editable,
                create => Commit(create(item.Value)), () => AcquirePropertyStringOwner(asset), axis => !asset.CollectionAxisReadOnly(item, axis));
        }
        else if (NativeNumericAxes.Names(type).Count is var dimensions && dimensions != 0)
        {
            if (Gui.BeginTable("##Coordinates", Math.Min(4, dimensions), ImGuiTableFlags.SizingStretchSame))
            {
                try
                {
                    for (var axis = 0; axis < dimensions; axis++)
                    {
                        Gui.TableNextColumn(); Gui.PushID(axis);
                        try { if (type != typeof(Stride.Core.Mathematics.Matrix)) Gui.TextUnformatted(ExtendedMathLabel(type, axis)); DrawProceduralCollectionNumber(asset, item, axis); }
                        finally { Gui.PopID(); }
                    }
                }
                finally { Gui.EndTable(); }
            }
        }
        else if (ContentAssetReference.Supports(type)) DrawContentReferenceValue(type, value, item.AllowsNull, Commit, canEdit: Editable());
        else if (type == typeof(Stride.Core.IO.UFile) || type == typeof(Stride.Core.IO.UDirectory))
        {
            object?[] ReadPath()
            {
                var baseline = new List<object?> { item.Value, item.Context.Node, item.Index, item.Key };
                for (var parent = item.Context.Parent; parent != null; parent = parent.Context.Parent)
                { baseline.Add(parent.Context.Node); baseline.Add(parent.Index); baseline.Add(parent.Key); }
                return baseline.ToArray();
            }
            DrawTypedPropertyPath(asset, type, value, Editable, () => item.Value, Commit,
                identity: item.Context.Node, readValues: ReadPath);
        }
        else if (item.Context.Node.ItemReferences != null
            && item.Context.Node.IndexedTarget(item.Index) is { } target && ancestors.Add(target))
        {
            try
            {
                if (target.IsEnumerable && target is Stride.Core.Assets.Quantum.IAssetObjectNode nested)
                {
                    ancestors.Remove(target); DrawProceduralCollection(asset, new(item.Context.Origin, nested, item), ancestors);
                }
                else
                {
                    var presenter = asset.CollectionItemPresenter(item);
                    var inline = presenter != null && presenter.AttachedProperties.TryGetValue(InlineData.InlineMemberKey, out var inlined) && inlined
                        ? presenter.TryGetChild(InlineData.InlinedProperty)?.GetNodeAccessor().Node as IMemberNode : null;
                    if (inline != null) DrawAssetProperty(asset, asset.PropertyField(inline, item.Key + "/" + inline.Name)
                        with { Label = presenter!.DisplayName }, ancestors);
                    var enabled = inline != null ? target.Members.FirstOrDefault(member => member.Name == "Enabled"
                        && member.Type == typeof(bool) && !asset.PropertyVisible(member)) : null;
                    if (enabled != null) DrawAssetProperty(asset, asset.PropertyField(enabled, item.Key + "/Enabled"), ancestors);
                    DrawProceduralFields(asset, target.Members.Where(member => !ReferenceEquals(member, inline) && asset.PropertyVisible(member))
                        .Select(member => asset.PropertyField(member, item.Key + "/" + member.Name)), ancestors);
                }
            }
            finally { ancestors.Remove(target); }
        }
        else DrawSelectableText("##Value", value?.ToString() ?? EditorLocalization.Text("(null)"));
    }
}
