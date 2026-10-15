using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.Mathematics;
using Stride.Core.Yaml;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private string? visualScriptLegacyFixturePath;
    internal object PrepareParityVisualScriptLegacyBaseline()
    {
        if (!args.Contains("--ui-test") || visualScriptLegacyFixturePath == null || workspace == null || sessionHistory == null)
            throw new InvalidOperationException("Legacy generator baseline requires the exact loaded private fixture.");
        var item = workspace.AssetItems.Single(candidate => SamePath(candidate.FullPath.ToOSPath(), visualScriptLegacyFixturePath));
        if (item.Asset is not VisualScriptAsset || item.Package.Container is not SolutionProject project
            || !workspace.Session.Projects.Contains(project) || !ReferenceEquals(project.Package, item.Package)
            || !ReferenceEquals(item.Package.Session, workspace.Session) || item.Package.IsReadOnly)
            throw new InvalidOperationException("Legacy generator fixture lost actual native project/package ownership.");
        var bytes = File.ReadAllBytes(visualScriptLegacyFixturePath);
        var owner = GetProjectBuildSettingsDocument(project, sessionHistory);
        owner.ValidateProjectFileBaseline();
        using (sessionHistory.CreateTransaction("Prepare legacy generator fixture membership"))
            owner.ChangeGeneratedAssetMembership(item, true);
        owner.Save();
        File.WriteAllText(visualScriptLegacyFixturePath + ".membership-proof.json", JsonSerializer.Serialize(owner.InspectLegacyGeneratorMembership(item),
            new JsonSerializerOptions { WriteIndented = true }));
        owner.ValidateProjectAssetMembership(item, true); owner.VerifySavedProject();
        if (!bytes.SequenceEqual(File.ReadAllBytes(visualScriptLegacyFixturePath)))
            throw new InvalidOperationException("Project membership arrangement upgraded the legacy source file.");
        return new { fixtureArrangement = true, nativeProducerId = item.Id.ToString(), producerPath = visualScriptLegacyFixturePath,
            projectPath = owner.Path, nativeGeneratorMetadataAuthoredAndSaved = true, originalInlineSourceUnchanged = true,
            coverage = "Actual canonical project history/membership save before acceptance; not user template creation or source upgrade proof." };
    }
    internal void PrepareVisualScriptLegacyReferencesFixtureBeforeLoad()
    {
        if (!args.Contains("--ui-test") || workspace != null || visualScriptLegacyFixturePath != null)
            throw new InvalidOperationException("Legacy references require one private pre-load fixture.");
        var package = Path.GetFullPath(projectPathInput);
        var privateRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Stride-ImGui-UI")) + Path.DirectorySeparatorChar;
        if (!package.StartsWith(privateRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Legacy fixture must be private.");
        var path = Path.Combine(Path.GetDirectoryName(package)!, "Assets", "VisualScriptLegacy.sdvs");
        if (File.Exists(path)) throw new IOException("Legacy fixture destination is occupied.");
        var asset = new VisualScriptAsset { Namespace = "EditorParity" };
        var method = new Method("LegacyMethod"); asset.Methods.Add(method);
        var templates = VisualScriptBlockTemplate.Discover();
        var start = (FunctionStartBlock)templates.Single(template => template.Type == typeof(FunctionStartBlock)).Create();
        var code = (CustomCodeBlock)templates.Single(template => template.Type == typeof(CustomCodeBlock)).Create();
        code.Code = "int legacyCompatibilityMarker = 71;";
        start.GenerateSlots(start.Slots, new SlotGeneratorContext(null));
        code.GenerateSlots(code.Slots, new SlotGeneratorContext(null));
        method.Blocks.Add(start); method.Blocks.Add(code);
        method.Links.Add(new Link(start.OutputExecution, code.InputExecution));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Real native serializer without graph metadata reproduces the old
        // inline endpoint encoding; no handwritten YAML or projected graph.
        AssetFileSerializer.Save(path, asset, null, assetNamespace: "EditorParity");
        File.WriteAllBytes(path + ".original-inline", File.ReadAllBytes(path));
        using (var input = File.OpenRead(path))
        {
            var raw = (VisualScriptAsset)AssetYamlSerializer.Default.Deserialize(input);
            File.WriteAllText(path + ".raw-proof.json", JsonSerializer.Serialize(new
            {
                assetId = raw.Id.ToString(),
                methods = raw.Methods.Select(value => new { value.Id,
                    ownedSlots = value.Blocks.Values.SelectMany(block => block.Slots).Select(slot => new { slot.Id, owner = slot.Owner?.Id }).ToArray(),
                    links = value.Links.Values.Select(edge => new { edge.Id, source = edge.Source?.Id, target = edge.Target?.Id,
                        sourceOwner = edge.Source?.Owner?.Id, targetOwner = edge.Target?.Owner?.Id }).ToArray() }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        visualScriptLegacyFixturePath = path;
    }
    internal object VerifyVisualScriptLegacyReferences(string artifacts)
    {
        if (!args.Contains("--ui-test") || workspace == null || sessionHistory == null || visualScriptLegacyFixturePath == null)
            throw new InvalidOperationException("Load the prepared real private legacy project before verification.");
        var path = visualScriptLegacyFixturePath;
        var proof = Path.GetFullPath(artifacts); Directory.CreateDirectory(proof);
        var bytes = File.ReadAllBytes(path);
        AssetLoadResult<VisualScriptAsset> raw;
        using (var rawStream = File.OpenRead(path))
            raw = new((VisualScriptAsset)AssetYamlSerializer.Default.Deserialize(rawStream), null, false, new());
        var rawMethod = raw.Asset.Methods.Single();
        var rawLink = rawMethod.Links.Values.Single();
        Check(raw.YamlMetadata.RetrieveMetadata(AssetObjectSerializerBackend.ObjectReferencesKey) == null
            && !rawMethod.Blocks.Values.SelectMany(block => block.Slots).Any(slot => ReferenceEquals(slot, rawLink.Source)),
            "Native no-metadata fixture must actually reproduce duplicate inline legacy endpoints");
        var publicLoaded = AssetFileSerializer.Load<VisualScriptAsset>(path);
        var publicMethod = publicLoaded.Asset.Methods.Single(); var publicLink = publicMethod.Links.Values.Single();
        Check(publicMethod.Blocks.Values.SelectMany(block => block.Slots).Any(slot => ReferenceEquals(slot, publicLink.Source))
            && publicMethod.Blocks.Values.SelectMany(block => block.Slots).Any(slot => ReferenceEquals(slot, publicLink.Target))
            && publicLink.Source.Id == rawLink.Source.Id && publicLink.Target.Id == rawLink.Target.Id
            && File.ReadAllBytes(path).SequenceEqual(bytes),
            "Actual public native load callback restores aliases before duplicate-ID analysis without changing IDs or file bytes");
        var canonicalItem = workspace.Session.FindAsset(raw.Asset.Id)
            ?? throw new InvalidOperationException("Actual native legacy project asset is unavailable.");
        var nativeBuildItem = canonicalItem.Clone(keepPackage: true, newAsset: raw.Asset);
        var rawCompiled = raw.Asset.Compile(nativeBuildItem);
        Check(rawCompiled.GeneratedSource?.Contains("legacyCompatibilityMarker", StringComparison.Ordinal) == true
            && File.ReadAllBytes(path).SequenceEqual(bytes),
            "Actual engine compiler normalizes legacy inline endpoints without upgrading original source bytes");
        foreach (var kind in new[] { "missing", "foreign", "duplicate" })
        {
            var malformed = AssetFileSerializer.Load<VisualScriptAsset>(path).Asset;
            var local = malformed.Methods.Single(); var edge = local.Links.Values.Single();
            if (kind == "missing") edge.Source = new Slot(SlotDirection.Output, SlotKind.Execution);
            else if (kind == "foreign")
            {
                var foreign = new Method("Foreign"); malformed.Methods.Add(foreign);
                var foreignBlock = new FunctionStartBlock(); foreignBlock.GenerateSlots(foreignBlock.Slots, new SlotGeneratorContext(null));
                foreign.Blocks.Add(foreignBlock); edge.Source = foreignBlock.OutputExecution;
            }
            else
            {
                var block = local.Blocks.Values.First();
                block.Slots.Add(new Slot { Id = block.Slots[0].Id });
            }
            // Fixture arrangement: new foreign/duplicate collections need the
            // same native stable item metadata as a normally loaded asset.
            // Establish it before capturing the immutable rejection baseline.
            AssetCollectionItemIdHelper.GenerateMissingItemIds(malformed);
            var malformedItem = canonicalItem.Clone(keepPackage: true, newAsset: malformed);
            var beforeSource = edge.Source; var beforeTarget = edge.Target;
            var beforeState = State(malformed);
            using var before = new MemoryStream(); AssetFileSerializer.Save(before, malformed, null);
            string? rejection = null;
            try { malformed.Compile(malformedItem); }
            catch (InvalidDataException error) { rejection = error.ToString(); }
            using var after = new MemoryStream(); AssetFileSerializer.Save(after, malformed, null);
            var beforeBytes = before.ToArray(); var afterBytes = after.ToArray();
            File.WriteAllBytes(Path.Combine(proof, kind + "-before.sdvs"), beforeBytes);
            File.WriteAllBytes(Path.Combine(proof, kind + "-after.sdvs"), afterBytes);
            File.WriteAllText(Path.Combine(proof, kind + "-rejection.json"), JsonSerializer.Serialize(new
            {
                kind, rejection, beforeState, afterState = State(malformed),
                beforeSha256 = Convert.ToHexString(SHA256.HashData(beforeBytes)), afterSha256 = Convert.ToHexString(SHA256.HashData(afterBytes)),
                equalBytes = beforeBytes.SequenceEqual(afterBytes), sourceSameReference = ReferenceEquals(edge.Source, beforeSource),
                targetSameReference = ReferenceEquals(edge.Target, beforeTarget), sourceBefore = beforeSource?.Id, sourceAfter = edge.Source?.Id,
                targetBefore = beforeTarget?.Id, targetAfter = edge.Target?.Id
            }, new JsonSerializerOptions { WriteIndented = true }));
            Check(rejection != null, "Malformed legacy compile must reject invalid endpoints");
            Check(before.ToArray().SequenceEqual(after.ToArray()) && ReferenceEquals(edge.Source, beforeSource) && ReferenceEquals(edge.Target, beforeTarget),
                "Malformed legacy normalization must reject all endpoints before any graph mutation");
            Check(JsonSerializer.Serialize(beforeState) == JsonSerializer.Serialize(State(malformed)),
                "Malformed legacy compile must preserve exact native graph state");
            using var invalidInput = new MemoryStream(before.ToArray(), false);
            try { AssetFileSerializer.Load<VisualScriptAsset>(invalidInput, new Stride.Core.IO.UFile(path)); throw new InvalidOperationException("Malformed public native load was accepted."); }
            catch (InvalidDataException) { }
        }
        var previous = documents.Active;
        var beforeHistory = sessionHistory.History.Position;
        var transactionIds = sessionHistory.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id).ToArray();
        var document = (VisualScriptDocument)GetCanonicalDocument(path, () => new VisualScriptDocument(path, sessionHistory, false));
        var entry = documents.Find(document) ?? documents.Add(document);
        InitializeVisualScriptDocument(document);
        var method = document.Asset.Methods.Single();
        var link = method.Links.Values.Single();
        Check(method.Blocks.Values.SelectMany(block => block.Slots).Any(slot => ReferenceEquals(slot, link.Source))
            && method.Blocks.Values.SelectMany(block => block.Slots).Any(slot => ReferenceEquals(slot, link.Target))
            && method.Blocks.Values.Any(block => ReferenceEquals(block, link.Source.Owner))
            && method.Blocks.Values.Any(block => ReferenceEquals(block, link.Target.Owner)),
            "Actual canonical document load normalizes native legacy endpoint aliases");
        Check(File.ReadAllBytes(path).SequenceEqual(bytes) && !document.IsDirty && sessionHistory.History.Position == beforeHistory
            && transactionIds.SequenceEqual(sessionHistory.Stack.RetrieveAllTransactions().Select(transaction => transaction.Id)),
            "Load-time compatibility must preserve original file bytes, clean state and shared authored history");
        var compilation = document.CaptureCompilation(GetVisualScriptCompilerOwner(document).Item);
        File.WriteAllText(Path.Combine(proof, "legacy-compiler-preassert.txt"), compilation.Result.GeneratedSource ?? "", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(proof, "legacy-compiler-preassert.json"), JsonSerializer.Serialize(new
        {
            compilation.ObjectReferenceCount, markerPresent = compilation.Result.GeneratedSource?.Contains("legacyCompatibilityMarker", StringComparison.Ordinal) == true,
            compilation.Revision, document.EditRevision, compilation.ReferenceEvidence,
            compilerHasErrors = compilation.Result.HasErrors, compilerMessages = compilation.Result.Messages.Select(message => message.ToString()).ToArray(),
            actualGraphDefinition = document.AuthoringGraph.Definition.GetType().FullName,
            originalBytesUnchanged = File.ReadAllBytes(path).SequenceEqual(bytes), document.IsDirty,
            historyPosition = sessionHistory.History.Position
        }, new JsonSerializerOptions { WriteIndented = true }));
        Check(compilation.ObjectReferenceCount == 2 && compilation.Result.GeneratedSource?.Contains("legacyCompatibilityMarker", StringComparison.Ordinal) == true,
            "Actual serialized native compiler reaches normalized legacy code through two endpoint references");
        File.WriteAllBytes(Path.Combine(proof, "original-inline.sdvs"), bytes);
        File.WriteAllText(Path.Combine(proof, "native-compiler.txt"), compilation.Result.GeneratedSource!, new UTF8Encoding(false));
        document.Save();
        var saved = AssetFileSerializer.Load<VisualScriptAsset>(path);
        var savedMethod = saved.Asset.Methods.Single(); var savedLink = savedMethod.Links.Values.Single();
        Check(saved.YamlMetadata.RetrieveMetadata(AssetObjectSerializerBackend.ObjectReferencesKey)?.Count == 2
            && saved.Asset.Id == document.Asset.Id && savedMethod.Id == method.Id && savedLink.Id == link.Id
            && savedMethod.Blocks.Values.SelectMany(block => block.Slots).Any(slot => ReferenceEquals(slot, savedLink.Source))
            && savedMethod.Blocks.Values.SelectMany(block => block.Slots).Any(slot => ReferenceEquals(slot, savedLink.Target)),
            "Only explicit native Save upgrades encoding while independent reload retains actual endpoint identities and aliases");
        Check(!document.IsDirty, "Explicit legacy save leaves canonical owner clean");
        File.WriteAllBytes(Path.Combine(proof, "saved-references.sdvs"), File.ReadAllBytes(path));
        CloseDocument(entry);
        if (previous != null && documents.Entries.Contains(previous)) ActivateDocument(previous);
        var evidence = new { scenario = "visualscript.legacy-inline-references", passed = true, assetId = document.Asset.Id.ToString(),
            method.Id, link = link.Id, source = link.Source.Id, target = link.Target.Id,
            loadPreservedBytesAndHistory = true, compilerReachedLegacyCode = true, savedEndpointReferenceCount = 2,
            missingForeignDuplicateRejections = true,
            actualPublicLoadCallbackPreservedIds = true,
            coverage = "Real native inline serializer fixture, canonical document load, native metadata/fixup normalization, serialized compiler and explicit native save/independent reload. No UI gestures or arbitrary malformed-asset repair claim." };
        File.WriteAllText(Path.Combine(proof, "legacy-references.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        return evidence;
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static object State(VisualScriptAsset asset) => new
        {
            assetId = asset.Id.ToString(),
            methods = asset.Methods.Select(method => new { method.Id, method.Name,
                blocks = method.Blocks.Values.Select(block => new { block.Id, type = block.GetType().FullName,
                    slots = block.Slots.Select(slot => new { slot.Id, slot.Name, slot.Kind, slot.Direction, slot.Flags, slot.Type, slot.Value, owner = slot.Owner?.Id }).ToArray() }).ToArray(),
                links = method.Links.Values.Select(link => new { link.Id, source = link.Source?.Id, target = link.Target?.Id,
                    sourceOwner = link.Source?.Owner?.Id, targetOwner = link.Target?.Owner?.Id }).ToArray() }).ToArray()
        };
    }
}

internal sealed partial class ProjectBuildSettingsDocument
{
    internal object InspectLegacyGeneratorMembership(AssetItem item)
    {
        var include = item.GetProjectInclude(); var generated = item.GetGeneratedInclude();
        return new { Path, include, generated, IsDirty,
            sources = new[] { "None", "AdditionalFiles" }.Select(type => new { type,
                included = properties.IsProjectItemIncluded(type, include),
                generator = properties.GetProjectItemMetadata(type, include, "Generator"),
                expectedGenerator = ((IProjectFileGeneratorAsset)item.Asset).Generator,
                lastGenOutput = properties.GetProjectItemMetadata(type, include, "LastGenOutput"),
                expectedLastGenOutput = System.IO.Path.GetFileName(generated) }).ToArray(),
            compiled = new { included = properties.IsProjectItemIncluded("Compile", generated),
                dependentUpon = properties.GetProjectItemMetadata("Compile", generated, "DependentUpon"), expectedDependentUpon = System.IO.Path.GetFileName(include),
                autoGen = properties.GetProjectItemMetadata("Compile", generated, "AutoGen"),
                designTime = properties.GetProjectItemMetadata("Compile", generated, "DesignTime"),
                sharedInput = properties.GetProjectItemMetadata("Compile", generated, "DesignTimeSharedInput") },
            authoredItems = properties.AuthoredCompileItems, actualXml = properties.Snapshot };
    }
}
