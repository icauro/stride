using System.ComponentModel;
using System.Runtime.CompilerServices;
using Stride.Core.Annotations;
using Stride.Core.Assets;
using Stride.Core.Extensions;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private sealed class PackageDictionaryAddView
    {
        internal string Text = "";
        internal uint InputId;
        internal bool CloseRequested;
    }
    private readonly ConditionalWeakTable<PackageSettingsDocument, Dictionary<uint, PackageDictionaryAddView>> packageDictionaryAddViews = new();

    private void DrawPackageDictionaryAdd(Package package, PackageSettingAccessor accessor, IObjectNode node,
        SettingStep[] path, bool readOnly, MemberCollectionAttribute? metadata)
    {
        if (node.Descriptor is not DictionaryDescriptor descriptor || metadata?.ReadOnly == true
            || !AssetCollectionCommandRules.CanConstruct(descriptor.KeyType)
            || !AssetCollectionCommandRules.CanAdd(descriptor.ValueType) || SessionAuthoringHistory is not { } history) return;
        var owner = GetPackageSettingsDocument(package, history);
        var state = history.Registry.Find(owner.Path);
        var revision = owner.AuthoringRevision;
        var generation = state?.Generation;
        var tree = packageSettingTrees[(package, accessor.Key)];
        var session = workspace?.Session;
        var presenter = SettingNodePresenter(tree, path);
        var version = history.Extensions?.Version;
        bool Current() => !readOnly && PackageSettingsEditable(package) && !PackagePresenterReadOnly(presenter)
            && owner.CanSave && owner.AuthoringRevision == revision && state is { IsPublished: true, IsDisposed: false }
            && state.Generation == generation && ReferenceEquals(history.Registry.Find(owner.Path), state)
            && ReferenceEquals(SessionAuthoringHistory, history) && ReferenceEquals(workspace?.Session, session)
            && session?.Packages.Contains(package) == true && history.Extensions?.Version == version
            && package.UserSettings.Profile.CanReadRegisteredValue(accessor.Key)
            && ReferenceEquals(packageSettingTrees.GetValueOrDefault((package, accessor.Key)), tree)
            && SameSettingValue(accessor, accessor.Get(package), tree.Source);
        var views = packageDictionaryAddViews.GetValue(owner, _ => new());
        var viewId = Gui.GetID("##AddDictionaryKey");
        if (!views.TryGetValue(viewId, out var view)) views[viewId] = view = new();
        NodeIndex Key(DictionaryDescriptor actual, object? parameter)
        {
            var converter = TypeDescriptor.GetConverter(actual.KeyType);
            return parameter != null && converter.CanConvertFrom(parameter.GetType())
                ? new NodeIndex(converter.ConvertFrom(parameter))
                : actual.KeyType.IsEnum ? new NodeIndex(parameter!) : new NodeIndex(Activator.CreateInstance(actual.KeyType));
        }
        void Add(object? parameter, uint feedback)
        {
            if (!Current()) throw new InvalidOperationException("The dictionary changed or is read-only.");
            var command = presenter?.Commands.SingleOrDefault(candidate => candidate.Name == "AddPrimitiveKey");
            if (command != null)
            {
                if (!command.CanExecute([presenter!], parameter)) throw new InvalidOperationException("The dictionary command cannot add this key.");
                QueuePackageSettingCommand(package, accessor, presenter!, command.Name, parameter);
                view.CloseRequested = true;
                return;
            }
            var index = PropertyConversionFeedback.Convert(feedback, () => Key(descriptor, parameter));
            if (node.Indices?.Contains(index) == true) return;
            ValidatePendingPropertyStrings();
            if (!Current()) throw new InvalidOperationException("The dictionary changed before validation.");
            EditPackageSetting(package, accessor, path, current =>
            {
                if (current.Descriptor is not DictionaryDescriptor actual || actual.IsReadOnly(current.Retrieve())
                    || SettingCollectionMetadata(tree.Root!, path, current)?.ReadOnly == true
                    || !AssetCollectionCommandRules.CanConstruct(actual.KeyType) || !AssetCollectionCommandRules.CanAdd(actual.ValueType))
                    throw new InvalidOperationException("The dictionary structure is read-only.");
                var key = Key(actual, parameter);
                if (current.Indices!.Contains(key)) return;
                var item = AssetCollectionCommandRules.CreateDictionaryItem(actual.ValueType);
                if (item == null && metadata?.NotNullItems == true) throw new InvalidOperationException("This setting collection does not allow null items.");
                current.Add(item!, key);
            }, structural: true);
            CommitPackageSettingEdits(package, accessor, retainPresentation: true);
            view.CloseRequested = true;
        }
        var popup = descriptor.KeyType == typeof(string) || descriptor.KeyType.IsNumeric() || descriptor.KeyType.IsEnum;
        using var disabled = ImGuiScopes.Disabled(!Current());
        if (Gui.SmallButton("+")) Execute(() =>
        {
            ValidatePendingPropertyStrings();
            if (!Current()) throw new InvalidOperationException("The dictionary changed before opening.");
            if (!popup) { Add(null, viewId); return; }
            view.Text = descriptor.KeyType == typeof(string) ? EditorLocalization.Text("New key") : "0";
            view.CloseRequested = false;
            Gui.OpenPopup("##DictionaryKey");
        });
        if (Gui.BeginPopup("##DictionaryKey"))
        {
            try
            {
                Gui.TextUnformatted(EditorLocalization.Text("Key name:"));
                if (descriptor.KeyType.IsEnum)
                {
                    var options = descriptor.KeyType.IsDefined(typeof(FlagsAttribute), false)
                        ? EditorFlagEnums.GetIndividualFlags(descriptor.KeyType).Cast<Enum>().ToArray()
                        : Enum.GetValues(descriptor.KeyType).Cast<Enum>().Distinct().ToArray();
                    for (var index = 0; index < options.Length; index++)
                    {
                        var option = options[index];
                        Gui.PushID(index);
                        try
                        {
                            var label = PackageEnumDisplay(option);
                            var feedback = Gui.GetID(label.Length == 0 ? "##KeyChoice" : label);
                            if (DrawNativeSelectable(label.Length == 0 ? "##KeyChoice" : label))
                                Execute(() => { Add(option, feedback); view.CloseRequested = true; });
                        }
                        finally { Gui.PopID(); }
                    }
                }
                else
                {
                    view.InputId = Gui.GetID("##NewDictionaryKey");
                    var feedback = view.InputId;
                    DrawPackagePropertyString(package, accessor, "##NewDictionaryKey", presenter, Current(),
                        () => view.Text, text => Add(text, feedback), commitSetting: false, cancelOnLostFocus: true);
                    PropertyConversionFeedback.Draw(feedback);
                }
                if (view.CloseRequested) Gui.CloseCurrentPopup();
            }
            finally { Gui.EndPopup(); }
        }
        else if (view.InputId != 0)
        {
            packagePropertyStrings.Abandon(owner, view.InputId);
            view.InputId = 0;
        }
    }
}
