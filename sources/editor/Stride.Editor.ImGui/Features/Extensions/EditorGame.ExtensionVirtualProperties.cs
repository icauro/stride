using System.Globalization;
using System.Runtime.CompilerServices;
using Hexa.NET.ImGui;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private NativePresenterCategories CreateAssetPresenterCategories(AssetMetadataDocument owner)
    {
        return new NativePresenterCategories(presenter =>
        {
            if (presenter is not VirtualNodePresenter category || !Gui.BeginPopupContextItem("Category actions")) return;
            try
            {
                foreach (var command in category.Commands)
                    if (Gui.MenuItem(command.Name, "", false, !IsAuthoringPipelineUpdating && command.CanExecute([category], null)))
                        Execute(() => StartEditorExtensionCommand(owner, owner.AcquireExtensionVirtualTarget(category), command));
            }
            finally { Gui.EndPopup(); }
        });
    }
    private void DrawExtensionVirtualProperties(AssetMetadataDocument owner)
    {
        var scope = GetAssetPresenterRenderScope(owner);
        var rows = owner.ExtensionVirtualProperties.Where(presenter => !NativePresenterCategories.IsCategory(presenter)
            && !scope.Consumed.TryGetValue(presenter, out _)).ToArray();
        DrawExtensionVirtualProperties(owner, rows, scope.Categories);
    }
    private void DrawExtensionVirtualProperties(AssetMetadataDocument owner, IReadOnlyList<VirtualNodePresenter> rows, NativePresenterCategories categories)
    {
        foreach (var presenter in rows)
        {
            Gui.PushID(RuntimeHelpers.GetHashCode(presenter));
            try
            {
                using var presentationLease = owner.AcquireExtensionVirtualTarget(presenter);
                if (!categories.Before(presenter, includeMemberCategories: false)) continue;
                var label = presenter.DisplayName == presenter.Name ? Stride.Core.Presentation.Core.Utils.SplitCamelCase(presenter.Name) : presenter.DisplayName;
                if (owner.ReadExtensionVirtualProperty(presenter) is Stride.Core.Yaml.IUnloadable unknown)
                {
                    DrawUnloadableProperty(unknown);
                    if (Gui.BeginPopupContextItem("Virtual property actions"))
                    { try { DrawExtensionVirtualCommands(owner, presenter); } finally { Gui.EndPopup(); } }
                    continue;
                }
                if (!BeginInspectorPropertyRow(label)) continue;
                try
                {
                    var editable = !IsAuthoringPipelineUpdating && owner.CanEditExtensionVirtualProperty(presenter);
                    var value = owner.ReadExtensionVirtualProperty(presenter); var type = Nullable.GetUnderlyingType(presenter.Type) ?? presenter.Type;
                    var nativeTextScalar = (NativeNumericAxes.Names(type).Count > 0 || AssetSettingNumericMetadata.Supports(type) || IsPathType(type) || IsTemporalType(type) || type == typeof(char) || type == typeof(AngleSingle) || type == typeof(Guid) || type == typeof(string)
                        || IsPackageColorType(type) || ContentAssetReference.Supports(presenter.Type))
                        && !presenter.AttachedProperties.TryGetValue(Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys.AbstractNodeEntryData.Key, out _)
                        && owner.SessionHistory?.Extensions?.HasPrimitiveEditor(presenter.Type) != true;
                    Gui.BeginDisabled(!editable && !nativeTextScalar);
                    try
                    {
                        if (owner.ExtensionAbstractEntries(presenter) is { } suppliedEntries)
                        {
                            var entries = suppliedEntries.ToArray();
                            if (Gui.BeginCombo("##Value", NativeAbstractEntryControl.Label(entries, value)))
                            {
                                try { NativeAbstractEntryControl.Choices(entries, value, entry => Execute(() =>
                                {
                                    if (!owner.CanEditExtensionVirtualProperty(presenter)
                                        || !NativeAbstractEntries.Contains(owner.ExtensionAbstractEntries(presenter)?.ToArray() ?? [], entry))
                                        throw new InvalidOperationException("The native factory entry is no longer available.");
                                    var target = owner.AcquireExtensionVirtualTarget(presenter); var transferred = false;
                                    try
                                    {
                                        var command = target.Presenter.Commands.FirstOrDefault(command => command.Name == "CreateNewInstance");
                                        if (command != null) { transferred = true; StartEditorExtensionCommand(owner, target, command, entry); }
                                        else
                                        {
                                            var current = owner.ReadExtensionVirtualProperty(presenter);
                                            if (entry.IsMatchingValue(current!)) return;
                                            ValidatePendingPropertyStrings();
                                            if (!owner.CanEditExtensionVirtualProperty(presenter)
                                                || !NativeAbstractEntries.Contains(owner.ExtensionAbstractEntries(presenter)?.ToArray() ?? [], entry))
                                                throw new InvalidOperationException("The native factory entry is no longer available.");
                                            current = owner.ReadExtensionVirtualProperty(presenter);
                                            if (entry.IsMatchingValue(current!)) return;
                                            owner.BufferExtensionVirtualProperty(presenter, entry.GenerateValue(current!));
                                            owner.EndExtensionVirtualEdit();
                                        }
                                    }
                                    finally
                                    {
                                        if (!transferred)
                                            try { target.Dispose(); } catch { packageCommandCleanup.Add(target.Dispose); throw; }
                                    }
                                })); }
                                finally { Gui.EndCombo(); }
                            }
                        }
                        else if (owner.SessionHistory?.Extensions?.HasPrimitiveEditor(presenter.Type) == true)
                            owner.SessionHistory.Extensions.TryDrawPrimitive(new(presenter.Type, value, label, editable,
                                updated => owner.BufferExtensionVirtualProperty(presenter, updated), owner.EndExtensionVirtualEdit));
                        else if (ContentAssetReference.Supports(presenter.Type))
                            DrawContentReferenceValue(presenter.Type, value,
                                !presenter.MemberAttributes.OfType<Stride.Core.Annotations.NotNullAttribute>().Any()
                                    && !presenter.MemberAttributes.OfType<System.Diagnostics.CodeAnalysis.NotNullAttribute>().Any(),
                                replacement => Execute(() => { owner.BufferExtensionVirtualProperty(presenter, replacement); owner.EndExtensionVirtualEdit(); }), canEdit: editable);
                        else DrawNativeVirtualScalar(owner, presenter, type, value, editable);
                    }
                    finally { Gui.EndDisabled(); }
                    if (Gui.BeginPopupContextItem("##VirtualCommands"))
                    {
                        try
                        {
                            DrawExtensionVirtualCommands(owner, presenter);
                        }
                        finally { Gui.EndPopup(); }
                    }
                }
                finally { Gui.EndTable(); }
            }
            finally { Gui.PopID(); }
        }
    }
    private void DrawExtensionVirtualCommands(AssetMetadataDocument owner, VirtualNodePresenter presenter)
    {
        var editable = !IsAuthoringPipelineUpdating && owner.CanEditExtensionVirtualProperty(presenter);
        if (ContentAssetReference.Supports(presenter.Type)) DrawContentReferenceFetchActions([owner.ReadExtensionVirtualProperty(presenter)]);
        if (owner.CanCopyVirtualProperty(presenter))
        {
            if (Gui.MenuItem(EditorLocalization.Widget("Copy", "Menu"))) Execute(() => Gui.SetClipboardText(owner.CopyVirtualProperty(presenter)));
            var text = Gui.GetClipboardTextS() ?? "";
            var canPaste = owner.CanPasteVirtualProperty(presenter, text);
            if (Gui.MenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste)) Execute(() => owner.PasteVirtualProperty(presenter, text, false));
            if (Gui.MenuItem("Replace", "", false, canPaste)) Execute(() => owner.PasteVirtualProperty(presenter, text, true));
        }
        var accessor = presenter.GetNodeAccessor();
        if (accessor.Node is { } node)
            DrawRegisteredPropertyCommands(owner, node, editable, owner.ApplyInheritedAssetMutation);
        DrawNativeVirtualFlagActions(presenter, editable, () => owner.ReadExtensionVirtualProperty(presenter),
            replacement => { owner.BufferExtensionVirtualProperty(presenter, replacement); owner.EndExtensionVirtualEdit(); });
        foreach (var command in presenter.Commands)
            if (Gui.MenuItem(command.Name, "", false, !IsAuthoringPipelineUpdating && command.CanExecute([presenter], null)))
                Execute(() => StartEditorExtensionCommand(owner, owner.AcquireExtensionVirtualTarget(presenter), command));
    }
    private void DrawNativeVirtualScalar(AssetMetadataDocument owner, VirtualNodePresenter presenter, Type type, object? value, bool editable)
        => DrawNativeVirtualScalar(owner, presenter, type, value, editable, owner.ExtensionNumericRange(presenter),
            () => owner.CanEditExtensionVirtualProperty(presenter), () => owner.ReadExtensionVirtualProperty(presenter),
            updated => owner.BufferExtensionVirtualProperty(presenter, updated), owner.EndExtensionVirtualEdit, axis =>
            {
                var name = NativeNumericAxes.Names(presenter.Type).ElementAtOrDefault(axis);
                var child = name == null ? null : presenter.TryGetChild(name);
                return child is VirtualNodePresenter virtualChild ? owner.ExtensionNumericRange(virtualChild)
                    : child?.GetNodeAccessor().Node is Stride.Core.Quantum.IMemberNode member ? owner.ExtensionNumericRange(member) : null;
            });
    private void DrawNativeVirtualScalar(IEditorDocument owner, VirtualNodePresenter presenter, Type type, object? value, bool editable,
        PropertyNumericRange? range, Func<bool> canEdit, Func<object?> read, Action<object?> assign, Action end, Func<int, PropertyNumericRange?>? axisRange = null, bool nullablePresence = true,
        IReadOnlyList<PropertyNumericRange?>? numericRanges = null, bool mixed = false, Func<object?[]>? readValues = null,
        Func<IDisposable>? colorOwner = null)
    {
        using var nullableRow = NativeNullableRow.Begin("##Nullable", nullablePresence && Nullable.GetUnderlyingType(presenter.Type) != null);
        if (nullablePresence && Nullable.GetUnderlyingType(presenter.Type) != null)
        {
            var present = value != null;
            Gui.BeginDisabled(!editable);
            if (DrawNativeNullablePresence(present || mixed, out var create) && editable)
                Execute(() => { assign(create ? CreateNullableValue(type) : null); end(); });
            Gui.EndDisabled();
            nullableRow.ValueColumn();
            if (value == null && !mixed) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); return; }
        }
        if (type == typeof(UFile) || type == typeof(UDirectory))
        {
            DrawTypedPropertyPath(owner, type, value, () => editable && !IsAuthoringPipelineUpdating && canEdit(), read,
                path => { assign(path); end(); }, mixed, presenter, () => readValues?.Invoke() ?? [read()],
                colorOwner ?? (() => AcquireRetainedVirtualColor(owner, presenter)), readValues?.Invoke().Length ?? 1);
            return;
        }
        if (IsTemporalType(type))
        {
            DrawRetainedTemporal(presenter, type, canEdit, () => readValues?.Invoke() ?? [read()],
                transform => { assign(transform(read())); end(); }, readValues?.Invoke().Length ?? 1,
                colorOwner ?? (() => AcquireRetainedVirtualColor(owner, presenter)));
            return;
        }
        if (type == typeof(char))
        {
            DrawRetainedCharacter(presenter, () => readValues?.Invoke() ?? [read()], canEdit, character => { assign(character); end(); },
                colorOwner ?? (() => AcquireRetainedVirtualColor(owner, presenter)), readValues?.Invoke().Length ?? 1);
            return;
        }
        if (type == typeof(Guid))
        {
            DrawRetainedGuid(presenter, () => readValues?.Invoke() ?? [read()], canEdit, guid => { assign(guid); end(); },
                colorOwner ?? (() => AcquireRetainedVirtualColor(owner, presenter)), readValues?.Invoke().Length ?? 1);
            return;
        }
        if (type == typeof(AngleSingle))
        {
            DrawRetainedAngle(presenter, () => readValues?.Invoke() ?? [read()], canEdit, angle => { assign(angle); end(); },
                colorOwner ?? (() => AcquireRetainedVirtualColor(owner, presenter)), readValues?.Invoke().Length ?? 1);
            return;
        }
        if (DrawNativeScalarTemplate(presenter, type, value, mixed, editable, replacement => Execute(() => { assign(replacement); end(); }))) return;
        if (type == typeof(string))
        {
            DrawNativeVirtualString(presenter, mixed, editable, canEdit, read, assign, end, readValues);
            return;
        }
        if (IsPackageColorType(type))
        {
            DrawRetainedColor(presenter, type, () => readValues?.Invoke() ?? [read()],
                () => !IsAuthoringPipelineUpdating && canEdit(), create => { assign(create(read())); end(); },
                colorOwner ?? (() => AcquireRetainedVirtualColor(owner, presenter)),
                axis => !NativeNumericAxes.IsReadOnly(presenter, axis), valueCount: readValues?.Invoke().Length ?? 1);
            return;
        }
        if (AssetSettingNumericMetadata.Supports(type))
        {
            DrawNativeVirtualNumber(owner, presenter, type, value, mixed, range, numericRanges, canEdit, read, assign, end,
                readValues, colorOwner);
            return;
        }
        if (type == typeof(Quaternion) || NativeNumericAxes.Names(type).Count > 0)
        {
            DrawRetainedVirtualMath(presenter, type, [presenter], () => readValues?.Invoke() ?? [read()], canEdit,
                axis => [axisRange?.Invoke(axis) ?? range],
                transform => { assign(transform(0, read())); end(); },
                colorOwner ?? (() => AcquireRetainedVirtualColor(owner, presenter)));
            return;
        }
        object? updated = value; var changed = false; var discrete = false; var deactivated = false;
        if (type == typeof(bool)) { var next = (bool?)value ?? false; changed = Gui.Checkbox("##Value", ref next); updated = next; discrete = true; }
        else if (type.IsEnum && Nullable.GetUnderlyingType(presenter.Type) == null && icons?.HasEnumImages(type) == true)
        {
            changed = NativeEnumImageControl.Draw(icons, presenter, type, value, mixed, Enum.GetValues(type).Cast<Enum>(), UiScale(1), out updated);
            discrete = true;
        }
        else if (type.IsEnum)
        {
            if (Gui.BeginCombo("##Value", value?.ToString() ?? ""))
            {
                foreach (var name in Enum.GetNames(type))
                {
                    var option = Enum.Parse(type, name);
                    if (type.IsDefined(typeof(FlagsAttribute), true))
                    {
                        var bits = EnumBits(updated ?? Enum.ToObject(type, 0)); var mask = EnumBits(option);
                        var enabled = mask == 0 ? bits == 0 : (bits & mask) == mask;
                        if (Gui.Checkbox(name, ref enabled)) { updated = EnumFromBits(type, mask == 0 ? 0 : enabled ? bits | mask : bits & ~mask); changed = true; }
                    }
                    else if (Gui.Selectable(name, Equals(value, option))) { updated = option; changed = true; }
                }
                if (type.IsDefined(typeof(FlagsAttribute), true) && DrawFlagBulkActions() is { } action)
                { updated = ApplyFlagBulkAction((Enum)(updated ?? Enum.ToObject(type, 0)), action); changed = true; }
                Gui.EndCombo();
            }
            discrete = true;
        }
        else if (value is Color color)
        {
            var channels = new System.Numerics.Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
            changed = Gui.ColorEdit4("##Value", ref channels);
            updated = new Color((byte)Math.Clamp(Math.Round(channels.X * 255), 0, 255), (byte)Math.Clamp(Math.Round(channels.Y * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(channels.Z * 255), 0, 255), (byte)Math.Clamp(Math.Round(channels.W * 255), 0, 255));
        }
        else if (value is Color3 or Color4)
            DrawCommonColorValue(NativePresenterCategories.Identity(presenter), type, [value], transform => Execute(() => { assign(transform(value)); end(); }));
        else DrawSelectableText("##Value", value?.ToString() ?? "");
        if (changed && editable) Execute(() => assign(updated));
        if (editable && (discrete && changed || deactivated || Gui.IsItemDeactivatedAfterEdit())) Execute(end);
    }
}
