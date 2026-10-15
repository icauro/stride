using System.Runtime.CompilerServices;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Mathematics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawCompositorVirtualRow(AssetMetadataDocument source, IReadOnlyList<VirtualNodePresenter> group,
        NativePresenterCategories categories)
    {
        if (group.Count == 0 || !categories.Before(group[0], includeMemberCategories: false)) return;
        var nodes = group.ToArray(); var first = nodes[0];
        if (!compositorEditors.TryGetValue(source, out var state)) return;
        var blocks = state.SelectedBlocks.ToArray(); var items = state.SelectedItems.ToArray();
        var selection = state.PropertySelection; var epoch = source.SessionHistory?.Extensions?.Version;
        bool Current() => compositorEditors.TryGetValue(source, out var current) && ReferenceEquals(current, state)
            && current.SelectedBlocks.SetEquals(blocks) && current.SelectedItems.SetEquals(items) && current.PropertySelection == selection
            && source.SessionHistory?.Extensions?.Version == epoch
            && sessionHistory?.Registry.Find(source.AuthoredAsset.Id) is { IsPublished: true, IsDisposed: false } owner
            && ReferenceEquals(owner.Document, source) && nodes.All(node => source.ExtensionVirtualProperties.Contains(node));
        bool Editable() => CanStartAssetEdit && Current() && nodes.All(source.CanEditExtensionVirtualProperty);
        object?[] Read() => nodes.Select(source.ReadExtensionVirtualProperty).Concat(nodes.SelectMany(node =>
        {
            var accessor = node.GetNodeAccessor();
            return new object?[] { node, accessor.Node, accessor.Index, accessor.RetrieveValue(),
                accessor.Node is IMemberNode member ? member.Parent.Retrieve() : null, epoch, source.EditRevision };
        })).ToArray();
        IDisposable Acquire()
        {
            if (!Current()) throw new InvalidOperationException("The compositor virtual property owner changed.");
            var leases = new List<IDisposable>();
            try
            {
                leases.Add(sessionHistory!.Registry.Find(source.AuthoredAsset.Id)!.AcquireView());
                foreach (var node in nodes) leases.Add(source.AcquireExtensionVirtualTarget(node));
                return new PropertyStringOwners(leases, [source]);
            }
            catch
            {
                foreach (var lease in leases.AsEnumerable().Reverse())
                    try { lease.Dispose(); } catch { packageCommandCleanup.Add(lease.Dispose); }
                throw;
            }
        }
        void Apply(Func<int, object?, object?> change)
        {
            ValidatePendingPropertyStrings(); source.CommitEdits();
            if (!Editable()) throw new InvalidOperationException("The native compositor virtual target changed before validation.");
            UseTextureSettingsCommandContext();
            source.TransformCompositorVirtualProperties(nodes, change);
        }
        PropertyNumericRange? AxisRange(VirtualNodePresenter node, int axis)
        {
            var name = NativeNumericAxes.Names(node.Type).ElementAtOrDefault(axis);
            var child = name == null ? null : node.TryGetChild(name);
            return child is VirtualNodePresenter virtualChild ? source.ExtensionNumericRange(virtualChild)
                : child?.GetNodeAccessor().Node is IMemberNode member ? source.ExtensionNumericRange(member) : null;
        }
        Gui.PushID(RuntimeHelpers.GetHashCode(first));
        var rowOpen = false; var childrenOpen = false;
        try
        {
            using var lease = Acquire();
            rowOpen = BeginInspectorPropertyRow(first.DisplayName);
            if (!rowOpen) return;
            var values = nodes.Select(source.ReadExtensionVirtualProperty).ToArray(); var value = values[0];
            var hasUnknown = values.Any(raw => raw is Stride.Core.Yaml.IUnloadable);
            var mixed = values.Skip(1).Any(candidate => !ContentAssetReference.AreEqual(candidate, value));
            var type = Nullable.GetUnderlyingType(first.Type) ?? first.Type;
            var complex = !hasUnknown && nodes.Any(IsCompositorNativeComplex)
                && source.SessionHistory?.Extensions?.HasPrimitiveEditor(first.Type) != true;
            var expanded = false;
            if (complex)
            {
                SetCompositorVirtualExpansion(first);
                expanded = Gui.TreeNodeEx("##Native children"); childrenOpen = expanded; Gui.SameLine();
            }
            var supplied = nodes.Select(node => source.ExtensionAbstractEntries(node)?.ToArray()).ToArray();
            var nullable = Nullable.GetUnderlyingType(first.Type) != null && supplied.All(entries => entries == null)
                && source.SessionHistory?.Extensions?.HasPrimitiveEditor(first.Type) != true;
            using var nullableRow = NativeNullableRow.Begin("##Nullable", nullable);
            if (nullable)
            {
                using (ImGuiScopes.Disabled(!Editable()))
                    if (DrawNativeNullablePresence(value != null || mixed, out var create))
                        Execute(() => Apply((_, _) => create ? CreateNullableValue(type) : null));
                nullableRow.ValueColumn();
            }
            if (nullable && value == null && !mixed) Gui.TextUnformatted(EditorLocalization.Text("(null)"));
            else if (supplied.Any(entries => entries != null))
            {
                var entries = NativeAbstractEntries.Combine(supplied.Where(entries => entries != null).Select(entries => (IReadOnlyList<Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry>)entries!).ToArray());
                using var disabled = ImGuiScopes.Disabled(!Editable());
                if (Gui.BeginCombo("##Value", mixed ? DifferentPropertyValues : NativeAbstractEntryControl.Label(entries, value)))
                {
                    try { NativeAbstractEntryControl.Choices(entries, mixed ? null : value, entry => Execute(() =>
                    {
                        ValidatePendingPropertyStrings(); source.CommitEdits();
                        if (!Editable() || !nodes.All(node => NativeAbstractEntries.Accepts(source.ExtensionAbstractEntries(node)?.ToArray(), entry)))
                            throw new InvalidOperationException("The native compositor factory entry changed.");
                        var targets = AcquireHierarchyFactoryTargets(nodes.Length, index => source.AcquireExtensionVirtualTarget(nodes[index]));
                        var target = new EditorPresenterTarget(targets.Select(owner => owner!).ToArray()); var transferred = false;
                        try
                        {
                            var command = NodePresenterCombination.Commands(target.Presenters).FirstOrDefault(command => command.Name == "CreateNewInstance");
                            if (command != null) { StartEditorExtensionCommand(source, target, command, entry); transferred = true; }
                            else if (target.Presenters.Any(presenter => presenter.Commands.Any(candidate => candidate.Name == "CreateNewInstance")))
                                throw new InvalidOperationException("The registered factory command does not support this native combination.");
                            else Apply((_, current) => entry.IsMatchingValue(current!) ? current : entry.GenerateValue(current!));
                        }
                        finally { if (!transferred) try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; } }
                    })); }
                    finally { Gui.EndCombo(); }
                }
            }
            else if (hasUnknown) { }
            else if (source.SessionHistory?.Extensions?.HasPrimitiveEditor(first.Type) == true)
            {
                using var disabled = ImGuiScopes.Disabled(!Editable());
                source.SessionHistory.Extensions.TryDrawPrimitive(new(first.Type, value, first.DisplayName, Editable(),
                    replacement => Apply((_, _) => replacement), () => source.CommitEdits()));
            }
            else if (complex) { }
            else if (type == typeof(string))
            {
                var feedback = Gui.GetID("##Value");
                try { PropertyGrid.Strings.Draw(first, mixed, Editable, Read, text => Apply((_, _) => text), Acquire); }
                catch (Exception error) { PropertyConversionFeedback.Fail(feedback, error); statusMessage = error.Message; }
                PropertyConversionFeedback.Draw(feedback);
            }
            else if (IsPackageColorType(type))
                DrawRetainedColor(first, type, Read, Editable, transform => Apply((_, current) => transform(current)), Acquire,
                    axis => nodes.All(node => !NativeNumericAxes.IsReadOnly(node, axis)), valueCount: nodes.Length);
            else if (IsTemporalType(type))
                DrawRetainedTemporal(first, type, Editable, Read, transform => Apply((_, current) => transform(current)), nodes.Length, Acquire);
            else if (type == typeof(AngleSingle))
                DrawRetainedAngle(first, Read, Editable, replacement => Apply((_, _) => replacement), Acquire, nodes.Length);
            else if (type == typeof(Guid))
                DrawRetainedGuid(first, Read, Editable, replacement => Apply((_, _) => replacement), Acquire, nodes.Length);
            else if (type == typeof(char))
                DrawRetainedCharacter(first, Read, Editable, replacement => Apply((_, _) => replacement), Acquire, nodes.Length);
            else if (IsPathType(type))
                DrawRetainedPath(source, first, type, Editable, Read, replacement => Apply((_, _) => replacement), nodes.Length, Acquire);
            else if (type == typeof(Quaternion) || NativeNumericAxes.Names(type).Count != 0)
                DrawRetainedVirtualMath(first, type, nodes, Read, Editable,
                    axis => nodes.Select(node => AxisRange(node, axis)).ToArray(), Apply, Acquire);
            else if (ContentAssetReference.Supports(first.Type))
                DrawContentReferenceValue(first.Type, value, nodes.All(node => !node.MemberAttributes.OfType<Stride.Core.Annotations.NotNullAttribute>().Any()
                    && !node.MemberAttributes.OfType<System.Diagnostics.CodeAnalysis.NotNullAttribute>().Any()),
                    replacement => Apply((_, _) => replacement), canEdit: Editable());
            else
                DrawNativeVirtualScalar(source, first, type, value, Editable(), source.ExtensionNumericRange(first), Editable,
                    () => source.ReadExtensionVirtualProperty(first), replacement => Apply((_, _) => replacement), () => { },
                    axis => NativeNumericInput.Merge(type, nodes.Select(node => AxisRange(node, axis))),
                    nullablePresence: false, numericRanges: nodes.Select(source.ExtensionNumericRange).ToArray(), mixed: mixed, readValues: Read, colorOwner: Acquire);
            if (hasUnknown) DrawUnloadableProperties(values);
            if (Gui.BeginPopupContextItem("Virtual property actions"))
            {
                try
                {
                    if (ContentAssetReference.Supports(first.Type)) DrawContentReferenceFetchActions(values);
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, nodes.Length == 1 && source.CanCopyVirtualProperty(first)))
                        Execute(() => { if (Current()) Gui.SetClipboardText(source.CopyVirtualProperty(first)); });
                    var text = Gui.GetClipboardTextS() ?? "";
                    var canPaste = Editable() && nodes.All(node => source.CanPasteVirtualProperty(node, text));
                    if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) Paste(false);
                    if (DrawNativeMenuItem("Replace", "", false, canPaste)) Paste(true);
                    if (type.IsEnum && type.IsDefined(typeof(FlagsAttribute), true))
                    {
                        using var disabled = ImGuiScopes.Disabled(!Editable());
                        if (FlagEnumPropertyControl.DrawBulkActions() is { } action)
                            Execute(() => Apply((_, current) => FlagEnumSelection.Apply((Enum)(current ?? Enum.ToObject(type, 0)), action)));
                    }
                    DrawNativePresenterCommands(source, Current,
                        () => AcquireHierarchyFactoryTargets(nodes.Length, index => source.AcquireExtensionVirtualTarget(nodes[index])),
                        () => new EditorAssetCommandMutation(source.BeginExtensionCommandMutation()));
                    void Paste(bool replace) => Execute(() =>
                    {
                        ValidatePendingPropertyStrings(); source.CommitEdits();
                        if (!Editable() || nodes.Any(node => !source.CanPasteVirtualProperty(node, text))) throw new InvalidOperationException("The native virtual paste target changed.");
                        using var capture = source.SessionHistory!.CaptureOperationsAtomic(() =>
                        { foreach (var node in nodes) source.PasteVirtualProperty(node, text, replace); });
                        capture.Publish(source.SessionHistory, replace ? "Replace property" : "Paste property");
                    });
                }
                finally { Gui.EndPopup(); }
            }
            if (expanded)
            {
                nullableRow.Dispose(); Gui.EndTable(); rowOpen = false;
                try { DrawCompositorNativeChildren(source, nodes); }
                finally { Gui.TreePop(); childrenOpen = false; }
            }
        }
        finally { if (childrenOpen) Gui.TreePop(); if (rowOpen) Gui.EndTable(); Gui.PopID(); }
    }
}
