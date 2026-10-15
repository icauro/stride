using Stride.Core.Assets;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawPackageCollectionRemove(Package package, PackageSettingAccessor accessor,
        IObjectNode node, SettingStep[] path, NodeIndex index, INodePresenter? presenter, bool readOnly)
    {
        if (SessionAuthoringHistory is not { } history) return;
        var tree = packageSettingTrees[(package, accessor.Key)];
        var projection = tree.ActiveProjection;
        var owner = GetPackageSettingsDocument(package, history);
        var state = history.Registry.Find(owner.Path);
        var generation = state?.Generation;
        var revision = owner.AuthoringRevision;
        var session = workspace?.Session;
        var indices = node.Indices!.ToArray();
        var item = node.Retrieve(index);
        var element = node.Descriptor is DictionaryDescriptor dictionary ? dictionary.ValueType
            : ((CollectionDescriptor)node.Descriptor).ElementType;
        bool Eligible(IObjectNode current) => current.Descriptor.Type == node.Descriptor.Type
            && SettingCollectionMetadata(tree.Root!, path, current)?.ReadOnly != true
            && (current.Descriptor is DictionaryDescriptor d && !d.IsReadOnly(current.Retrieve())
                || current.Descriptor is CollectionDescriptor c && !c.IsReadOnly(current.Retrieve())
                    && (c.HasRemoveAt || c.HasRemove) && AssetCollectionCommandRules.CanAdd(c.ElementType));
        bool Current() => !readOnly && PackageSettingsEditable(package) && owner.CanSave
            && state is { IsPublished: true, IsDisposed: false } && state.Generation == generation
            && ReferenceEquals(history.Registry.Find(owner.Path), state) && owner.AuthoringRevision == revision
            && ReferenceEquals(SessionAuthoringHistory, history) && ReferenceEquals(workspace?.Session, session)
            && session?.Packages.Contains(package) == true && package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
            && ReferenceEquals(packageSettingTrees.GetValueOrDefault((package, accessor.Key)), tree)
            && tree.Version == (history.Extensions?.Version ?? -1)
            && SameSettingValue(accessor, accessor.Get(package), tree.Source)
            && !PackagePresenterReadOnly(presenter) && Eligible(node)
            && node.Indices?.SequenceEqual(indices) == true && node.Indices.Contains(index)
            && (ReferenceEquals(node.Retrieve(index), item) || (element.IsValueType || element == typeof(string)) && Equals(node.Retrieve(index), item));
        var command = presenter?.Commands.SingleOrDefault(candidate => candidate.Name == "RemoveItem");
        Gui.SameLine();
        using var disabled = ImGuiScopes.Disabled(!Current() || command != null && !command.CanExecute([presenter!], null));
        if (!Gui.SmallButton("-")) return;
        Execute(() =>
        {
            if (!Current()) throw new InvalidOperationException("The collection entry changed or is read-only.");
            var native = presenter?.Commands.SingleOrDefault(candidate => candidate.Name == "RemoveItem");
            if (native != null)
            {
                QueuePackageSettingCommand(package, accessor, presenter!, native.Name, null);
                return;
            }
            ValidatePendingPropertyStrings();
            if (!Current()) throw new InvalidOperationException("The collection entry changed before validation.");
            var previousProjection = tree.ActiveProjection;
            IDisposable? pins = null;
            try
            {
                tree.ActiveProjection = projection;
                pins = AcquirePackagePropertyPins(package, accessor, presenter, Current);
                EditPackageSetting(package, accessor, path, current =>
                {
                    if (!Current() || !Eligible(current) || current.Indices?.SequenceEqual(indices) != true
                        || current.Indices.Contains(index) != true)
                        throw new InvalidOperationException("The collection entry changed before removal.");
                    current.Remove(current.Retrieve(index)!, index);
                }, structural: true);
                CommitPackageSettingEdits(package, accessor, retainPresentation: true);
            }
            finally
            {
                tree.ActiveProjection = previousProjection;
                if (pins != null) { packageCommandCleanup.Add(pins.Dispose); PumpPackageCommandCleanup(); }
            }
        });
    }
}
