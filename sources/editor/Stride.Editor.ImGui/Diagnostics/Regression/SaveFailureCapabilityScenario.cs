using System.Text;

namespace Stride.Editor.ImGui;

/// <summary>Actual SceneDocument saving against a disposable fixture, with real Windows I/O failures.</summary>
internal static class SaveFailureCapabilityScenario
{
    internal static void Run(string scenePath)
    {
        using var document = new SceneDocument(scenePath);
        var entity = document.Asset.Hierarchy.RootParts.First();
        var entityId = entity.Id;
        var transformId = entity.Transform.Id;
        var originalName = entity.Name;
        var originalBytes = File.ReadAllBytes(scenePath);
        Require(!document.IsDirty && !document.CanUndo && !document.CanRedo, "Fixture must open at a clean save point");

        var editedName = originalName + " save retry – 星";
        document.Rename(entity, editedName);
        using (var heldRead = new FileStream(scenePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = RequireSaveFailure(document);
            Require(error.Message.Contains("opened for saving", StringComparison.Ordinal),
                "Sharing violation must identify that the authored file could not be opened for saving");
            Require(File.ReadAllBytes(scenePath).AsSpan().SequenceEqual(originalBytes), "Failed locked save must preserve disk bytes");
            AssertPending(document, entityId, transformId, editedName);
        }

        // History must remain usable, including the original disk save point, after failed publication.
        document.Undo();
        Require(entity.Name == originalName && !document.IsDirty && !document.CanUndo && document.CanRedo,
            "Failed save must not advance the saved revision or discard undo history");
        document.Redo();
        AssertPending(document, entityId, transformId, editedName);
        document.Save();
        AssertSaved(document, scenePath, entityId, transformId, editedName);
        document.Undo();
        Require(entity.Name == originalName && document.IsDirty, "Undo after successful retry must depart the new save point");
        document.Redo();
        Require(entity.Name == editedName && !document.IsDirty, "Redo must return to the successful retry save point");

        var savedBytes = File.ReadAllBytes(scenePath);
        var conflictName = editedName + " conflict retry";
        document.Rename(entity, conflictName);
        // A YAML comment keeps the externally changed asset independently loadable.
        var externalBytes = savedBytes.Concat(Encoding.UTF8.GetBytes("\n# C06 independently authored external edit\n")).ToArray();
        File.WriteAllBytes(scenePath, externalBytes);
        var conflict = RequireSaveFailure(document);
        Require(conflict.Message.Contains("changed on disk", StringComparison.Ordinal),
            "External modification must produce the actual conflict diagnostic");
        Require(File.ReadAllBytes(scenePath).AsSpan().SequenceEqual(externalBytes), "Conflict rejection must preserve exact external bytes");
        AssertPending(document, entityId, transformId, conflictName);
        using (var external = new SceneDocument(scenePath))
            Require(external.Asset.Hierarchy.Parts[entityId].Entity.Name == editedName,
                "Independent reload of external data must not contain the rejected in-memory edit");

        document.Undo();
        Require(entity.Name == editedName && !document.IsDirty && document.CanRedo,
            "Conflict rejection must retain history and the previous document save point");
        document.Redo();
        AssertPending(document, entityId, transformId, conflictName);

        // Explicit fixture reconciliation restores the baseline. This does not claim
        // the editor automatically merges external changes or supplies conflict-resolution UI.
        File.WriteAllBytes(scenePath, savedBytes);
        document.Save();
        AssertSaved(document, scenePath, entityId, transformId, conflictName);
    }

    private static IOException RequireSaveFailure(SceneDocument document)
    {
        try { document.Save(); }
        catch (IOException error) { return error; }
        throw new InvalidOperationException("C06: save unexpectedly succeeded under a controlled write failure");
    }

    private static void AssertPending(SceneDocument document, Guid id, Guid transformId, string name)
    {
        var entity = document.Asset.Hierarchy.Parts[id].Entity;
        Require(entity.Name == name && entity.Transform.Id == transformId,
            "Save failure must preserve authored identity and pending value");
        Require(document.IsDirty && document.CanSave && document.CanUndo && !document.CanRedo,
            "Save failure must remain dirty, retryable and undoable without publishing a save point");
    }

    private static void AssertSaved(SceneDocument document, string path, Guid id, Guid transformId, string name)
    {
        Require(!document.IsDirty && document.CanUndo && !document.CanRedo, "Successful retry must publish the current save point and retain history");
        using var reopened = new SceneDocument(path);
        var entity = reopened.Asset.Hierarchy.Parts[id].Entity;
        Require(entity.Name == name && entity.Transform.Id == transformId && !reopened.IsDirty,
            "Independent disk reload must contain the retried edit and original component identity");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("C06: " + message);
    }
}
