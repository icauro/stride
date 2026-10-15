using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Mathematics;
using Stride.Core.Reflection;
using Stride.Core.Extensions;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool TryDrawGameItemValueDraft(AssetMetadataDocument owner, GameSettingItem item)
    {
        if (item.Value is Stride.Core.Yaml.IUnloadable) return false;
        var type = item.Type;
        var nullableStruct = Nullable.GetUnderlyingType(item.DeclaredType)?.IsStruct() == true;
        var special = IsPathType(type) || type == typeof(Guid) || type == typeof(char) || type == typeof(AngleSingle) || IsTemporalType(type);
        var math = type == typeof(Quaternion) || type == typeof(Matrix) || IsPackageVectorType(type) || IsPackageRectangleType(type);
        var reference = ContentAssetReference.Supports(item.DeclaredType);
        var extensions = owner.SessionHistory?.Extensions;
        var registered = extensions?.HasPrimitiveEditor(item.DeclaredType) == true;
        if (!registered && (item.Children.Count != 0 || type != typeof(string) && !AssetSettingNumericMetadata.Supports(type) && !special
            && !math && !IsPackageColorType(type) && type != typeof(bool) && !type.IsEnum && !reference)) return false;
        // Supplied entries retain their contextual template unless an exact editor overrides it.
        if (!nullableStruct && !registered
            && owner.ExtensionAbstractEntries(item.Collection, item.Index) != null) return false;
        var initial = owner.AcquireGameItemCommandTarget(item)
            ?? throw new InvalidOperationException("The settings item presenter changed.");
        using var held = new RetainedPathLease(this, initial);
        var node = initial.Presenter;
        var version = extensions?.Version;
        bool Current() => IsCurrentMetadataControlOwner(owner) && extensions?.Version == version
            && owner.IsCurrentGameItemValue(item, node);
        bool Editable() => !IsAuthoringPipelineUpdating && Current() && owner.CanEditPresentedGameSettingItem(item)
            && !initial.Presenters.Any(presenter => presenter.IsReadOnly);
        object?[] Target() => [node.Value, node, node.GetNodeAccessor().Node, node.GetNodeAccessor().Index,
            item.Collection, item.Context, item.Id, item.Index, item.Field.Node, item.Field.Key,
            item.Field.Node.Parent.Retrieve(), item.Context.Parent?.Value, owner.Path,
            node.Value is DateTime date ? date.Kind : null,
            sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation, extensions?.Version];
        object?[]? completedRead = null;
        object?[] Read() => completedRead ?? [.. Target(), owner.EditRevision];
        IDisposable Acquire()
        {
            var leases = new List<IDisposable>();
            try
            {
                if (!Current()) throw new InvalidOperationException("The settings item context changed.");
                leases.Add(AcquirePropertyStringOwner(owner));
                leases.Add(owner.AcquireGameItemCommandTarget(item)
                    ?? throw new InvalidOperationException("The settings item target changed."));
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
            if (!Editable()) throw new InvalidOperationException("The settings item is read-only or changed.");
            var baseline = Read();
            var assigned = owner.UpdateGameItemScalarValue(item, node, value);
            // Set replacement or a full journal refresh can retire this exact item
            // before the control's post-commit formatter runs. Retain only the actual
            // committed result for that formatter; Current still rejects further edits.
            if (!Current()) completedRead = [assigned, .. baseline.Skip(1).SkipLast(1), owner.EditRevision];
        }
        void Discrete(object? value)
        {
            if (!Editable()) throw new InvalidOperationException("The settings item is read-only or changed.");
            if (ContentAssetReference.AreEqual(node.Value, value)) return;
            var before = Target(); ValidatePendingPropertyStrings();
            if (!Editable() || !before.SequenceEqual(Target())) throw new InvalidOperationException("The settings item changed during pending validation.");
            using var lease = new RetainedPathLease(this, Acquire()); Assign(value);
        }
        if (extensions?.HasPrimitiveEditor(item.DeclaredType) == true)
        {
            using var ownerLease = new RetainedPathLease(this, AcquirePropertyStringOwner(owner));
            var rendering = true; var expected = Read();
            bool ContextCurrent() => rendering && !IsAuthoringPipelineUpdating && Current() && expected.SequenceEqual(Read());
            void Validate()
            {
                if (!ContextCurrent()) throw new InvalidOperationException("The settings item editor context changed.");
                var before = Target(); ValidatePendingPropertyStrings();
                if (!rendering || !Current() || IsAuthoringPipelineUpdating || !before.SequenceEqual(Target()))
                    throw new InvalidOperationException("The settings item changed during pending validation.");
                expected = Read();
            }
            try
            {
                extensions.TryDrawPrimitive(new(item.DeclaredType, node.Value, "##GameItemValue", Editable(), value =>
                {
                    if (!ContextCurrent() || !Editable()) throw new InvalidOperationException("The settings item is read-only or changed.");
                    if (Equals(node.Value, value)) return;
                    Validate(); UseTextureSettingsCommandContext(); Assign(value); expected = Read();
                }, () => { if (!rendering) throw new InvalidOperationException("The settings item editor context expired."); }, (name, parameter) =>
                {
                    if (!ContextCurrent()) throw new InvalidOperationException("The settings item command context changed.");
                    var command = NodePresenterCombination.Commands(initial.Presenters)
                        .SingleOrDefault(candidate => candidate.Name == name && candidate.CanExecute(initial.Presenters, parameter))
                        ?? throw new InvalidOperationException("The native settings item command is not available.");
                    Validate();
                    var actorTarget = owner.AcquireGameItemCommandTarget(item)
                        ?? throw new InvalidOperationException("The settings item command target changed.");
                    var transferred = false;
                    try
                    {
                        if (!NodePresenterCombination.Commands(actorTarget.Presenters).Contains(command)
                            || !command.CanExecute(actorTarget.Presenters, parameter))
                            throw new InvalidOperationException("The settings item command changed during pending validation.");
                        transferred = true; StartEditorExtensionCommand(owner, actorTarget, command, parameter);
                    }
                    finally { if (!transferred) ReleaseHierarchyFactoryTarget(actorTarget); }
                }));
            }
            finally { rendering = false; }
            return true;
        }
        using var nullable = NativeNullableRow.Begin("##Nullable", nullableStruct);
        if (nullableStruct)
        {
            var entries = owner.GameItemEntries(item); var present = node.Value != null;
            var entry = NativeAbstractEntries.TypeEntry(entries, present ? null : type);
            using (ImGuiScopes.Disabled(!Editable() || entry == null || !CanExecuteNativeFactory(owner, node.GetNodeAccessor(), [entry])))
                if (DrawNativeNullablePresence(present, out _, item.AllowsNull) && entry != null)
                    SelectNativeFactoryEntry(owner, node.GetNodeAccessor(), entry, () => owner.GameItemEntries(item),
                        () => Assign(entry.GenerateValue(node.Value!)));
            nullable.ValueColumn();
            if (!present) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); return true; }
        }
        if (reference)
        {
            object?[] expected = Read();
            using var resources = BeginContentReferenceResources(Editable, Acquire,
                () => { if (!Editable()) throw new InvalidOperationException("The settings item context changed."); expected = Read(); });
            DrawContentReferenceValue(item.DeclaredType, node.Value, item.AllowsNull, value =>
            {
                if (!expected.SequenceEqual(Read())) throw new InvalidOperationException("The settings item reference changed.");
                Discrete(value);
            }, canEdit: Editable());
        }
        else if (type == typeof(string))
            PropertyGrid.Strings.Draw(node, false, Editable, Read, text => Assign(text), Acquire);
        else if (IsPathType(type)) DrawRetainedPath(owner, node, type, Editable, Read, Assign, 1, Acquire);
        else if (type == typeof(Guid)) DrawRetainedGuid(node, Read, Editable, value => Assign(value), Acquire);
        else if (type == typeof(char)) DrawRetainedCharacter(node, Read, Editable, value => Assign(value), Acquire,
            assignGlyph: value => Assign(value == '\0' && Nullable.GetUnderlyingType(item.DeclaredType) != null ? null : value));
        else if (type == typeof(AngleSingle)) DrawRetainedAngle(node, Read, Editable, value => Assign(value), Acquire);
        else if (IsTemporalType(type)) DrawRetainedTemporal(node, type, Editable, Read,
            transform => Assign(transform(node.Value)), 1, Acquire);
        else if (IsPackageColorType(type)) DrawRetainedColor(node, type, Read, Editable,
            transform => Assign(transform(node.Value)), Acquire, axis => !NativeNumericAxes.IsReadOnly(node, axis));
        else if (math) DrawRetainedVirtualMath(node, type, [node], Read, Editable,
            axis => [owner.GameItemAxisRanges(node).ElementAtOrDefault(axis)],
            transform => Assign(transform(0, node.Value)), Acquire);
        else if (type == typeof(bool))
        {
            using var disabled = ImGuiScopes.Disabled(!Editable());
            var flag = node.Value == null || node.Value is true;
            Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, node.Value == null);
            try { if (DrawNativeCheckbox("##Value", ref flag)) Execute(() => Discrete(flag)); }
            finally { Gui.PopItemFlag(); }
        }
        else if (type.IsEnum)
        {
            var value = node.Value; var flags = item.DeclaredType.IsDefined(typeof(FlagsAttribute), false);
            var options = type.IsDefined(typeof(FlagsAttribute), false) ? EditorFlagEnums.GetIndividualFlags(type).ToArray()
                : Enum.GetValues(type).Cast<Enum>().Distinct().ToArray();
            using var disabled = ImGuiScopes.Disabled(!Editable());
            if (icons?.HasEnumImages(item.DeclaredType) == true)
            {
                if (NativeEnumImageControl.Draw(icons, node, type, value, false, options, UiScale(1), out var next))
                    Execute(() => Discrete(next));
            }
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
                                if (DrawNativeCheckbox(PackageEnumDisplay(option), ref selected))
                                    Execute(() => Discrete(EnumFromBits(type, selected ? bits | mask : bits & ~mask)));
                            }
                            else if (DrawNativeSelectable(PackageEnumDisplay(option), Equals(option, value))) Execute(() => Discrete(option));
                        }
                        finally { Gui.PopID(); }
                    }
                    if (flags && DrawFlagBulkActions() is { } action) Execute(() => Discrete(ApplyFlagBulkAction((Enum)node.Value!, action)));
                }
                finally { Gui.EndCombo(); }
            }
        }
        else
        {
            var range = owner.GameItemNumericRange(node);
            var ranged = NativeRangedNumericControl.Matches(item.DeclaredType, range);
            object? display = node.Value; var active = false; var complete = false;
            if (ranged)
            {
                using var disabled = ImGuiScopes.Disabled(!Editable());
                complete = NativeRangedNumericControl.Draw(node, item.DeclaredType, node.Value, false, [range], UiScale(1), out display, out _);
                active = Gui.IsItemActive();
            }
            PropertyGrid.Strings.Draw(node, false, Editable, Read,
                text => Assign(NativeNumericText.Parse(text, type, range)), Acquire,
                text => StepPackageNumeric(type, item.DeclaredType, node.Value, range, ref text) ? text : null,
                raw => NativeNumericText.Format(raw, type, range),
                externalText: ranged && display != null && (active || complete) ? NativeNumericText.Format(display, type, range) : null,
                externalActive: active, externalCommit: complete);
        }
        return true;
    }
}
