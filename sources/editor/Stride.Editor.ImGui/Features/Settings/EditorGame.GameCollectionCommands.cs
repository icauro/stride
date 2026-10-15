using Stride.Core.Presentation.Quantum.Presenters;
using Stride.Core.Reflection;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void DrawGameCollectionAdd(AssetMetadataDocument owner, GameSettingCollection field)
    {
        var target = owner.AcquireGameCollectionCommandTarget(field);
        if (target == null) return;
        using var held = new RetainedPathLease(this, target);
        var node = target.Presenter;
        var dictionary = field.CollectionDescriptor as DictionaryDescriptor;
        var name = dictionary != null ? "AddPrimitiveKey" : "AddNewItem";
        var command = NodePresenterCombination.Commands(target.Presenters).SingleOrDefault(candidate => candidate.Name == name);
        if (command == null) return;
        var version = owner.SessionHistory?.Extensions?.Version;
        bool Current() => IsCurrentMetadataControlOwner(owner) && owner.SessionHistory?.Extensions?.Version == version
            && owner.IsCurrentGameCollectionTarget(field, node);
        bool Editable() => !IsAuthoringPipelineUpdating && Current() && owner.CanAddGameSettingCollection(field)
            && !target.Presenters.Any(presenter => presenter.IsReadOnly);
        object?[] Target() => [field.Node.Retrieve(), field.Node, field.Origin.Node, field.Origin.Key,
            field.Origin.Node.Parent.Retrieve(), field.Parent?.Value, owner.Path,
            sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation, owner.SessionHistory?.Extensions?.Version];
        var textKey = dictionary != null && (dictionary.KeyType == typeof(string) || AssetSettingNumericMetadata.Supports(dictionary.KeyType)
            || dictionary.KeyType == typeof(nint) || dictionary.KeyType == typeof(nuint));
        var initialKey = dictionary?.KeyType == typeof(string) ? EditorLocalization.Text("New key") : "0";
        object?[] Read() => [initialKey, .. Target(), owner.EditRevision];
        IDisposable Acquire()
        {
            var leases = new List<IDisposable>();
            try
            {
                if (!Current()) throw new InvalidOperationException("The settings collection changed.");
                leases.Add(AcquirePropertyStringOwner(owner));
                leases.Add(owner.AcquireGameCollectionCommandTarget(field)
                    ?? throw new InvalidOperationException("The settings collection target changed."));
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
        void Start(object? parameter)
        {
            if (!Editable() || !command.CanExecute(target.Presenters, parameter))
                throw new InvalidOperationException("The native settings collection command is not available.");
            if (dictionary != null)
            {
                // Mirror AddPrimitiveKey's native converter/enum/default-key branch.
                var converter = System.ComponentModel.TypeDescriptor.GetConverter(dictionary.KeyType);
                var key = parameter != null && converter.CanConvertFrom(parameter.GetType()) ? converter.ConvertFrom(parameter)
                    : dictionary.KeyType.IsEnum ? parameter : Activator.CreateInstance(dictionary.KeyType);
                if (dictionary.ContainsKey(field.Node.Retrieve(), key)) return;
            }
            var before = Target(); ValidatePendingPropertyStrings();
            if (!Editable() || !before.SequenceEqual(Target())) throw new InvalidOperationException("The settings collection changed during pending validation.");
            var actorTarget = owner.AcquireGameCollectionCommandTarget(field)
                ?? throw new InvalidOperationException("The settings collection command target changed.");
            var transferred = false;
            try
            {
                if (!NodePresenterCombination.Commands(actorTarget.Presenters).Contains(command)
                    || !command.CanExecute(actorTarget.Presenters, parameter)) throw new InvalidOperationException("The native collection command changed.");
                transferred = true; StartEditorExtensionCommand(owner, actorTarget, command, parameter);
            }
            finally { if (!transferred) ReleaseHierarchyFactoryTarget(actorTarget); }
        }
        var supplied = dictionary == null ? owner.ExtensionCollectionAbstractEntries(field.Node) : null;
        if (dictionary == null && supplied == null)
        {
            using var disabled = ImGuiScopes.Disabled(!Editable() || !command.CanExecute(target.Presenters, null));
            if (Gui.Button("Add")) pendingGameSettingCollectionAction = () => Start(null);
            return;
        }
        using (ImGuiScopes.Disabled(!Editable())) if (Gui.Button("Add")) Gui.OpenPopup("AddItem");
        if (!Gui.BeginPopup("AddItem")) return;
        try
        {
            if (dictionary?.KeyType.IsEnum == true)
            {
                foreach (var key in Enum.GetValues(dictionary.KeyType).Cast<Enum>().Distinct())
                {
                    if (dictionary.ContainsKey(field.Node.Retrieve(), key)) continue;
                    using var disabled = ImGuiScopes.Disabled(!Editable() || !command.CanExecute(target.Presenters, key));
                    if (DrawNativeMenuItem(PackageEnumDisplay(key))) pendingGameSettingCollectionAction = () => Start(key);
                }
            }
            else if (textKey)
            {
                Gui.TextUnformatted(EditorLocalization.Text("Key name:")); Gui.SameLine();
                PropertyGrid.Strings.Draw(node, false, Editable, Read, Start, Acquire,
                    format: _ => initialKey, controlLabel: "##NewKey", refreshAfterCommit: false, cancelOnLostFocus: true,
                    input: DrawKeyInput);
            }
            else if (dictionary != null)
            {
                using var disabled = ImGuiScopes.Disabled(!Editable() || !command.CanExecute(target.Presenters, null));
                if (Gui.Button("Add")) pendingGameSettingCollectionAction = () => Start(null);
            }
            else
            {
                using var disabled = ImGuiScopes.Disabled(!Editable());
                NativeAbstractEntryControl.Choices(owner.GameCollectionEntries(field), null,
                    entry => pendingGameSettingCollectionAction = () =>
                    {
                        if (!NativeAbstractEntries.Contains(owner.GameCollectionEntries(field), entry))
                            throw new InvalidOperationException("The native collection entry changed.");
                        Start(entry);
                    }, entry => Editable() && command.CanExecute(target.Presenters, entry));
            }
        }
        finally { Gui.EndPopup(); }
        bool DrawKeyInput(string control, string hint, ref string text, ImGuiInputTextFlags flags)
        {
            var submitted = NativeStringInput.DrawWithHint(control, hint, ref text, flags);
            // Native ValidateCommand accepts Enter even when its initial parameter
            // text was not changed; the retained controller must observe that submit.
            if (submitted && (flags & ImGuiInputTextFlags.ReadOnly) == 0) ImGuiP.MarkItemEdited(Gui.GetID(control));
            return submitted;
        }
    }

    private bool CanExecuteGameItemRemove(AssetMetadataDocument owner, GameSettingItem item)
    {
        if (IsAuthoringPipelineUpdating || !IsCurrentMetadataControlOwner(owner) || !owner.CanRemoveGameSettingItem(item)) return false;
        var target = owner.AcquireGameItemCommandTarget(item);
        if (target == null) return false;
        using var held = new RetainedPathLease(this, target);
        return NodePresenterCombination.Commands(target.Presenters).Any(command => command.Name == "RemoveItem" && command.CanExecute(target.Presenters, null));
    }
    private void RemoveGameItemThroughCommand(AssetMetadataDocument owner, GameSettingItem item)
    {
        if (!CanExecuteGameItemRemove(owner, item)) throw new InvalidOperationException("The settings item cannot be removed.");
        var target = owner.AcquireGameItemCommandTarget(item) ?? throw new InvalidOperationException("The settings item changed.");
        using var held = new RetainedPathLease(this, target);
        var command = NodePresenterCombination.Commands(target.Presenters).Single(candidate => candidate.Name == "RemoveItem");
        StartGameItemCommand(owner, item, item.Value, command);
    }
}
