using System.Text;
using Stride.Assets.Effect;
using Stride.Core.Assets;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Serialization;
using Stride.Core.Transactions;
using Stride.Shaders.Compiler;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private EditorEffectCompilerSubscription? effectSubscription;
    private readonly List<Task> retiringEffectSubscriptions = [];
    private readonly HashSet<EffectCompileRequest> pendingEffects = [];
    private string[] effectPackageNames = [];
    private bool useEffectCompilerServer;
    private long pendingEffectsVersion, cachedPendingEffectsVersion = -1;
    private string? cachedEffectLogText;
    private int cachedPendingEffectCount;

    private void InitializeEffectLogs()
    {
        StopEffectLogs();
        useEffectCompilerServer = preferences?.Current.UseEffectCompilerServer == true;
    }

    private void StopEffectLogs()
    {
        RetireEffectSubscription();
        pendingEffects.Clear(); effectPackageNames = [];
        pendingEffectsVersion++; cachedEffectLogText = null;
    }

    private void PumpEffectLogs()
    {
        foreach (var retirement in retiringEffectSubscriptions.Where(task => task.IsCompleted).ToArray())
        {
            try { retirement.GetAwaiter().GetResult(); }
            catch (Exception error) { outputLog.Append("Project", LogMessageType.Warning, "Effect compiler server", error.ToString()); }
            retiringEffectSubscriptions.Remove(retirement);
        }
        if (!useEffectCompilerServer || workspace == null) return;
        var names = workspace.Session.Packages.Where(IsEditableAssetPackage).Select(package => package.Meta.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(effectPackageNames, StringComparer.Ordinal))
        {
            RetireEffectSubscription();
            effectPackageNames = names;
            effectSubscription = names.Length == 0 ? null : new(names);
        }
        if (effectSubscription == null) return;
        while (effectSubscription.TryTakeFailure(out var error))
            outputLog.Append("Project", LogMessageType.Warning, "Effect compiler server", error.ToString());
        for (var count = 0; count < 128 && effectSubscription.TryTakeRequest(out var data); count++)
        {
            try
            {
                using var stream = new MemoryStream(data, false);
                var reader = new BinarySerializationReader(stream);
                reader.Context.SerializerSelector = SerializerSelector.AssetWithReuse;
                EffectCompileRequest request = null!;
                reader.SerializeExtended(ref request, ArchiveMode.Deserialize, null);
                if (request != null && pendingEffects.Add(request)) pendingEffectsVersion++;
            }
            catch (Exception error)
            { outputLog.Append("Project", LogMessageType.Warning, "Effect compiler server", error.ToString()); }
        }
    }

    private void RetireEffectSubscription()
    {
        if (effectSubscription is not { } subscription) return;
        effectSubscription = null;
        var completion = subscription.DisposeAsync().AsTask();
        retiringEffectSubscriptions.Add(completion);
        // Final host destruction can stop actor polling. Observe faults without
        // calling engine/UI objects from the completing connection thread.
        _ = completion.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private AssetItem? FindEffectLogAsset()
    {
        if (workspace == null) return null;
        var primary = workspace.Package;
        if (primary == null) return null;
        var localDependencies = workspace.Session.CurrentProject?.FlattenedDependencies
            .Where(dependency => dependency.Type == DependencyType.Project).Select(dependency => dependency.Package).ToHashSet() ?? [];
        return primary.Assets.FirstOrDefault(item => item.Asset is EffectLogAsset && item.UnqualifiedUrl == EffectLogAsset.DefaultFile)
            ?? workspace.Session.Packages.Where(package => !package.IsReadOnly && localDependencies.Contains(package))
                .SelectMany(package => package.Assets).FirstOrDefault(item => item.Asset is EffectLogAsset && item.UnqualifiedUrl == EffectLogAsset.DefaultFile);
    }

    private string EffectLogText(AssetItem? item) => item == null ? ""
        : authoring?.Find(item.Id)?.Document is ScriptSourceDocument source ? source.Text : ((EffectLogAsset)item.Asset).Text;

    private int PendingEffectCount
    {
        get
        {
            if (pendingEffects.Count == 0) return 0;
            var text = EffectLogText(FindEffectLogAsset());
            if (cachedPendingEffectsVersion == pendingEffectsVersion && cachedEffectLogText == text) return cachedPendingEffectCount;
            using var stream = new MemoryStream();
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes); stream.Position = 0;
            cachedPendingEffectsVersion = pendingEffectsVersion; cachedEffectLogText = text;
            try
            {
                using var store = new EffectLogStore(stream); store.LoadNewValues();
                return cachedPendingEffectCount = pendingEffects.Count(request => !store.Contains(request));
            }
            catch (Exception error)
            {
                outputLog.Append("Project", LogMessageType.Warning, "Effect library", error.ToString());
                return cachedPendingEffectCount = 0;
            }
        }
    }

    private bool CanImportEffectLog => workspace != null && authoring != null && sessionHistory != null
        && workspace.Package is { } activePackage && IsEditableAssetPackage(activePackage) && !IsAuthoringPipelineUpdating && projectLoading == null
        && !IsUserProjectBuilding && !closeRequested && !closePromptOpen && closingDocument == null;

    private void ImportEffectLog()
    {
        if (!CanImportEffectLog || pendingEffects.Count == 0) return;
        documents.CommitAll();
        var imported = pendingEffects.ToArray();
        var item = FindEffectLogAsset();
        using var capture = sessionHistory!.CaptureOperationsAtomic(() =>
        {
            using var transaction = sessionHistory.CreateTransaction("Import effect log");
            if (item == null)
            {
                var package = workspace!.Package;
                var folder = assetBrowser.Packages.First(root => ReferenceEquals(root.Package, package)).Children.First(child => !child.IsCode);
                item = PasteSourceAssets([new AssetItem(new UFile(EffectLogAsset.DefaultFile), new EffectLogAsset { Text = "" })], folder).Single();
            }
            var source = GetCanonicalDocument(item.FullPath.ToOSPath(), () => CreateScriptDocument(item.FullPath.ToOSPath())) as ScriptSourceDocument
                ?? throw new InvalidOperationException("The effect library has no canonical source owner.");
            if (!source.CanEditText) throw new InvalidOperationException("The effect library is read-only.");
            using var stream = new MemoryStream();
            var bytes = Encoding.UTF8.GetBytes(source.Text); stream.Write(bytes); stream.Position = 0;
            using var store = new EffectLogStore(stream) { UseTransaction = false }; store.LoadNewValues();
            foreach (var request in imported) if (!store.Contains(request)) store[request] = true;
            source.UpdateEditorText(Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal));
            var operation = new ImportedEffectsOperation(pendingEffects, imported, () => pendingEffectsVersion++);
            pendingEffects.ExceptWith(imported);
            pendingEffectsVersion++;
            try { sessionHistory.PushOperation(operation, authoring!.Find(item.Id)!); }
            catch { pendingEffects.UnionWith(imported); throw; }
        });
        capture.Publish(sessionHistory, "Import effect log");
        if (item != null && assetBrowser.RevealAsset(workspace!, item.Id) is { } entry)
        { selectedAssetPath = entry.Path; ActivateAssetPropertyContext(); }
    }

    private sealed class ImportedEffectsOperation(HashSet<EffectCompileRequest> pending, EffectCompileRequest[] imported, Action changed) : Operation
    {
        protected override void Rollback() { pending.UnionWith(imported); changed(); }
        protected override void Rollforward() { pending.ExceptWith(imported); changed(); }
        protected override void FreezeContent() { }
    }
}
