using Stride.Core.Assets;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal static partial class CoreCapabilityScenarios
{
    private static void CanonicalSessionHistory(string firstPath, string secondPath)
    {
        // A separate authored asset needs a distinct serialized asset identity.
        using (var identity = new SceneDocument(secondPath))
        {
            identity.Asset.Id = AssetId.New();
            identity.Save();
        }
        RunPrefabSession((registry, history) =>
        {
            var first = new SceneDocument(firstPath, history);
            var firstState = registry.Register(first, first.Asset.Id);
            using var firstView = firstState.AcquireView();
            var second = new SceneDocument(secondPath, history);
            var secondState = registry.Register(second, second.Asset.Id);
            using var secondView = secondState.AcquireView();
            var firstEntity = first.Asset.Hierarchy.RootParts.First();
            var secondEntity = second.Asset.Hierarchy.RootParts.First();
            var firstName = firstEntity.Name;
            var secondName = secondEntity.Name;
            first.Rename(firstEntity, firstName + " session"); first.Save();
            second.Rename(secondEntity, secondName + " session");
            Require(!first.IsDirty && second.IsDirty, "Canonical owners must track their own saved/dirty revisions");
            first.Undo();
            Require(!first.IsDirty && !second.IsDirty && secondEntity.Name == secondName
                && firstEntity.Name == firstName + " session", "Global undo must roll latest participant without changing another owner's saved data");
            second.Redo();
            Require(!first.IsDirty && second.IsDirty && secondEntity.Name == secondName + " session",
                "Global redo must restore exact owning document mutation");
            second.Undo();
            first.Rename(firstEntity, firstName + " replacement");
            Require(first.IsDirty && !second.IsDirty && !history.CanRedo, "Cross-document branch must discard redo without dirtying uninvolved owner");
            var light = first.Asset.Hierarchy.Parts.Values.Single(part => part.Entity.Name == "Directional light").Entity.Get<LightComponent>();
            first.Selected = light.Entity;
            var field = first.GetComponentFields(light).Single(value => value.Name == "Intensity");
            var original = field.Node.Retrieve();
            var position = history.History.Position;
            first.UpdateComponent(field, 67f); first.CancelComponentEdit(field);
            Require(Equals(field.Node.Retrieve(), original) && history.History.Position == position && first.IsDirty && !second.IsDirty,
                "Canceled canonical edit must preserve shared stack boundary and each owner's dirty state");
            first.Save(); second.Save();
            using var firstReload = new SceneDocument(firstPath);
            using var secondReload = new SceneDocument(secondPath);
            Require(firstReload.Asset.Id != secondReload.Asset.Id
                && firstReload.Asset.Hierarchy.Parts[firstEntity.Id].Entity.Name == firstName + " replacement"
                && secondReload.Asset.Hierarchy.Parts[secondEntity.Id].Entity.Name == secondName,
                "Independent reload must preserve distinct authored IDs and per-owner saved result");
        });
    }
}
