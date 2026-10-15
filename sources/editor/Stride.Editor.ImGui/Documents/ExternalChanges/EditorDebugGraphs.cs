using Stride.Core.Assets.Quantum;

namespace Stride.Editor.ImGui;

internal abstract partial class EntityHierarchyDocument { internal AssetPropertyGraph DebugGraph => HierarchyGraph; }
internal sealed partial class ProceduralAssetDocument { internal AssetPropertyGraph? DebugGraph => graph; }
