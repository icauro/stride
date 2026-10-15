namespace Stride.Editor.ImGui;

internal sealed record EditorRecoveryRecord(string Path, string Kind, string? AssetId, string? Namespace,
    string? PackagePath, string? AssetLocation, byte[] BaselineHash, byte[] Payload, Guid[] Selection,
    Guid? PrimarySelection, string? ProjectPath = null, string[]? SourceFolders = null, string? SourceFolder = null);

internal sealed record EditorRecoveryEntry(string Path, string Kind, string? AssetId, string? Namespace,
    string? PackagePath, string? AssetLocation, byte[] BaselineHash, string PayloadHash, Guid[] Selection,
    Guid? PrimarySelection, string? ProjectPath, string[]? SourceFolders, string? SourceFolder);

internal sealed record EditorRecoveryManifest(int Version, Guid SessionId, string ProjectPath, int ProcessId,
    DateTime ProcessStartedUtc, DateTime CapturedUtc, bool Completed, EditorRecoveryEntry[] Documents,
    string[] LoadedAssemblies, EditorRecoveryWorkspaceState? Workspace = null);

internal sealed record EditorRecoveryCandidate(string ManifestPath, EditorRecoveryManifest Manifest);
internal sealed record EditorRecoveryCompletion(int Version, Guid SessionId, string ProjectPath, int ProcessId,
    DateTime ProcessStartedUtc, string CheckpointFile, string CheckpointHash, DateTime CompletedUtc);
