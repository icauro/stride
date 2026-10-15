using System.Security.Cryptography;
using Stride.Assets.Models;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Visitors;
using Stride.Core.Diagnostics;
using Stride.Core.Extensions;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Core.Quantum;
using Stride.Core.Reflection;
using Stride.Core.Transactions;
using Stride.Core.Yaml;
using Stride.Rendering.ProceduralModels;

namespace Stride.Editor.ImGui;

/// <summary>Authored procedural asset and its real Stride asset graph, metadata and edit history.</summary>
internal sealed partial class ProceduralAssetDocument : IEditorAssetGraphDocument
{
    private sealed record ModelName(string Value);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type, ModelName> ModelNames = new();
    public bool CanSave => CanEdit;
    public void CommitEdits() => EndEdit();
    internal static string ModelDisplayName(Type? type) => type == null ? "Unresolved" : ModelNames.GetValue(type,
        value => new(TypeDescriptorFactory.Default.AttributeRegistry.GetAttribute<DisplayAttribute>(value)?.Name ?? value.Name)).Value;
    private readonly ITransactionStack transactions;
    private readonly EditorDocumentHistoryScope historyScope;
    private readonly AssetItem item;
    private readonly AssetPropertyGraph? graph;
    private readonly LoggerResult log = new();
    private byte[] diskHash;
    private AssetPropertyField? activeField;
    private EditorQuantumMutation? activeJournal;
    private long revision, nextRevision, savedRevision;
    private readonly bool isReadOnly;
    public string Path { get; private set; }
    public ProceduralModelAsset Asset { get; }
    public IReadOnlyList<AssetPropertyField> Fields { get; private set; } = [];
    public IReadOnlyList<Type> ModelTypes => ProceduralModelTypeCatalog.Current;
    public bool CanEdit { get; }
    public bool IsDirty => diskHash.Length == 0 || revision != savedRevision || HasActiveChange || HasVirtualChange;
    public bool CanUndo => transactions.CanRollback || HasActiveChange || HasVirtualChange;
    public bool CanRedo => transactions.CanRollforward;
    private bool HasActiveChange => activeJournal?.HasEffect == true;
    private string lastAction = "Asset opened";
    public string LastAction { get => lastAction; private set { lastAction = value; historyScope.Name(value); } }

    public ProceduralAssetDocument(string path, EditorSessionHistory? history = null, bool readOnly = false)
        : this(path, AssetFileSerializer.Load<ProceduralModelAsset>(path), SHA256.HashData(File.ReadAllBytes(path)), history, readOnly) { }
    internal ProceduralAssetDocument(string path, AssetLoadResult<ProceduralModelAsset> loaded, byte[] baseline, EditorSessionHistory? history, bool readOnly)
    {
        isReadOnly = readOnly;
        historyScope = new(this, history, "Edit procedural model"); transactions = historyScope.Stack;
        Path = System.IO.Path.GetFullPath(path);
        Asset = loaded.Asset; diskHash = baseline.ToArray();
        var canonical = history?.HierarchyGraphs.ResolveAssetItem?.Invoke(Asset.Id);
        item = canonical?.Clone(keepPackage: true, newAsset: Asset) ?? new AssetItem(System.IO.Path.GetFileNameWithoutExtension(Path), Asset);
        item.YamlMetadata.ReplaceWith(loaded.YamlMetadata);
        DocumentAssetPath.Update(item, Path);
        var detector = new UnloadableDetector(); detector.Visit(Asset);
        CanEdit = !readOnly && (Asset.Archetype == null || history != null);
        AssetQuantumRegistry.RegisterAssembly(typeof(AssetPropertyGraph).Assembly);
        AssetQuantumRegistry.RegisterAssembly(typeof(ProceduralModelAsset).Assembly);
        var container = new AssetPropertyGraphContainer(history?.HierarchyGraphs.Nodes ?? new AssetNodeContainer { NodeBuilder = { NodeFactory = new AssetNodeFactory() } });
        graph = container.InitializeAsset(item, log)!;
        try
        {
            if (history != null) history.AssetGraphs.Register(this, graph);
            else if (Asset.Archetype == null) graph.Initialize();
            RefreshFields();
        }
        catch
        {
            history?.AssetGraphs.Unregister(this); graph.Dispose();
            throw;
        }
    }
    private void RefreshFields()
    {
        RefreshAuthoringNodeAccess();
        proceduralMembers.Clear();
        var typeMember = graph!.RootNode[nameof(ProceduralModelAsset.Type)];
        proceduralMembers.Add(typeMember);
        var typeNode = typeMember.Target;
        if (typeNode == null || typeNode.Retrieve() is IUnloadable) { Fields = []; return; }
        var memberVisitor = graph.CreateReconcilerVisitor();
        memberVisitor.Visiting += (node, _) => { if (node is IMemberNode member) proceduralMembers.Add(member); };
        memberVisitor.Visit(typeNode);
        Fields = typeNode.Members
            .Select(node => (Node: node, Display: node.MemberDescriptor.GetCustomAttributes<DisplayAttribute>(true).FirstOrDefault()))
            .Where(member => member.Display?.Browsable != false)
            .Select(member => PropertyField(member.Node)).ToArray();
    }
    public void ChangeType(Type type)
    {
        if (!CanEdit || !ModelTypes.Contains(type) || !CanEditAssetSettingNode(AuthoringGraph.RootNode[nameof(ProceduralModelAsset.Type)])) throw new InvalidOperationException("This procedural model type cannot be selected.");
        EndEdit();
        if (Asset.Type?.GetType() == type) return;
        var after = (IProceduralModel)ObjectFactoryRegistry.NewInstance(type);
        var node = graph!.RootNode[nameof(ProceduralModelAsset.Type)];
        ApplyInheritedAssetMutation(() => node.Update(after), "Change model type");
        LastAction = "Change model type to " + ModelDisplayName(type);
    }
    public void BeginEdit(AssetPropertyField field)
    {
        if (!CanEdit || (!field.SupportsEditing && !CanUpdateRegisteredProperty(field)) || !IsPropertyFieldCurrent(field)
            || !AssetMetadataDocument.CanAssignAuthoringNode(field.Node) || !CanEditAssetSettingNode(field.Node) || NumericReadOnly(field.Node))
            throw new InvalidOperationException("This asset property cannot be edited.");
        if (activeField == field) return;
        EndEdit(); activeField = field; activeJournal = CreateMutationJournal();
    }
    public void Update(AssetPropertyField field, object? value)
    {
        value = PropertyValueValidation.Normalize(value, field.NumericRange);
        BeginEdit(field);
        try { field.Node.Update(value); }
        catch { CancelEdit(); throw; }
    }
    public void CancelEdit()
    {
        if (activeJournal == null) return;
        var journal = activeJournal;
        if (SessionHistory != null) SessionHistory.CompensateInitialMutation(journal.RestoreInitial); else journal.RestoreInitial();
        activeField = null; activeJournal = null; journal.Dispose();
    }
    public void EndEdit()
    {
        EndVirtualEdit();
        if (activeJournal == null) return;
        var journal = activeJournal; var label = activeField!.Label;
        activeField = null; activeJournal = null;
        PublishMutation(journal, "Edit " + label, () => { });
        LastAction = "Edit " + label;
    }
    public void Undo() { EndEdit(); if (transactions.CanRollback) transactions.Rollback(); }
    public void Redo() { EndEdit(); if (transactions.CanRollforward) transactions.Rollforward(); }
    public void Save()
    {
        EndEdit();
        historyScope.EnsureMaySave();
        if (!CanEdit) throw new InvalidOperationException("This procedural asset cannot be saved.");
        if (diskHash.Length == 0)
        {
            if (File.Exists(Path) || Directory.Exists(Path)) throw new IOException("The new asset path is occupied by another file.");
        }
        else if (!SHA256.HashData(File.ReadAllBytes(Path)).SequenceEqual(diskHash)) throw new IOException("The asset changed on disk. Reload it before saving to avoid overwriting external changes.");
        var authoredItem = AuthoringGraph.AuthoredItem;
        graph!.PrepareForSave(log, authoredItem);
        if (log.HasErrors) throw new InvalidOperationException(log.ToText());
        using var stream = new MemoryStream(); AssetFileSerializer.Save(stream, Asset, authoredItem.YamlMetadata, assetNamespace: authoredItem.Package?.Container?.AssetNamespace);
        var bytes = stream.ToArray();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var written = WindowsOwnedAssetFileWriter.Write(Path, diskHash.Length == 0 ? null : diskHash, bytes);
        diskHash = written.ContentHash; savedRevision = revision; historyScope.Saved(diskHash); MarkAssetTagsSaved(); LastAction = "Saved " + System.IO.Path.GetFileName(Path);
    }
    public void Dispose() { DrainVirtualMutationCleanup(); historyScope.EnsureMayDispose(); CancelVirtualEdit(); CancelEdit(); DisposeNumericPresentation(); SessionHistory?.AssetGraphs.Unregister(this); graph?.Dispose(); }
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
