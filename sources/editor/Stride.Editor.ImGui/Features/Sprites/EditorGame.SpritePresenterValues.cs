using Hexa.NET.ImGui;
using Stride.Core.IO;
using Stride.Core.Reflection;
using Stride.Core.Mathematics;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private static bool SpritePresenterAllowsNull(INodePresenter presenter)
    {
        var member = presenter as MemberNodePresenter ?? (presenter as ItemNodePresenter)?.Parent as MemberNodePresenter;
        if (member == null) return true;
        if (presenter is ItemNodePresenter)
        {
            var collection = member.MemberAttributes.OfType<Stride.Core.Annotations.MemberCollectionAttribute>().FirstOrDefault()
                ?? member.Descriptor.Attributes.OfType<Stride.Core.Annotations.MemberCollectionAttribute>().FirstOrDefault();
            return collection?.NotNullItems != true;
        }
        return !member.MemberAttributes.OfType<Stride.Core.Annotations.NotNullAttribute>().Any();
    }

    private IDisposable AcquireSpritePresenterGroup(AssetMetadataDocument owner, IReadOnlyList<INodePresenter> nodes)
    {
        var leases = new List<IDisposable>();
        try
        {
            if (!owner.IsCurrentSpritePresenterGroup(nodes)) throw new InvalidOperationException("The sprite presenter group changed.");
            leases.Add(AcquirePropertyStringOwner(owner));
            foreach (var node in nodes) leases.Add(owner.AcquireSpritePresenterTarget(node));
            return new PropertyStringOwners(leases, [owner]);
        }
        catch (Exception failure)
        {
            var cleanup = new PropertyStringOwners(leases, [owner]);
            try { cleanup.Dispose(); } catch (Exception release) { packageCommandCleanup.Add(cleanup.Dispose); throw new AggregateException(failure, release); }
            throw;
        }
    }
    private void ApplySpritePresenterGroup(AssetMetadataDocument owner, IReadOnlyList<INodePresenter> nodes, Func<int, object?, object?> transform)
    {
        if (!owner.IsCurrentSpritePresenterGroup(nodes) || nodes.Any(node => !owner.CanEditSpritePresenter(node)))
            throw new InvalidOperationException("The sprite property is no longer editable.");
        using var lease = new RetainedPathLease(this, AcquireSpritePresenterGroup(owner, nodes));
        var fields = nodes.Select(owner.SpritePresenterSourceField).ToArray();
        if (fields.Any(field => field != null))
        {
            if (fields.Any(field => field == null) || nodes.Any(node => node.Type != typeof(UFile)))
                throw new InvalidOperationException("This source projection requires a native typed source adapter.");
            var sources = nodes.Select((node, index) => transform(index, owner.ReadSpritePresenter(node))).ToArray();
            if (sources.Any(source => source != null && source is not UFile)) throw new ArgumentException("Invalid projected sprite source.");
            QueueSpriteSourceEdit(owner, fields.Select(field => field!).ToArray(), sources.Cast<UFile?>().ToArray(), projections: nodes);
        }
        else owner.TransformSpritePresenterGroup(nodes, transform);
    }
    private void ValidateSpritePresenterAction(AssetMetadataDocument owner, IReadOnlyList<INodePresenter> nodes, ref object?[] expected)
    {
        if (!IsCurrentMetadataControlOwner(owner) || !owner.IsCurrentSpritePresenterGroup(nodes)
            || !expected.SequenceEqual(owner.CaptureSpritePresenterGroup(nodes))) throw new InvalidOperationException("The sprite property context changed.");
        object?[] Target() => [.. nodes.Select(owner.ReadSpritePresenter), .. nodes.Select(node => (object?)node),
            .. nodes.Select(node => (object?)node.GetNodeAccessor().Node), .. nodes.Select(node => (object?)node.GetNodeAccessor().Index),
            .. nodes.Select(node => owner.ReadSpritePresenter(node) is DateTime date ? (object?)date.Kind : null),
            .. owner.SelectedSpriteIds.Cast<object?>(), owner.Path, sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation, sessionHistory?.Extensions?.Version];
        var target = Target();
        ValidatePendingPropertyStrings();
        if (!IsCurrentMetadataControlOwner(owner) || !owner.IsCurrentSpritePresenterGroup(nodes) || !target.SequenceEqual(Target()))
            throw new InvalidOperationException("The sprite property changed during pending validation.");
        // Only the authorized pending-draft commit may advance the surrounding owner revision.
        expected = owner.CaptureSpritePresenterGroup(nodes);
    }
    private void DrawSpritePresenterValues(AssetMetadataDocument owner, IReadOnlyList<INodePresenter> nodes)
    {
        var first = nodes[0]; var type = Nullable.GetUnderlyingType(first.Type) ?? first.Type;
        if (!BeginInspectorPropertyRow(first.DisplayName)) return;
        try
        {
            object?[] Read() => owner.CaptureSpritePresenterGroup(nodes);
            var expected = Read();
            bool Current() => IsCurrentMetadataControlOwner(owner) && owner.IsCurrentSpritePresenterGroup(nodes)
                && expected.SequenceEqual(Read());
            bool Editable() => !IsAuthoringPipelineUpdating && Current() && nodes.All(owner.CanEditSpritePresenter);
            IDisposable Acquire() => AcquireSpritePresenterGroup(owner, nodes);
            void Apply(Func<int, object?, object?> transform)
            {
                if (!Editable()) throw new InvalidOperationException("The sprite property source changed.");
                ApplySpritePresenterGroup(owner, nodes, transform); expected = Read();
            }
            void CommitDiscrete(object? replacement)
            {
                if (!Editable()) throw new InvalidOperationException("The sprite property source changed.");
                ValidateSpritePresenterAction(owner, nodes, ref expected);
                if (!Editable()) throw new InvalidOperationException("The sprite property source changed.");
                Apply((_, _) => replacement);
            }
            void Discrete(object? replacement) => Execute(() => CommitDiscrete(replacement));
            var values = expected.Take(nodes.Count).ToArray(); var mixed = values.Skip(1).Any(other => !Equals(other, values[0]));
            var value = values[0];
            if (values.Any(raw => raw is Stride.Core.Yaml.IUnloadable))
            {
                // Native unknown headers override typed value templates without
                // replacing the containing member/item or its command context.
                for (var index = 0; index < values.Length; index++)
                    if (values[index] is Stride.Core.Yaml.IUnloadable unknown)
                    {
                        Gui.PushID(index);
                        try { DrawUnloadableProperty(unknown); } finally { Gui.PopID(); }
                    }
                if (Gui.BeginPopupContextItem("Property actions"))
                { try { DrawSpritePresenterCommands(owner, nodes); } finally { Gui.EndPopup(); } }
                return;
            }
            var underlying = Nullable.GetUnderlyingType(first.Type);
            var nullablePresence = underlying?.IsStruct() == true && owner.SessionHistory?.Extensions?.HasPrimitiveEditor(first.Type) != true;
            using var nullable = NativeNullableRow.Begin("##Nullable", nullablePresence);
            if (nullablePresence)
            {
                var present = value != null || mixed;
                var supplied = SpritePresenterEntries(nodes);
                var presenceEntry = supplied == null ? present
                    ? Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeValue.Null
                    : (Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry)new Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeType(type)
                    : NativeAbstractEntries.TypeEntry(supplied, present ? null : type);
                var presenceCommand = NodePresenterCombination.Commands(nodes).FirstOrDefault(command => command.Name == "CreateNewInstance");
                var canClear = !present || nodes.All(SpritePresenterAllowsNull);
                using (ImGuiScopes.Disabled(!Editable() || !canClear || presenceEntry == null || presenceCommand != null && !presenceCommand.CanExecute(nodes, presenceEntry)))
                    if (DrawNativeNullablePresence(present, out _) && presenceEntry != null) SelectSpritePresenterEntry(owner, nodes, presenceEntry);
                nullable.ValueColumn();
                if (value == null && !mixed) { Gui.TextUnformatted(EditorLocalization.Text("(null)")); return; }
            }
            if (owner.SessionHistory?.Extensions?.HasPrimitiveEditor(first.Type) == true)
            {
                using var lease = new RetainedPathLease(this, Acquire()); var rendering = true;
                try { owner.SessionHistory.Extensions.TryDrawPrimitive(new(first.Type, mixed ? null : value, first.DisplayName, Editable(),
                    replacement => { if (!rendering) throw new InvalidOperationException("The sprite control context expired."); CommitDiscrete(replacement); }, () => { })); }
                finally { rendering = false; }
            }
            else if (ContentAssetReference.Supports(first.Type))
            {
                var selection = owner.SelectedSpriteIds.ToArray(); var version = sessionHistory?.Extensions?.Version;
                var state = sessionHistory?.Registry.Find(owner.AuthoredAsset.Id); var generation = state?.Generation;
                bool ResourceCurrent() => IsCurrentMetadataControlOwner(owner) && owner.IsCurrentSpritePresenterGroup(nodes)
                    && selection.SequenceEqual(owner.SelectedSpriteIds) && sessionHistory?.Extensions?.Version == version
                    && state is { IsPublished: true, IsDisposed: false } && state.Generation == generation
                    && ReferenceEquals(sessionHistory?.Registry.Find(owner.AuthoredAsset.Id), state) && nodes.All(owner.CanEditSpritePresenter);
                using var resources = BeginContentReferenceResources(ResourceCurrent, Acquire, () =>
                {
                    if (!ResourceCurrent()) throw new InvalidOperationException("The sprite reference presenter context changed.");
                    expected = Read();
                });
                DrawContentReferenceValue(first.Type, value, nodes.All(SpritePresenterAllowsNull),
                    replacement => CommitDiscrete(replacement), mixed: mixed, canEdit: Editable());
            }
            else if (type == typeof(string))
                PropertyGrid.Strings.Draw(first, mixed, Editable, Read, text => Apply((_, _) => text), Acquire);
            else if (IsPathType(type))
            {
                var sourceFields = nodes.Select(owner.SpritePresenterSourceField).ToArray();
                var sources = sourceFields.All(field => field != null) ? sourceFields.Select(field => field!).ToArray() : null;
                DrawRetainedPath(owner, first, type, Editable, Read, path => Apply((_, _) => path), nodes.Count, Acquire,
                    format: raw => sources != null && SpriteSourceRequest(owner, sources) is { } pending
                        ? pending.SourceFor(sources[0])?.ToString() ?? "" : raw?.ToString() ?? "");
            }
            else if (IsPackageColorType(type))
                DrawRetainedColor(first, type, Read, Editable, transform => Apply((_, old) => transform(old)), Acquire,
                    axis => nodes.All(node => !NativeNumericAxes.IsReadOnly(node, axis)), valueCount: nodes.Count);
            else if (type == typeof(Quaternion) || NativeNumericAxes.Names(type).Count > 0)
                DrawRetainedVirtualMath(first, type, nodes, Read, Editable,
                    axis => nodes.Select(node => owner.SpritePresenterAxisRanges(node).ElementAtOrDefault(axis)).ToArray(), Apply, Acquire);
            else if (IsTemporalType(type))
                DrawRetainedTemporal(first, type, Editable, Read, transform => Apply((_, old) => transform(old)), nodes.Count, Acquire);
            else if (type == typeof(char)) DrawRetainedCharacter(first, Read, Editable, replacement => Apply((_, _) => replacement), Acquire, nodes.Count);
            else if (type == typeof(Guid)) DrawRetainedGuid(first, Read, Editable, replacement => Apply((_, _) => replacement), Acquire, nodes.Count);
            else if (type == typeof(AngleSingle)) DrawRetainedAngle(first, Read, Editable, replacement => Apply((_, _) => replacement), Acquire, nodes.Count);
            else if (AssetSettingNumericMetadata.Supports(type))
            {
                var ranges = nodes.Select(owner.SpritePresenterNumericRange).ToArray(); var range = NativeNumericInput.Combine(type, ranges);
                var ranged = ranges.All(candidate => NativeRangedNumericControl.Matches(first.Type, candidate));
                object? display = value; var active = false; var complete = false;
                if (ranged)
                { using var disabled = ImGuiScopes.Disabled(!Editable()); complete = NativeRangedNumericControl.Draw(first, first.Type, value, mixed, ranges, UiScale(1), out display, out _); active = Gui.IsItemActive(); }
                PropertyGrid.Strings.Draw(first, mixed, Editable, Read, text => Apply((_, _) => NativeNumericText.Parse(text, type, range)), Acquire,
                    text => StepPackageNumeric(type, first.Type, owner.ReadSpritePresenter(first), range, ref text) ? text : null,
                    raw => NativeNumericText.Format(raw, type, range),
                    externalText: ranged && display != null && (active || complete) ? NativeNumericText.Format(display, type, range) : null,
                    externalActive: active, externalCommit: complete);
            }
            else
            {
                using var disabled = ImGuiScopes.Disabled(!Editable());
                if (type == typeof(bool))
                { var flag = mixed || value == null || value is true; Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, mixed || value == null); try { if (DrawNativeCheckbox("##Value", ref flag)) Discrete(flag); } finally { Gui.PopItemFlag(); } }
                else if (type.IsEnum && Nullable.GetUnderlyingType(first.Type) == null && icons?.HasEnumImages(type) == true)
                { if (NativeEnumImageControl.Draw(icons, first, type, value, mixed, Enum.GetValues(type).Cast<Enum>(), UiScale(1), out var replacement)) Discrete(replacement); }
                else if (type.IsEnum && Gui.BeginCombo("##Value", mixed ? DifferentPropertyValues : value?.ToString()))
                {
                    try
                    {
                        foreach (var name in Enum.GetNames(type))
                        {
                            var option = Enum.Parse(type, name);
                            if (!type.IsDefined(typeof(FlagsAttribute), false)) { if (Gui.Selectable(name, Equals(value, option))) Discrete(option); continue; }
                            var mask = EnumBits(option); var bits = EnumBits(value ?? Enum.ToObject(type, 0)); var selected = mask == 0 ? bits == 0 : (bits & mask) == mask;
                            var different = values.Any(raw => { var actual = EnumBits(raw ?? Enum.ToObject(type, 0)); return (mask == 0 ? actual == 0 : (actual & mask) == mask) != selected; });
                            Gui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, different);
                            try { if (DrawNativeCheckbox(name, ref selected)) Execute(() => { ValidateSpritePresenterAction(owner, nodes, ref expected); Apply((_, raw) => { var actual = EnumBits(raw ?? Enum.ToObject(type, 0)); return EnumFromBits(type, mask == 0 ? 0 : selected ? actual | mask : actual & ~mask); }); }); }
                            finally { Gui.PopItemFlag(); }
                        }
                        if (type.IsDefined(typeof(FlagsAttribute), false) && DrawFlagBulkActions() is { } action)
                            Execute(() => { ValidateSpritePresenterAction(owner, nodes, ref expected); Apply((_, raw) => ApplyFlagBulkAction((Enum)(raw ?? Enum.ToObject(type, 0)), action)); });
                    }
                    finally { Gui.EndCombo(); }
                }
                else DrawSelectableText("##Value", mixed ? DifferentPropertyValues : value?.ToString() ?? "");
            }
            if (Gui.BeginPopupContextItem("Property actions"))
            { try { DrawSpritePresenterCommands(owner, nodes); } finally { Gui.EndPopup(); } }
        }
        catch (Exception error) { statusMessage = error.Message; PropertyConversionFeedback.Fail(Gui.GetID("##Value"), error); PropertyConversionFeedback.Draw(Gui.GetID("##Value")); }
        finally { Gui.EndTable(); }
    }
}
