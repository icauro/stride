using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Core.Reflection;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawFontItemValue(AssetMetadataDocument owner, FontSettingField field, FontSettingItem item)
    {
        var declared = (field.Collection?.Descriptor as CollectionDescriptor)?.ElementType;
        if (declared == null) return;
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        var scalar = AssetSettingNumericMetadata.Supports(type) || type == typeof(bool) || type == typeof(char) || type.IsEnum || type == typeof(string)
            || type == typeof(Guid) || type == typeof(AngleSingle) || IsTemporalType(type) || IsPathType(type)
            || IsPackageColorType(type) || type == typeof(Quaternion) || NativeNumericAxes.Names(type).Count != 0
            || ContentAssetReference.Supports(declared);
        if (!scalar) { DrawPresentedFontFields(owner, item.Fields); return; }
        var initial = owner.AcquireFontItemCommandTarget(field, item)
            ?? throw new InvalidOperationException("The font item presenter changed.");
        using var held = new RetainedPathLease(this, initial);
        var node = initial.Presenter; var version = owner.SessionHistory?.Extensions?.Version;
        bool Current() => IsCurrentMetadataControlOwner(owner) && owner.SessionHistory?.Extensions?.Version == version
            && owner.IsCurrentFontItemProperty(field, item.Id, item.Index, node);
        bool Editable() => !IsAuthoringPipelineUpdating && Current() && owner.CanEditFontItemProperty(field, item.Id, item.Index, node);
        object?[] Target() => [node.Value, node, node.GetNodeAccessor().Node, node.GetNodeAccessor().Index, field.Node, field.Key,
            field.Collection, item.Id, field.Node.Parent.Retrieve(), owner.Path, node.Value is DateTime date ? date.Kind : null,
            sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation, owner.SessionHistory?.Extensions?.Version];
        object?[] Read() => [.. Target(), owner.EditRevision];
        IDisposable Acquire()
        {
            var leases = new List<IDisposable>();
            try
            {
                if (!Current()) throw new InvalidOperationException("The font item context changed.");
                leases.Add(AcquirePropertyStringOwner(owner));
                leases.Add(owner.AcquireExtensionFactoryTarget(node.GetNodeAccessor())
                    ?? throw new InvalidOperationException("The font item target changed."));
                return new PropertyStringOwners(leases, [owner]);
            }
            catch (Exception failure)
            {
                var cleanup = new PropertyStringOwners(leases, [owner]);
                try { cleanup.Dispose(); } catch (Exception release) { packageCommandCleanup.Add(cleanup.Dispose); throw new AggregateException(failure, release); }
                throw;
            }
        }
        void Apply(Func<object?, object?> transform)
        {
            if (!Editable()) throw new InvalidOperationException("The font item is read-only or changed.");
            owner.UpdateFontItemProperty(field, item.Id, item.Index, node, transform(node.Value));
        }
        void Discrete(object? replacement)
        {
            if (!Editable()) throw new InvalidOperationException("The font item is read-only or changed.");
            if (ContentAssetReference.AreEqual(node.Value, replacement)) return;
            var expected = Target(); ValidatePendingPropertyStrings();
            if (!Editable() || !expected.SequenceEqual(Target())) throw new InvalidOperationException("The font item changed during pending validation.");
            using var lease = new RetainedPathLease(this, Acquire()); Apply(_ => replacement);
        }
        var value = node.Value;
        var nullableStruct = Nullable.GetUnderlyingType(declared)?.IsStruct() == true;
        using var nullable = NativeNullableRow.Begin("##Nullable", nullableStruct);
        if (nullableStruct)
        {
            var supplied = owner.ExtensionAbstractEntries(field.Collection!, node.GetNodeAccessor().Index);
            var entries = NativeAbstractEntries.Available(supplied, [type], owner.FontItemAllowsNull(field, node));
            var present = value != null; var entry = NativeAbstractEntries.TypeEntry(entries, present ? null : type);
            using (ImGuiScopes.Disabled(!Editable() || entry == null || !CanExecuteNativeFactory(owner, node.GetNodeAccessor(), [entry])))
                if (DrawNativeNullablePresence(present, out _) && entry != null)
                    SelectNativeFactoryEntry(owner, node.GetNodeAccessor(), entry,
                        () => NativeAbstractEntries.Available(owner.ExtensionAbstractEntries(field.Collection!, node.GetNodeAccessor().Index), [type], owner.FontItemAllowsNull(field, node)),
                        () => Discrete(entry.GenerateValue(node.Value!)));
            nullable.ValueColumn();
            if (value == null) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); return; }
        }
        if (ContentAssetReference.Supports(declared))
        {
            object?[] expected = Read();
            using var resources = BeginContentReferenceResources(Editable, Acquire, () => { if (!Editable()) throw new InvalidOperationException("The font item context changed."); expected = Read(); });
            DrawContentReferenceValue(declared, value, owner.FontItemAllowsNull(field, node), replacement =>
            { if (!expected.SequenceEqual(Read())) throw new InvalidOperationException("The font item reference changed."); Discrete(replacement); }, canEdit: Editable());
        }
        else if (type == typeof(string)) PropertyGrid.Strings.Draw(node, false, Editable, Read, text => Apply(_ => text), Acquire);
        else if (IsPathType(type)) DrawRetainedPath(owner, node, type, Editable, Read, path => Apply(_ => path), 1, Acquire);
        else if (type == typeof(Guid)) DrawRetainedGuid(node, Read, Editable, replacement => Apply(_ => replacement), Acquire);
        else if (type == typeof(char)) DrawRetainedCharacter(node, Read, Editable, replacement => Apply(_ => replacement), Acquire,
            assignGlyph: replacement => Apply(_ => replacement == '\0' && Nullable.GetUnderlyingType(declared) != null ? null : replacement));
        else if (type == typeof(AngleSingle)) DrawRetainedAngle(node, Read, Editable, replacement => Apply(_ => replacement), Acquire);
        else if (IsTemporalType(type)) DrawRetainedTemporal(node, type, Editable, Read, Apply, 1, Acquire);
        else if (IsPackageColorType(type)) DrawRetainedColor(node, type, Read, Editable, Apply, Acquire, axis => !NativeNumericAxes.IsReadOnly(node, axis));
        else if (type == typeof(Quaternion) || NativeNumericAxes.Names(type).Count != 0)
            DrawRetainedVirtualMath(node, type, [node], Read, Editable, axis => [owner.FontItemAxisRanges(node).ElementAtOrDefault(axis)],
                transform => Apply(old => transform(0, old)), Acquire);
        else if (AssetSettingNumericMetadata.Supports(type))
        {
            var range = owner.FontItemNumericRange(node); var ranged = NativeRangedNumericControl.Matches(declared, range);
            object? display = value; var active = false; var complete = false;
            if (ranged) { using var disabled = ImGuiScopes.Disabled(!Editable()); complete = NativeRangedNumericControl.Draw(node, declared, value, false, [range], UiScale(1), out display, out _); active = Gui.IsItemActive(); }
            PropertyGrid.Strings.Draw(node, false, Editable, Read, text => Apply(_ => NativeNumericText.Parse(text, type, range)), Acquire,
                text => StepPackageNumeric(type, declared, node.Value, range, ref text) ? text : null,
                raw => NativeNumericText.Format(raw, type, range),
                externalText: ranged && display != null && (active || complete) ? NativeNumericText.Format(display, type, range) : null,
                externalActive: active, externalCommit: complete);
        }
        else
        {
            using var disabled = ImGuiScopes.Disabled(!Editable());
            if (type == typeof(bool))
            {
                var flag = value == null || value is true; Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, value == null);
                try { if (DrawNativeCheckbox("##Value", ref flag)) Execute(() => Discrete(flag)); } finally { Gui.PopItemFlag(); }
            }
            else if (declared == type && icons?.HasEnumImages(type) == true)
            { if (NativeEnumImageControl.Draw(icons, node, type, value, false, Enum.GetValues(type).Cast<Enum>(), UiScale(1), out var chosen)) Execute(() => Discrete(chosen)); }
            else if (type.IsEnum && Gui.BeginCombo("##Value", value == null ? EditorLocalization.Text("(None)") : EnumLabel(value)))
            {
                try
                {
                    foreach (var name in Enum.GetNames(type))
                    {
                        var option = Enum.Parse(type, name);
                        if (!type.IsDefined(typeof(FlagsAttribute), false)) { if (Gui.Selectable(EnumLabel(option), Equals(value, option))) Execute(() => Discrete(option)); continue; }
                        var mask = EnumBits(option); var bits = EnumBits(value ?? Enum.ToObject(type, 0)); var selected = mask == 0 ? bits == 0 : (bits & mask) == mask;
                        if (DrawNativeCheckbox(EnumLabel(option), ref selected)) Execute(() => Discrete(EnumFromBits(type, mask == 0 ? 0 : selected ? bits | mask : bits & ~mask)));
                    }
                    if (type.IsDefined(typeof(FlagsAttribute), false) && DrawFlagBulkActions() is { } action)
                        Execute(() => Discrete(ApplyFlagBulkAction((Enum)(node.Value ?? Enum.ToObject(type, 0)), action)));
                }
                finally { Gui.EndCombo(); }
            }
        }
        string EnumLabel(object option)
        {
            var name = option.ToString()!;
            return type.GetField(name)?.GetCustomAttributes(typeof(Stride.Core.DisplayAttribute), false)
                .OfType<Stride.Core.DisplayAttribute>().FirstOrDefault()?.Name ?? name;
        }
    }
}
