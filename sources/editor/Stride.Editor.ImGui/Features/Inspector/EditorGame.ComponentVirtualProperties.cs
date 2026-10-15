using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Annotations;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    internal static IEnumerable<INodePresenter> NativeVirtualChildren(IEnumerable<INodePresenter> nodes)
    {
        foreach (var node in nodes)
            if (node is VirtualNodePresenter && NativePresenterCategories.IsCategory(node))
            { foreach (var child in NativeVirtualChildren(node.Children)) yield return child; }
            else yield return node;
    }
    internal static VirtualNodePresenter? NativeVirtualAncestor(INodePresenter node)
    {
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
            if (parent is VirtualNodePresenter virtualNode && !NativePresenterCategories.IsCategory(parent)) return virtualNode;
        return null;
    }
    private void DrawFieldlessComponentVirtualRows(EntityHierarchyDocument owner, Stride.Engine.EntityComponent component)
    {
        var presentation = owner.GetComponentPresentation(component);
        var categories = new NativePresenterCategories(node =>
        {
            if (node is not VirtualNodePresenter category || !Gui.BeginPopupContextItem("Category actions")) return;
            try { DrawComponentVirtualCommands(owner, [], category, presentation); } finally { Gui.EndPopup(); }
        });
        foreach (var row in presentation.VirtualRows.Where(row => !NativePresenterCategories.IsCategory(row) && ReferenceEquals(NativeRowContainer(row), presentation.RootPresenter))
            .OrderBy(NativeRowPath, NativeRowPathComparer.Instance))
            if (categories.Before(row, includeMemberCategories: false)) DrawComponentVirtualProperty(owner, [], row, presentation, categories);
    }
    private void DrawComponentVirtualCommands(EntityHierarchyDocument owner, IReadOnlyList<ComponentField> fields, VirtualNodePresenter presenter, ComponentNodePresentation? sourcePresentation = null)
    {
        var presentation = sourcePresentation ?? fields.Select(field => field.Presentation).FirstOrDefault(value => value?.OwnsVirtual(presenter) == true);
        if (presentation == null) return;
        DrawComponentVirtualClipboard(owner, presenter, presentation);
        DrawNativeVirtualFlagActions(presenter, !IsAuthoringPipelineUpdating && owner.CanEditComponentVirtual(presenter),
            () => owner.ReadComponentVirtual(presenter), replacement => { owner.BufferComponentVirtual(presenter, replacement); owner.EndComponentVirtualEdit(); });
        foreach (var command in presenter.Commands)
        {
            var choices = presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var entries)
                ? entries.Where(entry => command.CanExecute([presenter], entry)).ToArray() : [];
            if (choices.Length > 0)
            {
                if (!BeginNativeMenu(command.Name, !IsAuthoringPipelineUpdating)) continue;
                try { NativeAbstractEntryControl.Choices(choices, presenter.Value,
                    entry => Execute(() => StartEditorExtensionCommand(owner, presentation.AcquireVirtualTarget(presenter), command, entry))); }
                finally { Gui.EndMenu(); }
            }
            else if (DrawNativeMenuItem(command.Name, "", false, !IsAuthoringPipelineUpdating && command.CanExecute([presenter], null)))
                Execute(() => StartEditorExtensionCommand(owner, presentation.AcquireVirtualTarget(presenter), command));
        }
    }
    private void DrawComponentVirtualProperty(EntityHierarchyDocument owner, IReadOnlyList<ComponentField> fields, VirtualNodePresenter presenter, ComponentNodePresentation? sourcePresentation = null, NativePresenterCategories? categoryScope = null)
    {
        var presentation = sourcePresentation ?? fields.Select(field => field.Presentation).First(value => value?.OwnsVirtual(presenter) == true)!;
        var target = presentation.AcquireVirtualTarget(presenter);
        Gui.PushID(NativePresenterCategories.Identity(presenter));
        try
        {
            if (owner.ReadComponentVirtual(presenter) is Stride.Core.Yaml.IUnloadable unknown)
            {
                DrawUnloadableProperty(unknown);
                if (Gui.BeginPopupContextItem("Virtual property actions"))
                { try { DrawComponentVirtualCommands(owner, fields, presenter, presentation); } finally { Gui.EndPopup(); } }
                return;
            }
            if (NativeVirtualChildren(presenter.Children).Any(node => node.IsVisible) && owner.SessionHistory?.Extensions?.HasPrimitiveEditor(presenter.Type) != true)
            {
                var expand = presenter.AttachedProperties.TryGetValue(DisplayData.AutoExpandRuleKey, out var rule) ? rule : Stride.Core.ExpandRule.Auto;
                if (expand == Stride.Core.ExpandRule.Always) Gui.SetNextItemOpen(true);
                else if (expand == Stride.Core.ExpandRule.Never) Gui.SetNextItemOpen(false, Hexa.NET.ImGui.ImGuiCond.Once);
                else if (expand == Stride.Core.ExpandRule.Once) Gui.SetNextItemOpen(true, Hexa.NET.ImGui.ImGuiCond.Once);
                var open = Gui.TreeNodeEx(presenter.DisplayName);
                if (Gui.BeginPopupContextItem("Virtual property actions"))
                { try { DrawComponentVirtualCommands(owner, fields, presenter, presentation); } finally { Gui.EndPopup(); } }
                if (open)
                {
                    try
                    {
                        var categories = categoryScope ?? componentPresenterCategories ?? new NativePresenterCategories(node =>
                        {
                            if (node is not VirtualNodePresenter category || !Gui.BeginPopupContextItem("Category actions")) return;
                            try { DrawComponentVirtualCommands(owner, fields, category, presentation); } finally { Gui.EndPopup(); }
                        });
                        foreach (var child in NativeVirtualChildren(presenter.Children).OrderBy(NativeRowPath, NativeRowPathComparer.Instance))
                        {
                            if (!PresenterPathVisible(child) || !categories.Before(child, includeSelf: false, includeMemberCategories: false)) continue;
                            if (child is VirtualNodePresenter virtualChild)
                            { componentVirtualRowsDrawn?.Add(virtualChild); DrawComponentVirtualProperty(owner, fields, virtualChild, presentation, categories); }
                            else if (child.GetNodeAccessor().Node is Stride.Core.Quantum.IMemberNode member && presentation.AuthoredValue is Stride.Engine.EntityComponent component)
                            {
                                var field = EntityHierarchyDocument.Flatten(owner.GetComponentFields(component)).FirstOrDefault(field => ReferenceEquals(field.Node, member));
                                if (field != null) DrawComponentField(field);
                            }
                        }
                    }
                    finally { Gui.TreePop(); }
                }
                return;
            }
            if (!BeginInspectorPropertyRow(presenter.DisplayName)) return;
            try
            {
                var editable = !IsAuthoringPipelineUpdating && owner.CanEditComponentVirtual(presenter);
                var value = owner.ReadComponentVirtual(presenter);
                var scalarType = Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type;
                var nativeSceneReference = SceneObjectReference.Supports(presenter.Type)
                    && owner.SessionHistory?.Extensions?.HasPrimitiveEditor(presenter.Type) != true;
                var nativeTextScalar = (NativeNumericAxes.Names(scalarType).Count > 0 || AssetSettingNumericMetadata.Supports(scalarType) || IsPathType(scalarType) || IsTemporalType(scalarType) || scalarType == typeof(char) || scalarType == typeof(Stride.Core.Mathematics.AngleSingle) || scalarType == typeof(Guid) || scalarType == typeof(string)
                    || IsPackageColorType(scalarType) || ContentAssetReference.Supports(presenter.Type))
                    && !presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out _)
                    && owner.SessionHistory?.Extensions?.HasPrimitiveEditor(presenter.Type) != true;
                Gui.BeginDisabled(!editable && !nativeTextScalar && !nativeSceneReference);
                try
                {
                    if (nativeSceneReference)
                        DrawVirtualSceneReference([new(owner, presentation, presenter)], value, false, editable);
                    else if (presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var supplied))
                    {
                        var entries = supplied.ToArray();
                        if (Gui.BeginCombo("##Value", NativeAbstractEntryControl.Label(entries, value)))
                        {
                            try { NativeAbstractEntryControl.Choices(entries, value, entry => Execute(() =>
                            {
                                if (!owner.CanEditComponentVirtual(presenter) || !presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var current)
                                    || !NativeAbstractEntries.Contains(current.ToArray(), entry)) throw new InvalidOperationException("The native factory entry is no longer available.");
                                var command = presenter.Commands.FirstOrDefault(command => command.Name == "CreateNewInstance");
                                if (command != null) { owner.EndEdits(); StartEditorExtensionCommand(owner, presentation.AcquireVirtualTarget(presenter), command, entry); }
                                else if (!entry.IsMatchingValue(owner.ReadComponentVirtual(presenter)!))
                                {
                                    ValidatePendingPropertyStrings();
                                    if (!owner.CanEditComponentVirtual(presenter) || !presenter.AttachedProperties.TryGetValue(AbstractNodeEntryData.Key, out var latest)
                                        || !NativeAbstractEntries.Contains(latest.ToArray(), entry)) throw new InvalidOperationException("The native factory entry is no longer available.");
                                    var actual = owner.ReadComponentVirtual(presenter);
                                    if (!entry.IsMatchingValue(actual!)) { owner.BufferComponentVirtual(presenter, entry.GenerateValue(actual!)); owner.EndComponentVirtualEdit(); }
                                }
                            })); }
                            finally { Gui.EndCombo(); }
                        }
                    }
                    else if (owner.SessionHistory?.Extensions?.HasPrimitiveEditor(presenter.Type) == true)
                        owner.SessionHistory.Extensions.TryDrawPrimitive(new(presenter.Type, value, presenter.DisplayName, editable,
                            updated => owner.BufferComponentVirtual(presenter, updated), owner.EndComponentVirtualEdit));
                    else if (ContentAssetReference.Supports(presenter.Type))
                        DrawContentReferenceValue(presenter.Type, value, !presenter.MemberAttributes.OfType<NotNullAttribute>().Any()
                            && !presenter.MemberAttributes.OfType<System.Diagnostics.CodeAnalysis.NotNullAttribute>().Any(), updated =>
                        { owner.BufferComponentVirtual(presenter, updated); owner.EndComponentVirtualEdit(); }, canEdit: editable);
                    else DrawNativeVirtualScalar(owner, presenter, Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type, value, editable,
                        presentation.VirtualNumericRange(presenter), () => owner.CanEditComponentVirtual(presenter), () => owner.ReadComponentVirtual(presenter),
                        updated => owner.BufferComponentVirtual(presenter, updated), owner.EndComponentVirtualEdit,
                        axis => presentation.VirtualAxisNumericRanges(presenter).ElementAtOrDefault(axis),
                        colorOwner: () => AcquireRetainedVirtualColor(owner, () => presentation.AcquireVirtualTarget(presenter)));
                }
                finally { Gui.EndDisabled(); }
                if (Gui.BeginPopupContextItem("Virtual property actions"))
                {
                    try { if (ContentAssetReference.Supports(presenter.Type)) DrawContentReferenceFetchActions([owner.ReadComponentVirtual(presenter)]); DrawComponentVirtualCommands(owner, fields, presenter, presentation); }
                    finally { Gui.EndPopup(); }
                }
            }
            finally { Gui.EndTable(); }
        }
        finally
        {
            Gui.PopID();
            try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
        }
    }
}
