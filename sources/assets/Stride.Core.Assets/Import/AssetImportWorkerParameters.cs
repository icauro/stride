namespace Stride.Core.Assets;

/// <summary>Immutable serialized input/output contract for the native asset importer worker.</summary>
public sealed class AssetImportWorkerParameters
{
    public string[] Files { get; set; } = [];
    public string[] AssemblyPaths { get; set; } = [];
    public Dictionary<string, string> AssemblyModules { get; set; } = [];
    public string? PreferredType { get; set; }
    public string? ImporterId { get; set; }
    public Dictionary<string, bool>? Outputs { get; set; }
    public Dictionary<string, AssetImportWorkerFileOptions> FileOptions { get; set; } = [];
    public bool IsForReImport { get; set; }
    public bool DeduplicateMaterials { get; set; } = true;
    public string OutputDirectory { get; set; } = "";
}
public sealed class AssetImportWorkerFileOptions
{
    public string? ImporterId { get; set; }
    public Dictionary<string, bool>? Outputs { get; set; }
    public bool DeduplicateMaterials { get; set; } = true;
}
public sealed class AssetImportWorkerResult
{
    public AssetImportWorkerAsset[] Assets { get; set; } = [];
    public Dictionary<string, string> SourceHashes { get; set; } = [];
    public Dictionary<string, string> ContentHashes { get; set; } = [];
    public AssetImportWorkerMessage[] Messages { get; set; } = [];
}
public sealed class AssetImportWorkerAsset
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public string Type { get; set; } = "";
    public string SourceFile { get; set; } = "";
}
public sealed class AssetImportWorkerMessage
{
    public string Level { get; set; } = "";
    public string Text { get; set; } = "";
}
public sealed class AssetImportWorkerProgress
{
    public const string Prefix = "@@STRIDE_IMPORT@@";
    public string File { get; set; } = "";
    public string Importer { get; set; } = "";
    public int FileIndex { get; set; }
    public int FileCount { get; set; }
    public string Message { get; set; } = "";
    public bool HasKnownSteps { get; set; }
    public int CurrentStep { get; set; }
    public int StepCount { get; set; }
    public bool Completed { get; set; }
    public string? AssetUrl { get; set; }
    public string? AssetType { get; set; }
    public int AssetIndex { get; set; }
    public int AssetCount { get; set; }
}
