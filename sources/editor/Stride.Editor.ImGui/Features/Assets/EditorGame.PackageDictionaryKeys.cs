using System.ComponentModel;
using Stride.Core.Assets;
using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool DrawPackageDictionaryKey(Package package, PackageSettingAccessor accessor,
        IObjectNode node, SettingStep[] path, NodeIndex index, INodePresenter? itemPresenter, bool editable)
    {
        if (node.Descriptor is not DictionaryDescriptor dictionary) return false;
        var converter = TypeDescriptor.GetConverter(dictionary.KeyType);
        if (dictionary.KeyType != typeof(string) && !converter.CanConvertFrom(typeof(string))) return false;
        var tree = packageSettingTrees[(package, accessor.Key)];
        var feedback = Gui.GetID("##DictionaryItemKey");
        DrawPackagePropertyString(package, accessor, "##DictionaryItemKey", itemPresenter, editable,
            () => index.Value, text =>
            {
                // Original AddPrimitiveKeyCommand.GenerateGenericKey uses this
                // converter (and its native culture), and ignores occupied keys.
                var key = PropertyConversionFeedback.Convert(feedback, () => converter.ConvertFrom(text));
                var nextIndex = new NodeIndex(key);
                if (node.Indices?.Contains(nextIndex) == true) return;
                var command = itemPresenter?.Commands.SingleOrDefault(candidate => candidate.Name == "RenameStringKey");
                if (command != null)
                {
                    if (!command.CanExecute([itemPresenter!], text)) throw new InvalidOperationException("The dictionary key cannot be renamed.");
                    QueuePackageSettingCommand(package, accessor, itemPresenter!, command.Name, text);
                    return;
                }
                EditPackageSetting(package, accessor, path, current =>
                {
                    if (current.Descriptor is not DictionaryDescriptor currentDictionary
                        || currentDictionary.IsReadOnly(current.Retrieve())
                        || SettingCollectionMetadata(tree.Root!, path, current)?.ReadOnly == true)
                        throw new InvalidOperationException("The dictionary structure is read-only.");
                    if (current.Indices?.Contains(index) != true) throw new InvalidOperationException("The dictionary entry changed before renaming.");
                    var currentKey = TypeDescriptor.GetConverter(currentDictionary.KeyType).ConvertFrom(text);
                    var destination = new NodeIndex(currentKey);
                    if (current.Indices.Contains(destination)) return;
                    var value = current.Retrieve(index);
                    current.Remove(value!, index);
                    current.Add(value!, destination);
                }, structural: true);
                CommitPackageSettingEdits(package, accessor, retainPresentation: true);
            }, format: value => itemPresenter?.DisplayName ?? Convert.ToString(value, System.Globalization.CultureInfo.CurrentCulture) ?? "",
            commitSetting: false, readValues: () => [index.Value, node.Retrieve(index)], cancelOnLostFocus: true);
        PropertyConversionFeedback.Draw(feedback);
        return true;
    }
}
