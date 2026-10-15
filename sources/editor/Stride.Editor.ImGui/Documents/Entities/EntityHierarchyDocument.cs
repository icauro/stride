using System.Security.Cryptography;
using Stride.Assets.Entities;
using Stride.Core.Assets;
using Stride.Core.Assets.Visitors;
using Stride.Core.Assets.Yaml;
using Stride.Core.IO;
using Stride.Core.Mathematics;
using Stride.Core.Reflection;
using Stride.Core.Transactions;
using Stride.Core.Yaml;
using Stride.Engine;

namespace Stride.Editor.ImGui;

/// <summary>Owns an actual entity hierarchy asset, original YAML metadata, and authored transactions.</summary>
internal abstract partial class EntityHierarchyDocument : IAssetTagDocument
{
    private readonly AssetLoadResult<Asset> loaded;
    private readonly ITransactionStack transactions;
    private readonly EditorDocumentHistoryScope historyScope;
    private readonly bool isReadOnly;
    private byte[]? diskHash;
    private long revision, nextRevision, savedRevision;
    private Entity? editingEntity;
    private TransformState editStart;
    public string Path { get; private set; }
    public bool CanSave => !isReadOnly && loaded.Asset is not IUnloadable;
    public void CommitEdits() => EndEdits();
    internal Guid DragIdentity { get; } = Guid.NewGuid();
    public EntityHierarchyAssetBase Asset => (EntityHierarchyAssetBase)loaded.Asset;
    public Entity? Selected { get => primarySelected; set => SelectEntity(value); }
    public bool IsDirty => diskHash == null || revision != savedRevision || HasPendingExtensionMutation || HasComponentVirtualChange || HasActiveComponentChange || HasActiveCollectionChange || HasActiveCommonPropertyChange || HasActiveMultiTransformChange || (editingEntity != null && TransformState.Read(editingEntity) != editStart);
    public bool CanUndo => transactions.CanRollback || HasComponentVirtualChange || HasActiveComponentChange || HasActiveCollectionChange || HasActiveCommonPropertyChange || HasActiveMultiTransformChange || HasActiveSceneOffsetChange || (editingEntity != null && TransformState.Read(editingEntity) != editStart);
    public bool CanRedo => transactions.CanRollforward;
    public bool HasUnloadableObjects { get; }
    public bool CanEditSelected => CanSave && SelectedCount == 1 && Selected != null &&
        Asset.Hierarchy.Parts[Selected.Id].Base == null;
    private string lastAction = "Scene opened";
    public string LastAction { get => lastAction; private set { lastAction = value; historyScope.Name(value); } }

    protected EntityHierarchyDocument(string path, Type assetType, EditorSessionHistory? history = null, bool readOnly = false)
        : this(path, assetType, LoadHierarchy(path), history, readOnly)
    { }
    private EntityHierarchyDocument(string path, Type assetType, HierarchyFileSnapshot snapshot, EditorSessionHistory? history, bool readOnly)
        : this(path, assetType, snapshot.Loaded, history, readOnly)
    {
        diskHash = snapshot.ContentHash;
    }

    protected EntityHierarchyDocument(string path, Type assetType, AssetLoadResult<Asset> asset,
        EditorSessionHistory? history = null, bool readOnly = false)
    {
        isReadOnly = readOnly;
        historyScope = new(this, history, assetType == typeof(PrefabAsset) ? "Edit prefab" : "Edit scene"); transactions = historyScope.Stack;
        Path = System.IO.Path.GetFullPath(path);
        loaded = asset;
        if (!assetType.IsInstanceOfType(loaded.Asset) || loaded.Asset is not EntityHierarchyAssetBase)
            throw new InvalidDataException("The file does not contain the expected entity hierarchy asset.");
        HasInheritedHierarchy = Asset.Hierarchy.Parts.Values.Any(part => part.Base != null);
        var visitor = new UnloadableDetector();
        visitor.Visit(Asset);
        HasUnloadableObjects = visitor.Found;
        lastAction = assetType == typeof(PrefabAsset) ? "Prefab opened" : "Scene opened";
    }
    private sealed record HierarchyFileSnapshot(AssetLoadResult<Asset> Loaded, byte[] ContentHash);
    private static HierarchyFileSnapshot LoadHierarchy(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var loaded = AssetFileSerializer.Load<Asset>(source, new UFile(path));
        source.Position = 0;
        return new(loaded, SHA256.HashData(source));
    }

    internal bool IsEditingTransform(Entity entity) => ReferenceEquals(editingEntity, entity);

    public void BeginTransformEdit()
    {
        EndComponentEdit();
        EndCollectionEdit(); EndCommonPropertyEdit(); EndMultiTransformEdit();
        if (editingEntity != null) return;
        if (!CanEditSelected) throw new InvalidOperationException("This entity cannot be edited until its dependencies and prefab semantics are supported.");
        editingEntity = Selected;
        editStart = TransformState.Read(editingEntity!);
    }

    public void EndTransformEdit()
    {
        if (editingEntity == null) return;
        var entity = editingEntity;
        var after = TransformState.Read(entity);
        editingEntity = null;
        if (after == editStart) return;
        var beforeRevision = revision;
        var afterRevision = ++nextRevision;
        using (CreateDocumentTransaction())
            RecordOperation(new TransformOperation(entity, editStart, after,
                () => revision = beforeRevision, () => revision = afterRevision));
        revision = afterRevision;
        LastAction = $"Edit transform: {entity.Name}";
    }

    public void Undo()
    {
        EndEdits();
        historyScope.BeforeReplay();
        if (!CanUndo) return;
        transactions.Rollback();
        LastAction = "Undo edit";
    }

    public void Redo()
    {
        EndEdits();
        historyScope.BeforeReplay();
        if (!CanRedo) return;
        transactions.Rollforward();
        LastAction = "Redo edit";
    }

    public void Save()
    {
        historyScope.EnsureMaySave();
        EndEdits();
        if (!CanSave) throw new InvalidOperationException("This scene cannot be saved.");
        using var stream = new MemoryStream();
        PrepareHierarchyMetadata();
        AssetFileSerializer.Save(stream, Asset, hierarchyItem?.YamlMetadata ?? loaded.YamlMetadata,
            assetNamespace: hierarchyItem?.Package?.Container?.AssetNamespace);
        var bytes = stream.ToArray();
        diskHash = WindowsOwnedAssetFileWriter.Write(Path, diskHash, bytes).ContentHash;
        savedRevision = revision;
        historyScope.Saved(diskHash);
        MarkAssetTagsSaved();
        LastAction = $"Saved {System.IO.Path.GetFileName(Path)}";
    }

    private sealed class TransformOperation(Entity entity, TransformState before, TransformState after,
        Action undoRevision, Action redoRevision) : Operation
    {
        protected override void Rollback() { before.Apply(entity); undoRevision(); }
        protected override void Rollforward() { after.Apply(entity); redoRevision(); }
    }

    private sealed class UnloadableDetector : AssetVisitorBase
    {
        public bool Found { get; private set; }
        public override void VisitObject(object obj, ObjectDescriptor descriptor, bool visitMembers)
        {
            if (obj is IUnloadable) { Found = true; return; }
            base.VisitObject(obj, descriptor, visitMembers);
        }
    }
}

