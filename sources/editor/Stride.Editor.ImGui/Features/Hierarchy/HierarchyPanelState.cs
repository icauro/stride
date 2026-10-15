using Stride.Core.Assets;
using Stride.Engine;

namespace Stride.Editor.ImGui;

internal sealed class HierarchyPanelState
{
    internal bool Visible = true;
    internal readonly Dictionary<object, HierarchyDropRowGeometry> DropRows = [];
    internal string hierarchyFilter = "";
    internal string hierarchyTokenSource = "";
    internal string[] hierarchyFilterTokens = [];
    internal Guid? revealCreatedParent;
    internal Guid? scrollHierarchyEntity;
    internal readonly HashSet<Guid> hierarchyVisible = [];
    internal readonly List<Guid> hierarchyDisplayed = [];
    internal Guid? hierarchySelectionAnchor;
    internal EntityHierarchyDocument? hierarchySelectionDocument;
    internal (Entity Entity, bool Toggle, bool Range)? pendingHierarchySelection;
    internal (EntityHierarchyDocument Document, Entity Entity)? pendingHierarchyGroupClick;
    internal bool hierarchyGroupClickDragged;
    internal bool? hierarchyExpandAll;
    internal int hierarchyExpansionRevision;
    internal readonly Dictionary<uint, int> hierarchyExpansionApplied = [];
    internal EditorTreeView hierarchyTree = new();
    internal readonly HashSet<(AssetId Asset, Guid Entity)> lockedHierarchyEntities = [];
    internal System.Numerics.Vector2 hierarchyRowMinimum, hierarchyRowMaximum;
}
