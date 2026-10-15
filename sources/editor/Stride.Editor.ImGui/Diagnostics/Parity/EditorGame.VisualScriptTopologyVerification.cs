using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stride.Core.Yaml;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private IEnumerator<object?>? visualScriptTopologyVerification;
    private string? visualScriptTopologyArtifacts;
    internal void StartVisualScriptTopologyVerification(string? artifacts = null)
    {
        if (visualScriptTopologyVerification != null) throw new InvalidOperationException("Topology verification is already running.");
        visualScriptTopologyArtifacts = artifacts;
        visualScriptTopologyVerification = RunVisualScriptTopologyVerification().GetEnumerator();
    }
    internal object? StepVisualScriptTopologyVerification()
    {
        var run = visualScriptTopologyVerification ?? throw new InvalidOperationException("Topology verification is not running.");
        if (!run.MoveNext()) throw new InvalidOperationException("Topology verification ended without evidence.");
        var evidence = run.Current;
        if (evidence != null) { run.Dispose(); visualScriptTopologyVerification = null; }
        return evidence;
    }
    internal void CancelVisualScriptTopologyVerification()
    {
        visualScriptTopologyVerification?.Dispose(); visualScriptTopologyVerification = null;
    }
    private IEnumerable<object?> RunVisualScriptTopologyVerification()
    {
        if (!args.Contains("--ui-test") || workspace == null || sessionHistory == null)
            throw new InvalidOperationException("Topology verification requires the actual private loaded project.");
        var path = visualScriptHistoryFixturePath ?? throw new InvalidOperationException("Prepare and complete the native history producer fixture first.");
        var previous = documents.Active;
        var producer = (VisualScriptDocument)GetCanonicalDocument(path, () => new VisualScriptDocument(path, sessionHistory, false));
        var entry = documents.Find(producer) ?? documents.Add(producer);
        InitializeVisualScriptDocument(producer); ActivateDocument(entry);
        yield return null;
        var baseline = InspectVisualScriptHistoryNativeState() ?? throw new InvalidOperationException("Actual producer/generated canonical owners are unavailable.");
        Check(!producer.IsDirty && !baseline.Generated.IsDirty, "Topology starts from a clean real compiler-owned baseline");
        var diskBefore = File.ReadAllBytes(path);
        var generatedBefore = File.ReadAllBytes(baseline.Generated.Path);
        var history = sessionHistory;
        var checkpoints = new List<object>();
        var method = producer.AddMethod();
        yield return null;
        Check(history.History.Position == baseline.HistoryPosition + 1, "Native method creation creates exactly one shared journal");
        var templates = VisualScriptBlockTemplate.Discover();
        var start = (FunctionStartBlock)templates.Single(template => template.Type == typeof(FunctionStartBlock)).Create();
        var branch = (ConditionalBranchBlock)templates.Single(template => template.Type == typeof(ConditionalBranchBlock)).Create();
        var code = (CustomCodeBlock)templates.Single(template => template.Type == typeof(CustomCodeBlock)).Create();
        // Factory instance configuration is detached arrangement; registration,
        // publication, generated slots and every later mutation remain native.
        code.Code = "int topologyMarker = 37;";
        producer.AddBlock(method, start, new Int2(-151, 31), producer.InspectorCompilation);
        yield return null;
        producer.AddBlock(method, branch, new Int2(17, 31), producer.InspectorCompilation);
        yield return null;
        producer.AddBlock(method, code, new Int2(211, 127), producer.InspectorCompilation);
        yield return null;
        var output = branch.Slots.Single(slot => slot.Kind == SlotKind.Execution && slot.Direction == SlotDirection.Output && slot.Name == "False");
        var input = code.Slots.Single(slot => slot.Kind == SlotKind.Execution && slot.Direction == SlotDirection.Input);
        var entryOutput = start.Slots.Single(slot => slot.Kind == SlotKind.Execution && slot.Direction == SlotDirection.Output);
        var branchInput = branch.Slots.Single(slot => slot.Kind == SlotKind.Execution && slot.Direction == SlotDirection.Input);
        producer.Connect(method, entryOutput, branchInput);
        yield return null;
        Check(branch.Slots.All(slot => ReferenceEquals(slot.Owner, branch)) && code.Slots.All(slot => ReferenceEquals(slot.Owner, code)),
            "Registered native block factories generate slots owned by their actual blocks");
        var beforeConnect = history.History.Position;
        producer.Connect(method, output, input);
        yield return null;
        var link = method.Links.Values.Single(value => ReferenceEquals(value.Source, output));
        Check(history.History.Position == beforeConnect + 1 && ReferenceEquals(link.Source, output) && ReferenceEquals(link.Target, input),
            "Connect publishes one native journal with exact slot endpoint identity");
        producer.Connect(method, output, input);
        Check(history.History.Position == beforeConnect + 1, "Duplicate connection creates no journal");
        try { producer.Connect(method, input, input); throw new InvalidDataException("Invalid native connection was accepted."); }
        catch (InvalidOperationException) { }
        Check(history.History.Position == beforeConnect + 1 && method.Links.Values.Single(value => ReferenceEquals(value.Source, output)).Id == link.Id,
            "Rejected native connection preserves graph and journal");
        producer.Disconnect(method, input);
        yield return null;
        Check(method.Links.Count == 1 && method.Links.Values.All(value => value.Id != link.Id), "Disconnect removes actual native link and retains entry connection");
        history.Undo(); yield return null;
        Check(method.Links.Values.Single(value => value.Id == link.Id).Id == link.Id && ReferenceEquals(method.Links.Values.Single(value => value.Id == link.Id).Target, input),
            "Disconnect Undo restores exact native link and slot identity");
        history.Redo(); yield return null;
        Check(method.Links.Count == 1 && method.Links.Values.All(value => value.Id != link.Id), "Disconnect Redo removes link again");
        history.Undo(); yield return null;
        var beforeDelete = history.History.Position;
        producer.DeleteGraphSelection(method, [code.Id], []);
        yield return null;
        Check(!method.Blocks.Values.Contains(code) && method.Links.Count == 1 && history.History.Position == beforeDelete + 1,
            "Graph selection deletion removes dependent links with block in one journal");
        history.Undo(); yield return null;
        Check(method.Blocks.Values.Contains(code) && method.Links.Values.Any(value => value.Id == link.Id),
            "Graph deletion Undo restores original native block and dependent link");
        history.Redo(); yield return null;
        Check(!method.Blocks.Values.Contains(code) && method.Links.Count == 1, "Graph deletion Redo removes block and link");
        history.Undo(); yield return null;
        var compiled = InspectVisualScriptHistoryNativeState()!;
        var proofDirectory = Path.GetFullPath(visualScriptTopologyArtifacts ?? Path.Combine(Path.GetDirectoryName(path)!, "topology-proof"));
        Directory.CreateDirectory(proofDirectory);
        // Mandatory pre-assert receipts distinguish native compiler behavior
        // from projection ownership/currentness without guessing or relaxing it.
        WriteCompilerReceipt(compiled);
        var nativeSnapshot = producer.CaptureCompilation(GetVisualScriptCompilerOwner(producer).Item);
        Check(nativeSnapshot.ObjectReferenceCount == 4, "Native compiler snapshot carries exactly four endpoint object references");
        CertifyAliases((VisualScriptAsset)nativeSnapshot.Item.Asset);
        var projectionDeadline = Environment.TickCount64 + 5000;
        while (compiled.Generated.Text != compiled.CompilerText && Environment.TickCount64 < projectionDeadline)
        {
            yield return null;
            compiled = InspectVisualScriptHistoryNativeState() ?? throw new InvalidOperationException("Canonical topology owner disappeared while awaiting generated output.");
            WriteCompilerReceipt(compiled);
        }
        Check(compiled.CompilerText != baseline.CompilerText && compiled.CompilerText.Contains("topologyMarker", StringComparison.Ordinal)
            && compiled.Generated.Text == compiled.CompilerText,
            "Real graph mutations update protected generated source to the actual serialized native compiler output");
        Check(ReferenceEquals(compiled.ProducerState, baseline.ProducerState) && ReferenceEquals(compiled.GeneratedState, baseline.GeneratedState),
            "Topology mutation retains exact canonical producer and generated owners");
        producer.Save(); yield return null;
        var restoredLoad = AssetFileSerializer.Load<VisualScriptAsset>(path);
        var restored = restoredLoad.Asset;
        Check(restoredLoad.YamlMetadata.RetrieveMetadata(AssetObjectSerializerBackend.ObjectReferencesKey)?.Count == 4,
            "Native saved graph carries exactly four endpoint object references");
        CertifyAliases(restored);
        var restoredMethod = restored.Methods.Single(value => value.Id == method.Id);
        var restoredLink = restoredMethod.Links.Values.Single(value => value.Id == link.Id);
        Check(restored.Id == producer.Asset.Id && restoredMethod.Links.Count == 2
            && restoredMethod.Links.Values.Any(value => value.Source.Id == entryOutput.Id && value.Target.Id == branchInput.Id)
            && restoredMethod.Blocks.Values.Select(block => block.Id).Order().SequenceEqual(new[] { start.Id, branch.Id, code.Id }.Order())
            && restoredLink.Id == link.Id && restoredLink.Source.Id == output.Id && restoredLink.Target.Id == input.Id
            && ReferenceEquals(restoredLink.Source.Owner, restoredMethod.Blocks.Values.Single(block => block.Id == branch.Id))
            && ReferenceEquals(restoredLink.Target.Owner, restoredMethod.Blocks.Values.Single(block => block.Id == code.Id)),
            "Independent native deserialize retains asset/method/block/link/slot IDs and actual restored endpoint owners");
        Check(File.ReadAllText(baseline.Generated.Path) == compiled.CompilerText, "Saved generated bytes carry the exact native compiler text");
        checkpoints.Add(new { method.Id, branch = branch.Id, code = code.Id, link = link.Id, source = output.Id, target = input.Id });
        while (history.History.Position > baseline.HistoryPosition) { history.Undo(); yield return null; }
        Check(producer.Asset.Methods.All(value => value.Id != method.Id) && baseline.Generated.Text == baseline.CompilerText,
            "Native history restores original topology and generated text");
        producer.Save(); yield return null;
        Check(diskBefore.SequenceEqual(File.ReadAllBytes(path)) && generatedBefore.SequenceEqual(File.ReadAllBytes(baseline.Generated.Path)),
            "Successful topology cleanup restores exact producer and generated saved baselines");
        CloseDocument(entry);
        if (previous != null && documents.Entries.Contains(previous)) ActivateDocument(previous);
        yield return new { scenario = "visualscript.graph-topology", passed = true, checkpoints,
            nativeFactorySlots = true, connectNoOpAndRejection = true, disconnectUndoRedo = true, dependentDeleteUndoRedo = true,
            nativeCompilerAndCanonicalOwners = true, independentNativePersistence = true, originalDiskRestored = true,
            coverage = "Real registered block factories and canonical document/history/serialized compiler services. No graph-canvas pointer gestures, slot-regeneration property editor, diagnostic navigation or assembly reload are certified." };

        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void WriteCompilerReceipt(VisualScriptHistoryNativeState current)
        {
            File.WriteAllText(Path.Combine(proofDirectory, "baseline-compiler.txt"), baseline.CompilerText, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(proofDirectory, "current-compiler.txt"), current.CompilerText, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(proofDirectory, "current-generated.txt"), current.Generated.Text, new UTF8Encoding(false));
            var compilation = producer.CaptureCompilation(GetVisualScriptCompilerOwner(producer).Item);
            File.WriteAllText(Path.Combine(proofDirectory, "compiler-receipt.json"), JsonSerializer.Serialize(new
            {
                frame = Hexa.NET.ImGui.ImGui.GetFrameCount(), assetId = producer.Asset.Id.ToString(), producer.EditRevision,
                producerGeneration = current.ProducerState.Generation, generatedGeneration = current.GeneratedState.Generation,
                sameProducer = ReferenceEquals(current.ProducerState, baseline.ProducerState),
                sameGenerated = ReferenceEquals(current.GeneratedState, baseline.GeneratedState),
                generatedOwnedByProducer = current.Generated.IsGeneratedOutputOwnedBy(current.ProducerState),
                generatedReadOnly = !current.Generated.CanEditText, current.HistoryPosition,
                compilerChanged = current.CompilerText != baseline.CompilerText,
                markerPresent = current.CompilerText.Contains("topologyMarker", StringComparison.Ordinal),
                generatedMatchesCompiler = current.Generated.Text == current.CompilerText,
                baselineSha256 = Hash(baseline.CompilerText), compilerSha256 = Hash(current.CompilerText), generatedSha256 = Hash(current.Generated.Text),
                compilation.Revision, compilation.GeneratedPath,
                compilerHasErrors = compilation.Result.HasErrors,
                compilerMessages = compilation.Result.Messages.Select(message => message.ToString()).ToArray(),
                compilation.ReferenceEvidence,
                compiledGraph = GraphCertificate((VisualScriptAsset)compilation.Item.Asset),
                liveGraph = GraphCertificate(producer.Asset),
                graph = producer.Asset.Methods.Select(value => new { value.Id, value.Name,
                    blocks = value.Blocks.Values.Select(block => new { block.Id, type = block.GetType().FullName,
                        slots = block.Slots.Select(slot => new { slot.Id, slot.Name, slot.Direction, slot.Kind, owner = slot.Owner?.Id }).ToArray() }).ToArray(),
                    links = value.Links.Values.Select(edge => new { edge.Id, source = edge.Source.Id, target = edge.Target.Id }).ToArray() }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        static void CertifyAliases(VisualScriptAsset asset)
        {
            foreach (var method in asset.Methods)
            {
                var slots = method.Blocks.Values.SelectMany(block => block.Slots).ToArray();
                foreach (var link in method.Links.Values)
                    Check(slots.Any(slot => ReferenceEquals(slot, link.Source)) && slots.Any(slot => ReferenceEquals(slot, link.Target))
                        && method.Blocks.Values.Any(block => ReferenceEquals(block, link.Source.Owner))
                        && method.Blocks.Values.Any(block => ReferenceEquals(block, link.Target.Owner)),
                        "Native serialized link endpoints must alias actual slots owned by the same restored method blocks");
            }
        }
        static object GraphCertificate(VisualScriptAsset asset) => new
        {
            assetId = asset.Id.ToString(),
            methods = asset.Methods.Select(value => new
            {
                value.Id, value.Name,
                blocks = value.Blocks.Values.Select(block => new
                {
                    block.Id, type = block.GetType().FullName, code = (block as CustomCodeBlock)?.Code,
                    outputExecution = (block as ExecutionBlock)?.OutputExecution?.Id,
                    slots = block.Slots.Select(slot => new { slot.Id, slot.Name, slot.Flags, slot.Direction, slot.Kind,
                        owner = slot.Owner?.Id, actualOwner = ReferenceEquals(slot.Owner, block) }).ToArray()
                }).ToArray(),
                links = value.Links.Values.Select(edge => new
                {
                    edge.Id, source = edge.Source?.Id, target = edge.Target?.Id,
                    sourceOwner = edge.Source?.Owner?.Id, targetOwner = edge.Target?.Owner?.Id,
                    sourceAliasesOwnedSlot = value.Blocks.Values.SelectMany(block => block.Slots).Any(slot => ReferenceEquals(slot, edge.Source)),
                    targetAliasesOwnedSlot = value.Blocks.Values.SelectMany(block => block.Slots).Any(slot => ReferenceEquals(slot, edge.Target)),
                    sourceOwnerAliasesBlock = value.Blocks.Values.Any(block => ReferenceEquals(block, edge.Source?.Owner)),
                    targetOwnerAliasesBlock = value.Blocks.Values.Any(block => ReferenceEquals(block, edge.Target?.Owner)),
                    sourceAliasesOutputExecution = value.Blocks.Values.OfType<ExecutionBlock>().Any(block => ReferenceEquals(block.OutputExecution, edge.Source))
                }).ToArray()
            }).ToArray()
        };
    }
}
