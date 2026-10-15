using Hexa.NET.ImGui;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed record ComponentVirtualOwner(EntityHierarchyDocument Document, ComponentNodePresentation Presentation, VirtualNodePresenter Node);
    private void DrawAdditionalCombinedVirtualPanels(IReadOnlyList<EntityHierarchyDocument> owners, IEnumerable<string> existingKeys)
    {
        var excluded = existingKeys.ToHashSet(StringComparer.Ordinal);
        var maps = new List<Dictionary<string, (EntityHierarchyDocument Owner, ComponentNodePresentation Presentation, Type Type)>>();
        foreach (var owner in owners)
        foreach (var id in owner.SelectedIds)
        {
            if (!owner.Asset.Hierarchy.Parts.TryGetValue(id, out var design)) continue;
            var map = new Dictionary<string, (EntityHierarchyDocument, ComponentNodePresentation, Type)>();
            var occurrences = new Dictionary<Type, int>();
            foreach (var component in design.Entity.Components)
            {
                var type = component.GetType(); var occurrence = occurrences.GetValueOrDefault(type); occurrences[type] = occurrence + 1;
                var key = component is Stride.Engine.TransformComponent ? "Transform" : type.AssemblyQualifiedName + "@" + occurrence;
                if (excluded.Contains(key) || component is Stride.Core.Yaml.IUnloadable) continue;
                map.Add(key, (owner, owner.GetComponentPresentation(component), type));
            }
            maps.Add(map);
        }
        if (maps.Count < 2) return;
        foreach (var pair in maps[0])
        {
            if (maps.Skip(1).Any(map => !map.TryGetValue(pair.Key, out var other) || other.Type != pair.Value.Type)) continue;
            var entries = maps.Select(map => map[pair.Key]).ToArray();
            var groups = new List<IReadOnlyList<VirtualNodePresenter>>();
            void Visit(IReadOnlyList<INodePresenter> parents)
            {
                foreach (var group in NodePresenterCombination.CombineChildren(parents))
                {
                    if (!NodePresenterCombination.CanConstruct(group) || group.All(node => !node.IsVisible)) continue;
                    if (group.All(node => node is VirtualNodePresenter)) groups.Add(group.Cast<VirtualNodePresenter>().ToArray());
                    Visit(group);
                }
            }
            Visit(entries.Select(entry => entry.Presentation.RootPresenter).ToArray());
            var roots = groups.Where(group => !NativePresenterCategories.IsCategory(group[0]) && NativeVirtualAncestor(group[0]) == null).ToArray();
            if (roots.Length == 0) continue;
            ComponentVirtualOwner[] Owners(IReadOnlyList<VirtualNodePresenter> nodes) => nodes.Select(node =>
            {
                var entry = entries.Single(entry => entry.Presentation.OwnsVirtual(node));
                return new ComponentVirtualOwner(entry.Owner, entry.Presentation, node);
            }).ToArray();
            Gui.PushID(pair.Key);
            try
            {
                var label = Stride.Core.DisplayAttribute.GetDisplayName(pair.Value.Type);
                if (string.IsNullOrEmpty(label) || label == pair.Value.Type.Name) label = Stride.Core.Presentation.Core.Utils.SplitCamelCase(pair.Value.Type.Name);
                if (!Gui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen)) continue;
                var categories = new NativePresenterCategories(node =>
                {
                    var group = groups.FirstOrDefault(group => ReferenceEquals(group[0], node));
                    if (group == null || !Gui.BeginPopupContextItem("Category actions")) return;
                    try { DrawCombinedComponentVirtualCommands(Owners(group)); } finally { Gui.EndPopup(); }
                });
                foreach (var group in roots.OrderBy(group => NativeRowPath(group[0]), NativeRowPathComparer.Instance))
                    if (categories.Before(group[0], includeMemberCategories: false)) DrawCombinedComponentVirtual(Owners(group), categoryScope: categories);
            }
            finally { Gui.PopID(); }
        }
    }
    private void DrawPresentedCombinedComponentRows(IReadOnlyList<CommonComponentProperty> properties,
        Func<ComponentField, EntityHierarchyDocument> ownerFor, Action<CommonComponentProperty> drawAuthored)
    {
        var targets = new List<EditorPresenterTarget>();
        try
        {
            var roots = properties.SelectMany(property => property.Fields).Select(field => (Owner: ownerFor(field), Presentation: field.Presentation))
                .Where(row => row.Presentation != null).Distinct().ToArray();
            var ownerMap = roots.ToDictionary(row => row.Presentation!, row => row.Owner);
            var groups = new List<IReadOnlyList<VirtualNodePresenter>>();
            void Visit(IReadOnlyList<INodePresenter> parents)
            {
                foreach (var group in NodePresenterCombination.CombineChildren(parents))
                {
                    if (!NodePresenterCombination.CanConstruct(group) || group.All(node => !node.IsVisible)) continue;
                    if (group.All(node => node is VirtualNodePresenter)) groups.Add(group.Cast<VirtualNodePresenter>().ToArray());
                    Visit(group);
                }
            }
            Visit(roots.Select(row => row.Presentation!.RootPresenter).ToArray());
            var authored = properties.Select(property =>
            {
                var target = property.First.Presentation?.AcquireCommandTarget(property.First);
                if (target != null) targets.Add(target);
                return (Property: property, Presenter: target?.Presenter);
            }).ToArray();
            var containers = authored.Where(row => row.Presenter != null).Select(row => NativeRowContainer(row.Presenter!)).ToHashSet();
            foreach (var row in authored.Where(row => row.Presenter != null))
                for (var ancestor = NativeVirtualAncestor(row.Presenter!); ancestor != null; ancestor = NativeVirtualAncestor(ancestor)) containers.Add(NativeRowContainer(ancestor));
            var virtuals = groups.Where(group => !NativePresenterCategories.IsCategory(group[0]) && NativeVirtualAncestor(group[0]) == null && containers.Contains(NativeRowContainer(group[0]))).ToArray();
            ComponentVirtualOwner[] Owners(IReadOnlyList<VirtualNodePresenter> group) => group.Select(node =>
            {
                var presentation = ownerMap.Keys.Single(value => value.OwnsVirtual(node));
                return new ComponentVirtualOwner(ownerMap[presentation], presentation, node);
            }).ToArray();
            var categories = new NativePresenterCategories(node =>
            {
                var group = groups.FirstOrDefault(group => ReferenceEquals(group[0], node));
                if (group == null || !Gui.BeginPopupContextItem("Category actions")) return;
                try { DrawCombinedComponentVirtualCommands(Owners(group)); } finally { Gui.EndPopup(); }
            }, node => groups.FirstOrDefault(group => ReferenceEquals(group[0], node)) is { } group ? group.Any(value => value.IsVisible) : node.IsVisible);
            var rows = authored.Where(row => row.Presenter == null || NativeVirtualAncestor(row.Presenter) == null).Select(row => (row.Property, row.Presenter, Virtual: (IReadOnlyList<VirtualNodePresenter>?)null,
                    Path: row.Presenter == null ? new[] { new NativeRowOrder(row.Property.First.DisplayOrder ?? int.MaxValue, row.Property.Key) } : NativeRowPath(row.Presenter)))
                .Concat(virtuals.Select(group => (Property: (CommonComponentProperty)null!, Presenter: (INodePresenter?)group[0],
                    Virtual: (IReadOnlyList<VirtualNodePresenter>?)group, Path: NativeRowPath(group[0]))))
                .OrderBy(row => row.Path, NativeRowPathComparer.Instance);
            foreach (var row in rows)
            {
                if (row.Presenter != null && !categories.Before(row.Presenter, includeSelf: false, includeMemberCategories: false)) continue;
                if (row.Virtual != null) DrawCombinedComponentVirtual(Owners(row.Virtual), group =>
                {
                    var property = properties.FirstOrDefault(property => property.Fields.Count == group.Count
                        && property.Fields.Select(field => field.Node).Zip(group.Select(node => node.GetNodeAccessor().Node)).All(pair => ReferenceEquals(pair.First, pair.Second)));
                    if (property != null) drawAuthored(property);
                }, categories);
                else drawAuthored(row.Property);
            }
        }
        finally
        {
            List<Exception>? failures = null;
            foreach (var target in targets) try { target.Dispose(); }
                catch (Exception error) { packageCommandCleanup.Add(target.Dispose); (failures ??= []).Add(error); }
            if (failures != null) throw new AggregateException("Aggregate presenter release requires recovery.", failures);
        }
    }
    private void ApplyCombinedComponentVirtual(IReadOnlyList<ComponentVirtualOwner> entries, Func<object?, object?> transform)
        => Execute(() => ApplyCombinedComponentVirtualCore(entries, (_, value) => transform(value)));
    private void ApplyCombinedComponentVirtual(IReadOnlyList<ComponentVirtualOwner> entries, Func<ComponentVirtualOwner, object?, object?> transform)
        => Execute(() => ApplyCombinedComponentVirtualCore(entries, transform));
    private void ApplyCombinedComponentVirtualCore(IReadOnlyList<ComponentVirtualOwner> entries, Func<ComponentVirtualOwner, object?, object?> transform)
    {
        if (IsAuthoringPipelineUpdating || closeRequested || closePromptOpen || closingDocument != null)
            throw new InvalidOperationException("The editor cannot edit these virtual properties now.");
        var history = sessionHistory ?? throw new InvalidOperationException("The selected properties have no shared history.");
        var owners = entries.Select(entry => entry.Document).Distinct().ToArray();
        foreach (var owner in owners) owner.EndEdits();
        if (entries.Any(entry => !ReferenceEquals(entry.Document.SessionHistory, history) || !entry.Document.CanEditComponentVirtual(entry.Node)))
            throw new InvalidOperationException("A selected virtual property owner changed.");
        var before = entries.Select(entry => entry.Node.Value).ToArray();
        var values = entries.Select((entry, index) => transform(entry, before[index])).ToArray();
        var leases = new List<IDisposable>();
        var targets = new List<EditorPresenterTarget>();
        try
        {
            foreach (var owner in owners)
            {
                var state = history.Registry.Find(owner.Asset.Id);
                if (!ReferenceEquals(state?.Document, owner)) throw new InvalidOperationException("A selected property is no longer published.");
                leases.Add(state!.AcquireView());
            }
            foreach (var entry in entries) targets.Add(entry.Presentation.AcquireVirtualTarget(entry.Node));
            using var captured = history.CaptureOperationsAtomic(() =>
            {
                for (var index = 0; index < entries.Count; index++)
                    if (!ContentAssetReference.AreEqual(before[index], entries[index].Node.Value)) throw new InvalidOperationException("A selected virtual property changed.");
                foreach (var owner in owners)
                {
                    var indices = Enumerable.Range(0, entries.Count).Where(index => ReferenceEquals(entries[index].Document, owner)).ToArray();
                    owner.ApplyComponentVirtualValues(indices.Select(index => entries[index].Node).ToArray(), indices.Select(index => values[index]).ToArray());
                }
            });
            captured.Publish(history, "Edit " + entries[0].Node.DisplayName);
        }
        finally
        {
            foreach (var target in targets) try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); }
            foreach (var lease in leases) try { lease.Dispose(); } catch { packageCommandCleanup.Add(lease.Dispose); }
        }
    }
    private void RunCombinedComponentVirtualCommand(IReadOnlyList<ComponentVirtualOwner> entries, INodePresenterCommand command, object? parameter)
    {
        var acquired = AcquireHierarchyFactoryTargets(entries.Count, index => entries[index].Presentation.AcquireVirtualTarget(entries[index].Node));
        var combined = new EditorPresenterTarget(acquired.Select(target => target!).ToArray());
        StartEditorExtensionCommand(entries[0].Document, combined, command, parameter,
            () => new EditorHierarchyCommandsMutation(sessionHistory!, entries.Select(entry => entry.Document).Distinct().ToArray()));
    }
    private void DrawCombinedComponentVirtualCommands(IReadOnlyList<ComponentVirtualOwner> entries)
    {
        DrawCombinedComponentVirtualClipboard(entries);
        var type = Nullable.GetUnderlyingType(entries[0].Node.Type) ?? entries[0].Node.Type;
        if (type.IsEnum && type.IsDefined(typeof(FlagsAttribute), true))
        {
            Gui.BeginDisabled(IsAuthoringPipelineUpdating || entries.Any(entry => !entry.Document.CanEditComponentVirtual(entry.Node)));
            if (DrawFlagBulkActions() is { } action) ApplyCombinedComponentVirtual(entries, value => ApplyFlagBulkAction((Enum)(value ?? Enum.ToObject(type, 0)), action));
            Gui.EndDisabled();
        }
        var nodes = entries.Select(entry => (INodePresenter)entry.Node).ToArray();
        foreach (var command in NodePresenterCombination.Commands(nodes))
        {
            var choices = entries[0].Node.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var supplied)
                ? supplied.Where(entry => command.CanExecute(nodes, entry)).ToArray() : [];
            void Run(object? parameter) => Execute(() => RunCombinedComponentVirtualCommand(entries, command, parameter));
            if (choices.Length > 0)
            {
                if (!BeginNativeMenu(command.Name, !IsAuthoringPipelineUpdating)) continue;
                try { NativeAbstractEntryControl.Choices(choices, entries[0].Node.Value, entry => Run(entry)); } finally { Gui.EndMenu(); }
            }
            else if (DrawNativeMenuItem(command.Name, "", false, !IsAuthoringPipelineUpdating && command.CanExecute(nodes, null))) Run(null);
        }
    }
    private void DrawCombinedComponentVirtual(IReadOnlyList<ComponentVirtualOwner> entries, Action<IReadOnlyList<INodePresenter>>? drawMembers = null, NativePresenterCategories? categoryScope = null)
    {
        var first = entries[0].Node;
        Gui.PushID(NativePresenterCategories.Identity(first));
        try
        {
            if (DrawUnloadableProperties(entries.Select(entry => entry.Document.ReadComponentVirtual(entry.Node)).ToArray()))
            {
                if (Gui.BeginPopupContextItem("Virtual property actions"))
                { try { DrawCombinedComponentVirtualCommands(entries); } finally { Gui.EndPopup(); } }
                return;
            }
            if (entries.Any(entry => NativeVirtualChildren(entry.Node.Children).Any(node => node.IsVisible)) && sessionHistory?.Extensions?.HasPrimitiveEditor(first.Type) != true)
            {
                var expand = first.AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var rule) ? rule : Stride.Core.ExpandRule.Auto;
                if (expand == Stride.Core.ExpandRule.Always) Gui.SetNextItemOpen(true);
                else if (expand == Stride.Core.ExpandRule.Never) Gui.SetNextItemOpen(false, ImGuiCond.Once);
                else if (expand == Stride.Core.ExpandRule.Once) Gui.SetNextItemOpen(true, ImGuiCond.Once);
                var open = Gui.TreeNodeEx(first.DisplayName);
                if (Gui.BeginPopupContextItem("Virtual property actions"))
                { try { DrawCombinedComponentVirtualCommands(entries); } finally { Gui.EndPopup(); } }
                if (open)
                {
                    try
                    {
                        var categories = categoryScope ?? new NativePresenterCategories();
                        IEnumerable<IReadOnlyList<INodePresenter>> Children(IReadOnlyList<INodePresenter> parents)
                        {
                            foreach (var group in NodePresenterCombination.CombineChildren(parents))
                            {
                                if (!NodePresenterCombination.CanConstruct(group) || group.All(node => !node.IsVisible)) continue;
                                if (group.All(node => node is VirtualNodePresenter && NativePresenterCategories.IsCategory(node)))
                                { foreach (var child in Children(group)) yield return child; }
                                else yield return group;
                            }
                        }
                        foreach (var group in Children(entries.Select(entry => (INodePresenter)entry.Node).ToArray()).OrderBy(group => NativeRowPath(group[0]), NativeRowPathComparer.Instance))
                        {
                            if (!categories.Before(group[0], includeSelf: false, includeMemberCategories: false)) continue;
                            if (group.All(node => node is VirtualNodePresenter))
                            {
                                var children = group.Select(node =>
                                {
                                    var parent = entries.Single(entry => entry.Presentation.OwnsVirtual((VirtualNodePresenter)node));
                                    return parent with { Node = (VirtualNodePresenter)node };
                                }).ToArray();
                                DrawCombinedComponentVirtual(children, drawMembers, categories);
                            }
                            else drawMembers?.Invoke(group);
                        }
                    }
                    finally { Gui.TreePop(); }
                }
                return;
            }
            if (!BeginInspectorPropertyRow(first.DisplayName)) return;
            try
            {
                var values = entries.Select(entry => (object?)entry.Node.Value).ToArray(); var value = values[0];
                var mixed = values.Skip(1).Any(other => !ContentAssetReference.AreEqual(other, value));
                var editable = !IsAuthoringPipelineUpdating && entries.All(entry => entry.Document.CanEditComponentVirtual(entry.Node));
                var type = Nullable.GetUnderlyingType(first.Type) ?? first.Type;
                var nativeSceneReference = SceneObjectReference.Supports(first.Type)
                    && sessionHistory?.Extensions?.HasPrimitiveEditor(first.Type) != true;
                var nativeTextScalar = (NativeNumericAxes.Names(type).Count > 0 || AssetSettingNumericMetadata.Supports(type) || IsPathType(type) || IsTemporalType(type) || type == typeof(char) || type == typeof(AngleSingle) || type == typeof(Guid) || type == typeof(string)
                    || IsPackageColorType(type) || ContentAssetReference.Supports(first.Type))
                    && entries.All(entry => !entry.Node.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out _))
                    && sessionHistory?.Extensions?.HasPrimitiveEditor(first.Type) != true;
                Gui.BeginDisabled(!editable && !nativeTextScalar && !nativeSceneReference);
                try
                {
                    using var nullableRow = NativeNullableRow.Begin("##Nullable", Nullable.GetUnderlyingType(first.Type) != null);
                    if (Nullable.GetUnderlyingType(first.Type) != null)
                    {
                        var present = values.Any(item => item != null);
                        Gui.BeginDisabled(!editable);
                        if (DrawNativeNullablePresence(present, out var create)) ApplyCombinedComponentVirtual(entries, item => create ? item ?? CreateNullableValue(type) : null);
                        Gui.EndDisabled(); nullableRow.ValueColumn();
                        if (values.All(item => item == null)) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); return; }
                        value ??= values.First(item => item != null);
                    }
                    var supplied = entries.Select(entry => entry.Node.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var options)
                        ? (IReadOnlyList<Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry>)options.ToArray() : null).ToArray();
                    var factoryEntries = supplied.All(options => options == null) ? null : NativeAbstractEntries.Combine(supplied.Where(options => options != null).Select(options => options!).ToArray());
                    if (nativeSceneReference)
                        DrawVirtualSceneReference(entries, value, mixed, editable);
                    else if (factoryEntries != null)
                    {
                        if (Gui.BeginCombo("##Value", mixed ? DifferentPropertyValues : NativeAbstractEntryControl.Label(factoryEntries, value)))
                        {
                            try { NativeAbstractEntryControl.Choices(factoryEntries, value, entry => Execute(() =>
                            {
                                if (entries.Any(owner => !owner.Document.CanEditComponentVirtual(owner.Node)
                                    || !NativeAbstractEntries.Accepts(owner.Node.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var options) ? options : null, entry)))
                                    throw new InvalidOperationException("A selected factory entry changed.");
                                var nodes = entries.Select(owner => (INodePresenter)owner.Node).ToArray();
                                var command = NodePresenterCombination.Commands(nodes).FirstOrDefault(command => command.Name == "CreateNewInstance");
                                if (command != null) RunCombinedComponentVirtualCommand(entries, command, entry);
                                else if (nodes.Any(node => node.Commands.Any(command => command.Name == "CreateNewInstance")))
                                    throw new InvalidOperationException("The native factory command cannot be combined for this selection.");
                                else
                                {
                                    if (entries.All(owner => entry.IsMatchingValue(owner.Document.ReadComponentVirtual(owner.Node)!))) return;
                                    ValidatePendingPropertyStrings();
                                    if (entries.Any(owner => !owner.Document.CanEditComponentVirtual(owner.Node)
                                        || !NativeAbstractEntries.Accepts(owner.Node.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var latest) ? latest : null, entry)))
                                        throw new InvalidOperationException("A selected factory entry changed.");
                                    ApplyCombinedComponentVirtual(entries, item => entry.IsMatchingValue(item!) ? item : entry.GenerateValue(item!));
                                }
                            })); }
                            finally { Gui.EndCombo(); }
                        }
                    }
                    else if (sessionHistory?.Extensions?.HasPrimitiveEditor(first.Type) == true)
                        sessionHistory.Extensions.TryDrawPrimitive(new(first.Type, value, first.DisplayName, editable,
                            updated => ApplyCombinedComponentVirtual(entries, _ => updated), () => { }));
                    else if (ContentAssetReference.Supports(type)) DrawContentReferenceValue(type, value,
                        entries.All(entry => !entry.Node.MemberAttributes.OfType<Stride.Core.Annotations.NotNullAttribute>().Any() && !entry.Node.MemberAttributes.OfType<System.Diagnostics.CodeAnalysis.NotNullAttribute>().Any()),
                        updated => ApplyCombinedComponentVirtual(entries, _ => updated), mixed, canEdit: editable);
                    else if (type == typeof(Stride.Core.IO.UFile) || type == typeof(Stride.Core.IO.UDirectory))
                    {
                        DrawRetainedPath(entries[0].Document, first, type,
                            () => entries.All(entry => entry.Document.CanEditComponentVirtual(entry.Node)),
                            () => entries.Select(entry => entry.Document.ReadComponentVirtual(entry.Node)).ToArray(),
                            chosen => ApplyCombinedComponentVirtualCore(entries, (_, _) => chosen),
                            entries.Count, () => AcquireRetainedCombinedVirtualTargets(entries));
                    }
                    else if (type.IsEnum && Nullable.GetUnderlyingType(first.Type) == null && icons?.HasEnumImages(type) == true)
                    {
                        if (NativeEnumImageControl.Draw(icons, first, type, value, mixed, Enum.GetValues(type).Cast<Enum>(), UiScale(1), out var selected))
                            ApplyCombinedComponentVirtual(entries, _ => selected);
                    }
                    else if (type.IsEnum && type.IsDefined(typeof(FlagsAttribute), true))
                    {
                        if (Gui.BeginCombo("##Value", mixed ? DifferentPropertyValues : value?.ToString()))
                        {
                            try
                            {
                                foreach (var name in Enum.GetNames(type))
                                {
                                    var option = Enum.Parse(type, name); var mask = EnumBits(option); var bits = EnumBits(value!);
                                    var selected = mask == 0 ? bits == 0 : (bits & mask) == mask;
                                    var different = values.Skip(1).Any(item => { var other = EnumBits(item!); return (mask == 0 ? other == 0 : (other & mask) == mask) != selected; });
                                    Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, different);
                                    if (Gui.Checkbox(name, ref selected)) ApplyCombinedComponentVirtual(entries, item =>
                                    { var actual = EnumBits(item!); return EnumFromBits(type, mask == 0 ? 0 : selected ? actual | mask : actual & ~mask); });
                                    Gui.PopItemFlag();
                                }
                                if (DrawFlagBulkActions() is { } action) ApplyCombinedComponentVirtual(entries, item => ApplyFlagBulkAction((Enum)item!, action));
                            }
                            finally { Gui.EndCombo(); }
                        }
                    }
                    else if (type == typeof(Quaternion) || !IsPackageColorType(type) && NativeNumericAxes.Names(type).Count > 0)
                        DrawRetainedCombinedVirtualMath(entries, type);
                    else if (IsTemporalType(type))
                        DrawRetainedCombinedVirtualTemporal(entries);
                    else if (type == typeof(char))
                        DrawRetainedCombinedVirtualCharacter(entries);
                    else if (type == typeof(Guid))
                        DrawRetainedCombinedVirtualGuid(entries);
                    else if (type == typeof(AngleSingle))
                        DrawRetainedCombinedVirtualAngle(entries);
                    else if (IsPackageColorType(type))
                        DrawRetainedCombinedVirtualColor(entries);
                    else
                    {
                        Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, mixed);
                        try { DrawNativeVirtualScalar(entries[0].Document, first, type, value, editable,
                            entries[0].Presentation.VirtualNumericRange(first), () => entries.All(entry => entry.Document.CanEditComponentVirtual(entry.Node)),
                            () => entries[0].Document.ReadComponentVirtual(first), updated => ApplyCombinedComponentVirtualCore(entries, (_, _) => updated), () => { },
                            axis => entries[0].Presentation.VirtualAxisNumericRanges(first).ElementAtOrDefault(axis), nullablePresence: false,
                            numericRanges: entries.Select(entry => entry.Presentation.VirtualNumericRange(entry.Node)).ToArray(), mixed: mixed,
                            readValues: () => entries.Select(entry => entry.Document.ReadComponentVirtual(entry.Node)).ToArray(),
                            colorOwner: () => AcquireRetainedCombinedVirtualTargets(entries)); }
                        finally { Gui.PopItemFlag(); }
                    }
                }
                finally { Gui.EndDisabled(); }
                if (Gui.BeginPopupContextItem("Virtual property actions"))
                {
                    try { if (ContentAssetReference.Supports(first.Type)) DrawContentReferenceFetchActions(values); DrawCombinedComponentVirtualCommands(entries); }
                    finally { Gui.EndPopup(); }
                }
            }
            finally { Gui.EndTable(); }
        }
        finally { Gui.PopID(); }
    }
}
