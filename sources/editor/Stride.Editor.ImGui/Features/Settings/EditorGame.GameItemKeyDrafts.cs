using System.ComponentModel;
using Stride.Core.Reflection;
using Stride.Core.Presentation.Quantum.Presenters;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool DrawGameItemKeyDraft(AssetMetadataDocument owner, GameSettingItem item)
    {
        var target = owner.AcquireGameItemCommandTarget(item);
        if (target == null) { Gui.TextUnformatted(owner.PresentedGameSettingItemLabel(item)); return owner.CanCopyGameSettingItem(item); }
        using var held = new RetainedPathLease(this, target);
        var node = target.Presenter;
        var command = NodePresenterCombination.Commands(target.Presenters).SingleOrDefault(candidate => candidate.Name == "RenameStringKey");
        if (command == null) { Gui.TextUnformatted(node.DisplayName); return owner.CanCopyGameSettingItem(item); }
        var version = owner.SessionHistory?.Extensions?.Version;
        bool Current() => IsCurrentMetadataControlOwner(owner) && owner.SessionHistory?.Extensions?.Version == version
            && owner.IsCurrentGameItemValue(item, node);
        bool Editable() => !IsAuthoringPipelineUpdating && Current() && owner.CanEditPresentedGameSettingItem(item)
            && owner.CanEditGameSettingKeys(item.Context)
            && command.CanExecute(target.Presenters, node.DisplayName);
        object?[] Target() => [item.Index.Value, item.Value, item.Collection, item.Id, item.Index, node,
            item.Context, item.Field.Node, item.Field.Node.Parent.Retrieve(), item.Context.Parent?.Value, owner.Path,
            sessionHistory?.Registry.Find(owner.AuthoredAsset.Id)?.Generation, owner.SessionHistory?.Extensions?.Version];
        object?[] Read() => [.. Target(), owner.EditRevision];
        IDisposable Acquire()
        {
            var leases = new List<IDisposable>();
            try
            {
                if (!Current()) throw new InvalidOperationException("The settings dictionary item changed.");
                leases.Add(AcquirePropertyStringOwner(owner));
                leases.Add(owner.AcquireGameItemCommandTarget(item)
                    ?? throw new InvalidOperationException("The settings dictionary target changed."));
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
        PropertyGrid.Strings.Draw(node, false, Editable, Read, text =>
        {
            if (!Editable() || !command.CanExecute(target.Presenters, text))
                throw new InvalidOperationException("The settings dictionary key cannot be renamed.");
            var dictionary = (DictionaryDescriptor)item.Context.CollectionDescriptor;
            // Original GenerateGenericKey uses ConvertFrom(object), hence the native culture,
            // and ignores occupied keys rather than replacing their values.
            var key = System.ComponentModel.TypeDescriptor.GetConverter(dictionary.KeyType).ConvertFrom(text);
            if (dictionary.ContainsKey(item.Collection.Retrieve(), key)) return;
            var before = Target(); ValidatePendingPropertyStrings();
            if (!Editable() || !before.SequenceEqual(Target()))
                throw new InvalidOperationException("The settings dictionary key changed during pending validation.");
            var actorTarget = owner.AcquireGameItemCommandTarget(item)
                ?? throw new InvalidOperationException("The settings dictionary command target changed.");
            var transferred = false;
            try
            {
                if (!NodePresenterCombination.Commands(actorTarget.Presenters).Contains(command)
                    || !command.CanExecute(actorTarget.Presenters, text))
                    throw new InvalidOperationException("The native dictionary rename command changed.");
                transferred = true; StartEditorExtensionCommand(owner, actorTarget, command, text);
            }
            finally { if (!transferred) ReleaseHierarchyFactoryTarget(actorTarget); }
        }, Acquire, format: _ => node.DisplayName, controlLabel: "##Key", refreshAfterCommit: false, cancelOnLostFocus: true);
        // A synchronous native rename can remove this exact indexed target before the
        // command actor publishes its journal. Do not render its stale value afterward.
        return Current();
    }
}
