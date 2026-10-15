namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    /// <summary>Call after the scene draw on the game thread, not from an external polling thread.</summary>
    internal EngineOutputSample ReadSceneVerificationOutput()
    {
        var target = InspectionViewport?.Target ?? throw new InvalidOperationException("E01: Scene output is unavailable.");
        return EngineOutputSample.Read(GraphicsContext.CommandList, target);
    }

    internal void SaveSceneVerificationOutput(string path)
    {
        var target = InspectionViewport?.Target ?? throw new InvalidOperationException("E01: Scene output is unavailable.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        target.Save(GraphicsContext.CommandList, file, Stride.Graphics.ImageFileType.Png);
    }

    internal void AssertCompiledSceneVerification()
    {
        var viewport = InspectionViewport;
        if (viewport == null || document == null || workspace == null)
            throw new InvalidOperationException("E01: A real project, scene document and viewport are required.");
        EngineCapabilityAssertions.CompiledScene(viewport, document, workspace);
    }

    internal void AssertRuntimeEntityVerification(Guid entityId, Guid? parentId)
    {
        var viewport = InspectionViewport;
        if (viewport == null || document == null || !document.Asset.Hierarchy.Parts.ContainsKey(entityId))
            throw new InvalidOperationException($"E01: Authored/runtime entity {entityId} is unavailable.");
        EngineCapabilityAssertions.RuntimeEntity(viewport, document.Asset.Hierarchy.Parts[entityId].Entity, parentId);
    }
}
