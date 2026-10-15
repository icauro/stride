using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Core.Reflection;
using Stride.Core.Extensions;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawGameFieldValueDraft(AssetMetadataDocument owner, GameSettingField field, string label)
    {
        // The original unloadable header overrides value templates; keep its
        // actual containing-field constructor/context route in the caller.
        if (field.Node.Retrieve() is Stride.Core.Yaml.IUnloadable) return false;
        var type = field.Type; var declared = field.Node.Type;
        var registered = owner.SessionHistory?.Extensions?.HasPrimitiveEditor(declared) == true;
        var math = type == typeof(Quaternion) || type == typeof(Matrix) || IsPackageVectorType(type) || IsPackageRectangleType(type);
        var reference = ContentAssetReference.Supports(declared);
        var special = IsPathType(type) || type == typeof(Guid) || type == typeof(char) || type == typeof(AngleSingle) || IsTemporalType(type);
        if (!registered && (field.Children.Count != 0 || field.IsCollection || !special && !math && !reference
            && !IsPackageColorType(type) && !AssetSettingNumericMetadata.Supports(type) && type != typeof(string) && type != typeof(bool) && !type.IsEnum)) return false;
        if (!registered && owner is VisualScriptDocument && type == typeof(string)
            && field.Node.MemberDescriptor.GetCustomAttributes<Stride.Assets.Scripts.ScriptVariableReferenceAttribute>(true).Any()) return false;
        var nullableStruct = Nullable.GetUnderlyingType(declared)?.IsStruct() == true;
        if (!registered && !nullableStruct && owner.ExtensionAbstractEntries(field.Node) != null) return false;
        var target = owner.AcquireExtensionCommandTarget(field.Node)
            ?? throw new InvalidOperationException("The settings property presenter changed.");
        using var held = new RetainedPathLease(this, target);
        var node = target.Presenter; var version = owner.SessionHistory?.Extensions?.Version;
        bool Current() => IsCurrentMetadataControlOwner(owner) && owner.SessionHistory?.Extensions?.Version == version
            && owner.IsCurrentGameSettingField(field) && ReferenceEquals(owner.GameFieldPresenter(field), node);
        bool Editable() => !IsAuthoringPipelineUpdating && Current() && owner.CanEditPresentedGameSetting(field);
        object?[] Target() => [field.Node.Retrieve(), field.Node, field.Key, field.Node.Parent.Retrieve(), declared, owner.Path,
            field.Node.Retrieve() is DateTime date ? date.Kind : null,
            sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation, owner.SessionHistory?.Extensions?.Version];
        object?[]? completedRead = null;
        object?[] Read() => completedRead ?? [.. Target(), owner.EditRevision];
        IDisposable Acquire()
        {
            var leases = new List<IDisposable>();
            try
            {
                if (!Current()) throw new InvalidOperationException("The settings property context changed.");
                leases.Add(AcquirePropertyStringOwner(owner));
                leases.Add(owner.AcquireExtensionCommandTarget(field.Node)
                    ?? throw new InvalidOperationException("The settings property target changed."));
                return new PropertyStringOwners(leases, [owner]);
            }
            catch (Exception failure)
            {
                var cleanup = new PropertyStringOwners(leases, [owner]);
                try { cleanup.Dispose(); }
                catch (Exception release) { packageCommandCleanup.Add(cleanup.Dispose); throw new AggregateException(failure, release); }
                throw;
            }
        }
        void Assign(object? value)
        {
            if (!Editable()) throw new InvalidOperationException("The setting is read-only or changed.");
            var before = Read(); var assigned = owner.UpdateGameFieldValue(field, value);
            if (!Current()) completedRead = [assigned, .. before.Skip(1).SkipLast(1), owner.EditRevision];
        }
        void Discrete(object? value)
        {
            if (!Editable()) throw new InvalidOperationException("The setting is read-only or changed.");
            if (ContentAssetReference.AreEqual(field.Node.Retrieve(), value)) return;
            var before = Target(); ValidatePendingPropertyStrings();
            if (!Editable() || !before.SequenceEqual(Target())) throw new InvalidOperationException("The setting changed during pending validation.");
            using var lease = new RetainedPathLease(this, Acquire()); Assign(value);
        }
        if (registered) return TryDrawCertifiedPropertyEditor(owner, field.Node, label, Current, Editable,
            () => field.Node.Retrieve(), Read, Target, Assign, () => { });
        if (!BeginInspectorPropertyRow(label, false)) return true;
        try
        {
            Gui.TableSetColumnIndex(0); DrawAssetSettingLabel(owner, field.Node, label);
            Gui.TableSetColumnIndex(1); Gui.SetNextItemWidth(-1);
            using var nullable = NativeNullableRow.Begin("##Nullable", nullableStruct);
            if (nullableStruct)
            {
                var entries = owner.GameSettingEntries(field); var present = field.Node.Retrieve() != null;
                var entry = NativeAbstractEntries.TypeEntry(entries, present ? null : type);
                using (ImGuiScopes.Disabled(!Editable() || entry == null || !CanExecuteNativeFactory(owner, new(field.Node, Stride.Core.Quantum.NodeIndex.Empty), [entry])))
                    if (DrawNativeNullablePresence(present, out _, field.AllowsNull) && entry != null)
                        SelectNativeFactoryEntry(owner, field.Node, entry, () => owner.GameSettingEntries(field),
                            () => Discrete(entry.GenerateValue(field.Node.Retrieve()!)));
                nullable.ValueColumn();
                if (!present) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); return true; }
            }
            var value = field.Node.Retrieve();
            if (reference)
            {
                object?[] expected = Read();
                using var resources = BeginContentReferenceResources(Editable, Acquire,
                    () => { if (!Editable()) throw new InvalidOperationException("The settings reference changed."); expected = Read(); });
                DrawContentReferenceValue(declared, value, field.AllowsNull, next =>
                { if (!expected.SequenceEqual(Read())) throw new InvalidOperationException("The settings reference changed."); Discrete(next); }, canEdit: Editable());
            }
            else if (type == typeof(string)) PropertyGrid.Strings.Draw(node, false, Editable, Read, Assign, Acquire);
            else if (IsPathType(type)) DrawRetainedPath(owner, node, type, Editable, Read, Assign, 1, Acquire);
            else if (type == typeof(Guid)) DrawRetainedGuid(node, Read, Editable, next => Assign(next), Acquire);
            else if (type == typeof(char)) DrawRetainedCharacter(node, Read, Editable, next => Assign(next), Acquire,
                assignGlyph: next => Assign(next == '\0' && Nullable.GetUnderlyingType(declared) != null ? null : next));
            else if (type == typeof(AngleSingle)) DrawRetainedAngle(node, Read, Editable, next => Assign(next), Acquire);
            else if (IsTemporalType(type)) DrawRetainedTemporal(node, type, Editable, Read, transform => Assign(transform(field.Node.Retrieve())), 1, Acquire);
            else if (IsPackageColorType(type)) DrawRetainedColor(node, type, Read, Editable, transform => Assign(transform(field.Node.Retrieve())), Acquire,
                axis => !NativeNumericAxes.IsReadOnly(node, axis));
            else if (math) DrawRetainedVirtualMath(node, type, [node], Read, Editable,
                axis => [owner.GameFieldAxisRanges(field).ElementAtOrDefault(axis)], transform => Assign(transform(0, field.Node.Retrieve())), Acquire);
            else if (type == typeof(bool))
            {
                using var disabled = ImGuiScopes.Disabled(!Editable()); var flag = value == null || value is true;
                Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, value == null);
                try { if (DrawNativeCheckbox("##Value", ref flag)) Execute(() => Discrete(flag)); } finally { Gui.PopItemFlag(); }
            }
            else if (type.IsEnum)
            {
                var flags = declared.IsDefined(typeof(FlagsAttribute), false);
                var options = type.IsDefined(typeof(FlagsAttribute), false) ? EditorFlagEnums.GetIndividualFlags(type).ToArray() : Enum.GetValues(type).Cast<Enum>().Distinct().ToArray();
                using var disabled = ImGuiScopes.Disabled(!Editable());
                if (icons?.HasEnumImages(declared) == true)
                { if (NativeEnumImageControl.Draw(icons, node, type, value, false, options, UiScale(1), out var next)) Execute(() => Discrete(next)); }
                else if (BeginNativeCombo("##Value", flags ? EditorLocalization.Text("Change values...", "Button") : PackageEnumDisplay(value)))
                {
                    try
                    {
                        foreach (var option in options)
                        {
                            Gui.PushID(option.ToString());
                            try
                            {
                                if (flags)
                                {
                                    var mask = EnumBits(option); var bits = EnumBits(value!); var selected = (bits & mask) == mask;
                                    if (DrawNativeCheckbox(PackageEnumDisplay(option), ref selected)) Execute(() => Discrete(EnumFromBits(type, selected ? bits | mask : bits & ~mask)));
                                }
                                else if (DrawNativeSelectable(PackageEnumDisplay(option), Equals(value, option))) Execute(() => Discrete(option));
                            }
                            finally { Gui.PopID(); }
                        }
                        if (flags && DrawFlagBulkActions() is { } action) Execute(() => Discrete(ApplyFlagBulkAction((Enum)field.Node.Retrieve()!, action)));
                    }
                    finally { Gui.EndCombo(); }
                }
            }
            else
            {
                var range = owner.GameFieldNumericRange(field); var ranged = NativeRangedNumericControl.Matches(declared, range);
                object? display = value; var active = false; var complete = false;
                if (ranged)
                {
                    using var disabled = ImGuiScopes.Disabled(!Editable());
                    complete = NativeRangedNumericControl.Draw(node, declared, value, false, [range], UiScale(1), out display, out _); active = Gui.IsItemActive();
                }
                PropertyGrid.Strings.Draw(node, false, Editable, Read, text => Assign(NativeNumericText.Parse(text, type, range)), Acquire,
                    text => StepPackageNumeric(type, declared, field.Node.Retrieve(), range, ref text) ? text : null,
                    raw => NativeNumericText.Format(raw, type, range),
                    externalText: ranged && display != null && (active || complete) ? NativeNumericText.Format(display, type, range) : null,
                    externalActive: active, externalCommit: complete);
            }
            DrawAssetSettingResetContext(owner, field.Node);
        }
        finally { Gui.EndTable(); }
        return true;
    }
}
