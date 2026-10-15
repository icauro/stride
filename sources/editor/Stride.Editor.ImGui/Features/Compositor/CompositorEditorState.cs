using Stride.Core.Reflection;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed class CompositorEditorState
{
    public readonly HashSet<Guid> SelectedBlocks = [];
    public readonly HashSet<CompositorSlot> SelectedLinks = [];
    public readonly Dictionary<CompositorSlot, CompositorLinkSelectionCertificate> SelectedLinkCertificates = [];
    public readonly HashSet<ItemId> SelectedItems = [];
    public readonly Dictionary<Guid, NVector2> BoundsMin = [], BoundsMax = [];
    public readonly Dictionary<CompositorSlot, NVector2> Pins = [];
    public readonly Dictionary<Guid, NVector2> Inputs = [];
    public readonly Dictionary<Guid, NVector2> DragStarts = [];
    public NVector2 Pan = new(30, 30), DragStart, PopupPosition;
    public float Zoom = 1;
    public float ListColumnFraction = .25f;
    public readonly float[] ListSectionHeights = [float.NaN, float.NaN, float.NaN];
    public readonly EditorSplitterState ColumnSplitter = new();
    public readonly EditorSplitterState[] RowSplitters = [new(), new()];
    public long Revision = -1;
    public Guid? Dragging;
    public CompositorBlock? PressedBlock;
    public NVector2 PressedGraphPosition;
    public NVector2? SelectionStart;
    public CompositorSlot? Linking;
    public CompositorOutputSlotCertificate? LinkingCertificate;
    private IObjectNodeSelection? propertySelection;
    // Registered collection chronology is independent of the unregistered link Inspector context.
    public IObjectNodeSelection? SelectionPrimary { get; private set; }
    public IObjectNodeSelection? PropertySelection
    {
        get => propertySelection;
        set { propertySelection = value; SelectionPrimary = value; }
    }
    public void ClearGraphInspectorSelection()
    {
        propertySelection = null;
        if (SelectionPrimary?.Item is not { } item || !SelectedItems.Contains(item)) SelectionPrimary = null;
    }
    public IReadOnlyList<Type> RendererTypes = [], FeatureTypes = [];
    public readonly Dictionary<string, IReadOnlyList<CompositorListItem>> ListRows = [];
    public readonly Dictionary<string, CompositorListKeyboardState> ListKeyboard = [];
    public int AssemblyVersion = -1;
    public CompositorListItem? ListDrag;
    public ItemId? EditingListItem, ListSelectionAnchor;
    public string EditingListCollection = "", EditingListOriginal = "", AnchorCollection = "";
    public bool FocusListName;
    public CompositorProperty? PropertyDrag;
    public Guid ItemDragToken;
    public long AggregateRevision = -1;
    public int AggregateAssemblyVersion = -1;
    public long? AggregateExtensionVersion;
    public long AggregatePresentationVersion = -1;
    public Guid[] AggregateBlocks = [];
    public ItemId[] AggregateItems = [];
    public IReadOnlyList<CompositorAggregateProperty> AggregateProperties = [];
    public readonly Dictionary<(string Key, int Axis), string> AggregateDrafts = [];
    public CompositorAggregateProperty? AggregateDrag;
}
internal sealed record IObjectNodeSelection(Guid? Block, ItemId? Item);
