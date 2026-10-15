using System.Collections.ObjectModel;

namespace Stride.Editor.ImGui;

// These values are copies, not leases or mutable authoring/runtime objects.
internal sealed record EditorInspectionSnapshot(int SchemaVersion, int Frame, string Status,
    Guid? ActiveDocumentId, ReadOnlyCollection<DocumentInspectionSnapshot> Documents,
    SelectionInspectionSnapshot Selection, ProjectInspectionSnapshot Project,
    ReadOnlyCollection<OperationInspectionSnapshot> Operations, ViewportInspectionSnapshot Viewport,
    ReadOnlyCollection<PanelInspectionSnapshot> Panels, int RetiringPreviewCount,
    ReadOnlyCollection<EntityInspectionSnapshot>? Hierarchy);

internal sealed record DocumentInspectionSnapshot(Guid Id, Guid? AuthoringGeneration, string? AssetId,
    string Path, string Kind, bool IsDirty, bool CanSave, bool CanUndo, bool CanRedo,
    int? ViewReferences, int? HistoryReferences);
internal sealed record SelectionInspectionSnapshot(string? AssetPath, string? HierarchyAssetId,
    Guid? PrimaryEntityId, ReadOnlyCollection<Guid> EntityIds, long? Version);
internal sealed record ProjectInspectionSnapshot(string? OpenPath, string? PackagePath,
    bool HasActivePackage, bool HasPackageChanges, bool HasCompiledContent, int AssetCount,
    string? CompilationFailure, string? LoadPhase, string? LoadDetail);
internal sealed record OperationInspectionSnapshot(string Id, bool IsPresent, string? TaskStatus,
    bool? CancellationRequested);
// Target allocation and submitted draw calls are evidence, not proof of visible geometry or GPU completion.
internal sealed record ViewportInspectionSnapshot(bool IsPresent, bool Visible, bool HasRuntimeScene,
    bool UsesAuthoredCompositor, string? SceneUrl, int TargetWidth, int TargetHeight,
    int ModelCount, int BackgroundCount, int SubmittedDrawCalls, bool RetirementStarted,
    bool RetirementDrainComplete, ReadOnlyCollection<string> Gaps);
// Visibility is the requested panel state, not native focus, bounds, or docking proof.
internal sealed record PanelInspectionSnapshot(string Id, bool RequestedVisible);
internal sealed record EntityInspectionSnapshot(Guid Id, string Name, Guid? ParentId,
    ReadOnlyCollection<Guid> ChildIds, ReadOnlyCollection<ComponentInspectionSnapshot> Components,
    Float3InspectionSnapshot Position, Float4InspectionSnapshot Rotation, Float3InspectionSnapshot Scale,
    bool? RuntimeTransformMatches);
internal sealed record ComponentInspectionSnapshot(Guid Id, string Type);
internal sealed record Float3InspectionSnapshot(float X, float Y, float Z);
internal sealed record Float4InspectionSnapshot(float X, float Y, float Z, float W);
