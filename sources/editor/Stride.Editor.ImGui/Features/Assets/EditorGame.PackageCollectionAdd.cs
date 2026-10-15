using Stride.Core.Assets;
using Stride.Core.Assets.Editor.Quantum.NodePresenters.Keys;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    // The stock Add button passes no parameter. AbstractNodeEntryData selects
    // the separate native factory control; an inferred type is not a parameter.
    private void DrawPackageCollectionDefaultAdd(Package package, PackageSettingAccessor accessor,
        IObjectNode node, SettingStep[] path, bool readOnly)
    {
        if (SessionAuthoringHistory is not { } history || node.Descriptor is not CollectionDescriptor descriptor
            || !descriptor.HasAdd || !AssetCollectionCommandRules.CanAdd(descriptor.ElementType)) return;
        var tree = packageSettingTrees[(package, accessor.Key)];
        var projection = tree.ActiveProjection;
        var presenter = SettingNodePresenter(tree, path);
        var owner = GetPackageSettingsDocument(package, history);
        var state = history.Registry.Find(owner.Path);
        var generation = state?.Generation;
        var revision = owner.AuthoringRevision;
        var session = workspace?.Session;
        bool Current() => !readOnly && PackageSettingsEditable(package) && owner.CanSave
            && state is { IsPublished: true, IsDisposed: false } && state.Generation == generation
            && ReferenceEquals(history.Registry.Find(owner.Path), state) && owner.AuthoringRevision == revision
            && ReferenceEquals(SessionAuthoringHistory, history) && ReferenceEquals(workspace?.Session, session)
            && session?.Packages.Contains(package) == true && package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
            && ReferenceEquals(packageSettingTrees.GetValueOrDefault((package, accessor.Key)), tree)
            && tree.Version == (history.Extensions?.Version ?? -1)
            && SameSettingValue(accessor, accessor.Get(package), tree.Source)
            && !PackagePresenterReadOnly(presenter)
            && presenter?.AttachedProperties.ContainsKey(AbstractNodeEntryData.Key) != true
            && !descriptor.IsReadOnly(node.Retrieve())
            && SettingCollectionMetadata(tree.Root!, path, node)?.ReadOnly != true;
        var command = presenter?.Commands.SingleOrDefault(candidate => candidate.Name == "AddNewItem");
        using var disabled = ImGuiScopes.Disabled(!Current() || command != null && !command.CanExecute([presenter!], null));
        if (!Gui.SmallButton("+")) return;
        Execute(() =>
        {
            if (!Current()) throw new InvalidOperationException("The collection changed or is read-only.");
            var native = presenter?.Commands.SingleOrDefault(candidate => candidate.Name == "AddNewItem");
            if (native != null)
            {
                QueuePackageSettingCommand(package, accessor, presenter!, native.Name, null);
                return;
            }
            ValidatePendingPropertyStrings();
            if (!Current()) throw new InvalidOperationException("The collection changed before validation.");
            var previousProjection = tree.ActiveProjection;
            IDisposable? pins = null;
            try
            {
                tree.ActiveProjection = projection;
                pins = AcquirePackagePropertyPins(package, accessor, presenter, Current);
                EditPackageSetting(package, accessor, path, current =>
                {
                    if (current.Descriptor is not CollectionDescriptor actual || actual.Type != descriptor.Type
                        || !actual.HasAdd || !AssetCollectionCommandRules.CanAdd(actual.ElementType)
                        || actual.IsReadOnly(current.Retrieve())
                        || SettingCollectionMetadata(tree.Root!, path, current)?.ReadOnly == true)
                        throw new InvalidOperationException("The collection structure is read-only.");
                    // Same no-parameter instantiation policy as AddNewItemCommand:
                    // real content references are left empty, never constructed inline.
                    var instance = ObjectFactoryRegistry.NewInstance(actual.ElementType);
                    var item = AssetCollectionCommandRules.IsReferenceType(actual.ElementType) ? null : instance;
                    if (!Current()) throw new InvalidOperationException("The collection changed during item construction.");
                    if (item == null && SettingCollectionMetadata(tree.Root!, path, current)?.NotNullItems == true)
                        throw new InvalidOperationException("This setting collection does not allow null items.");
                    current.Add(item!);
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
