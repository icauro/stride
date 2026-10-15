using System.Text.Json;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<(AssetId Asset, Guid Entity), EditorTreeRowState> captureHierarchyRows = [];

    private void RememberVisualCaptureHierarchyEntity(EntityHierarchyDocument owner, Guid entityId)
    {
        if (visualCapture == null) return;
        captureHierarchyRows[(owner.Asset.Id, entityId)] = EditorTreeStateAdapter.RememberCurrentRow("##Row");
    }

    private object ExpandVisualCaptureHierarchyEntity(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || parameters.EnumerateObject().Any(property => property.Name is not ("id" or "expanded" or "assetId"))
            || !parameters.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idValue.GetString(), out var id)
            || !parameters.TryGetProperty("expanded", out var expandedValue)
            || expandedValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("Hierarchy expansion requires an actual entity id and an expanded boolean.");
        var owner = document ?? throw new InvalidOperationException("No hierarchy document is loaded.");
        if (parameters.TryGetProperty("assetId", out var assetValue)
            && (assetValue.ValueKind != JsonValueKind.String || assetValue.GetString() != owner.Asset.Id.ToString()))
            throw new InvalidOperationException("The requested hierarchy asset is not active.");
        if (!owner.Asset.Hierarchy.Parts.ContainsKey(id)) throw new InvalidOperationException("The requested entity does not exist.");
        var expanded = expandedValue.GetBoolean();
        if (!captureHierarchyRows.TryGetValue((owner.Asset.Id, id), out var row)
            || !EditorTreeStateAdapter.TrySetExpanded(row, expanded, out var actual))
            throw new InvalidOperationException("The actual entity row is hidden or has not been presented; reveal its ancestors first.");
        return new { entityId = id.ToString(), assetId = owner.Asset.Id.ToString(), expanded = actual };
    }
}
