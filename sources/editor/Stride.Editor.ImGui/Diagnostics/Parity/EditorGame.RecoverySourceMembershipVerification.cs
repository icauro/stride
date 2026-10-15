using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stride.Core.Assets;

namespace Stride.Editor.ImGui;

internal sealed record RecoverySourceVerificationReceipt(EditorRecoveryOpenDocument Descriptor, string DiskSha256,
    string TextSha256, int CodePage, string ItemType, string[] Frameworks, RecoveryExternalLinkedSourceProof? PhysicalLinkProof = null);

internal sealed record RecoveryExternalLinkedSourceProof(string ProjectPath, string PhysicalPath, string CompileInclude,
    string LinkVirtual, string NativeAlternativePath, string CanonicalAssetId, string DiskSha256,
    bool OutsideProjectDirectory, bool InsideOwnedContainer);

internal sealed partial class EditorGame
{
    internal RecoverySourceVerificationReceipt[] PrepareRecoverySourceMembershipVerification(
        string projectPath, string includedPath, string linkedPath)
    {
        RequireRecoverySourceVerificationHost();
        var previous = documents.Active;
        var result = new List<RecoverySourceVerificationReceipt>();
        foreach (var path in new[] { includedPath, linkedPath })
        {
            var canonical = Path.GetFullPath(path);
            var item = workspace!.AssetItems.SingleOrDefault(candidate => SamePath(candidate.FullPath.ToOSPath(), canonical));
            var project = workspace.Projects.SingleOrDefault(owner => SamePath(owner.FullPath.ToOSPath(), projectPath))
                ?? throw new InvalidOperationException("The recovery source fixture project is not loaded.");
            if (item != null && (item.Asset is not SourceCodeAsset || !ReferenceEquals(item.Package.Container, project)))
                throw new InvalidOperationException("The recovery source fixture belongs to another native project asset owner.");
            var requested = new EditorRecoveryOpenDocument(canonical,
                item == null ? typeof(ScriptSourceDocument).FullName! : typeof(AssetScriptSourceDocument).FullName!,
                item?.Id.ToString(), item?.Package.FullPath.ToOSPath(), item?.Package.Container?.AssetNamespace ?? item?.Package.AssetNamespace,
                project.FullPath.ToOSPath());
            _ = ValidateRecoverySourceMembership(requested); // Never open an arbitrary external .cs file.
            var originalHash = RecoverySourceDiskHash(canonical);
            var entry = OpenRecoverySourcePresentation(requested);
            var source = entry.Document as ScriptSourceDocument ?? throw new InvalidOperationException("Actual source owner is not canonical.");
            if (source.IsDirty || !ReferenceEquals(source.SessionHistory, sessionHistory) || entry.State == null
                || !ReferenceEquals(entry.State.Document, source) || entry.State.AssetId != item?.Id)
                throw new InvalidOperationException("Recovery source is not a clean native owner on the current authoring/history generation.");
            var descriptor = CaptureRecoveryWorkspace().Documents.Single(owner => SamePath(owner.Path, canonical));
            var proof = ValidateRecoverySourceMembership(descriptor);
            if (originalHash != RecoverySourceDiskHash(canonical)) throw new InvalidOperationException("Opening recovery source changed its disk bytes.");
            result.Add(new(descriptor, originalHash, RecoverySourceTextHash(source.Text), source.CopySourceEncoding().CodePage,
                proof.ItemType, proof.Frameworks, VerifyRecoveryExternalLinkedSource(proof)));
        }
        if (previous != null) { ActivateDocument(previous); DocumentGroupPanel.Model.Select(previous); }
        return result.ToArray();
    }

    internal object VerifyRecoverySourceMembershipRejections(RecoverySourceVerificationReceipt included, string excludedPath)
    {
        RequireRecoverySourceVerificationHost();
        var canonical = Path.GetFullPath(excludedPath);
        var current = workspace!;
        var excludedItem = current.AssetItems.SingleOrDefault(item => SamePath(item.FullPath.ToOSPath(), canonical));
        var excluded = new EditorRecoveryOpenDocument(canonical,
            excludedItem == null ? typeof(ScriptSourceDocument).FullName! : typeof(AssetScriptSourceDocument).FullName!,
            excludedItem?.Id.ToString(), excludedItem?.Package.FullPath.ToOSPath(),
            excludedItem?.Package.Container?.AssetNamespace ?? excludedItem?.Package.AssetNamespace, included.Descriptor.SourceProjectPath);
        var rejected = new List<object>();
        AssertRejected(excluded, "excluded", "excludedCompile");
        AssertRejected(included.Descriptor with { SourceProjectPath = included.Descriptor.SourceProjectPath + ".not-loaded.csproj" },
            "project owner", "wrongLoadedProject");
        AssertRejected(included.Descriptor with { AssetId = Guid.NewGuid().ToString() }, "canonical package asset", "wrongCanonicalAsset");
        return new { rejected, actualQueuePreflight = true, noDocumentOrHistoryOrLayoutMutation = true };

        void AssertRejected(EditorRecoveryOpenDocument bad, string expectedReason, string caseName)
        {
            if (pendingRecoveryWorkspaceRestore != null || pendingRecoveryWorkspaceCompensation != null)
                throw new InvalidOperationException("Source rejection proof requires no pending native recovery transition.");
            var before = CaptureRecoveryWorkspace();
            var originalEntries = documents.Entries.Select(entry => (Entry: entry, entry.Document, entry.State, Generation: entry.State?.Generation)).ToArray();
            var originalActive = documents.Active;
            var originalHistory = sessionHistory!;
            var history = originalHistory.History;
            var sourceHash = RecoverySourceDiskHash(bad.Path);
            var beforeJson = JsonSerializer.Serialize(before);
            var old = included.Descriptor.Path;
            var addsDocument = !SamePath(bad.Path, old);
            var targetGroup = before.Groups.Groups.Single(group => group.Paths.Any(path => SamePath(path, old))).Id;
            var attempt = before with
            {
                // Excluded-source proof adds one candidate, retaining EVERY actual open owner.
                // Same-path identity proofs replace metadata only; current membership is intact.
                Documents = addsDocument ? before.Documents.Append(bad).ToArray()
                    : before.Documents.Select(owner => SamePath(owner.Path, old) ? bad : owner).ToArray(),
                Groups = before.Groups with { Groups = before.Groups.Groups.Select(group => group with
                {
                    Paths = addsDocument && group.Id == targetGroup ? group.Paths.Append(bad.Path).ToArray() : group.Paths.ToArray()
                }).ToArray() }
            };
            string? reason = null;
            try { QueueRecoveryWorkspaceRestore(attempt); }
            catch (InvalidOperationException error) { reason = error.Message; }
            if (reason == null || !reason.Contains(expectedReason, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Recovery preflight did not reject the actual source authority case: " + caseName + "; " + reason);
            var afterHistory = originalHistory.History;
            if (pendingRecoveryWorkspaceRestore != null || pendingRecoveryWorkspaceCompensation != null
                || !ReferenceEquals(workspace, current) || !ReferenceEquals(sessionHistory, originalHistory)
                || !ReferenceEquals(documents.Active, originalActive) || documents.Entries.Count != originalEntries.Length
                || originalEntries.Any(owner => !documents.Entries.Contains(owner.Entry)
                    || !ReferenceEquals(owner.Entry.Document, owner.Document) || !ReferenceEquals(owner.Entry.State, owner.State)
                    || owner.Entry.State?.Generation != owner.Generation)
                || history.Position != afterHistory.Position || !history.Entries.SequenceEqual(afterHistory.Entries)
                || beforeJson != JsonSerializer.Serialize(CaptureRecoveryWorkspace()) || sourceHash != RecoverySourceDiskHash(bad.Path))
                throw new InvalidOperationException("Rejected source recovery mutated native owners, history, layout or source disk.");
            rejected.Add(new { caseName, reason });
        }
    }

    internal object VerifyRestoredRecoverySourceMembership(RecoverySourceVerificationReceipt[] expected,
        string? dirtyPath = null, string? dirtyText = null, string? dirtyCurrentDiskSha256 = null)
    {
        RequireRecoverySourceVerificationHost();
        var evidence = new List<object>();
        foreach (var receipt in expected)
        {
            var descriptor = CaptureRecoveryWorkspace().Documents.Single(owner => SamePath(owner.Path, receipt.Descriptor.Path));
            if (descriptor != receipt.Descriptor) throw new InvalidOperationException("Recovered canonical source identity differs from the producer.");
            var proof = ValidateRecoverySourceMembership(descriptor);
            var physicalLinkProof = VerifyRecoveryExternalLinkedSource(proof);
            if (physicalLinkProof != receipt.PhysicalLinkProof)
                throw new InvalidOperationException("Recovered external linked source changed its actual evaluated include, Link or native physical asset owner.");
            var entry = documents.Find(descriptor.Path) ?? throw new InvalidOperationException("Recovered actual source owner missing.");
            var source = entry.Document as ScriptSourceDocument ?? throw new InvalidOperationException("Recovered source lost canonical source ownership.");
            var dirty = dirtyPath != null && SamePath(dirtyPath, source.Path);
            if (dirty && (dirtyText == null || dirtyCurrentDiskSha256 == null))
                throw new InvalidOperationException("Dirty source proof requires exact expected authored text and current external disk hash.");
            if (source.IsDirty != dirty || !ReferenceEquals(source.SessionHistory, sessionHistory)
                || !ReferenceEquals(entry.State?.Document, source) || entry.State?.AssetId?.ToString() != descriptor.AssetId
                || !SamePath(source.ProjectPath!, descriptor.SourceProjectPath!)
                || (dirty ? dirtyCurrentDiskSha256 : receipt.DiskSha256) != RecoverySourceDiskHash(source.Path)
                || receipt.DiskSha256 != Convert.ToHexString(source.DiskContentHash.Span)
                || (dirty ? RecoverySourceTextHash(dirtyText!) : receipt.TextSha256) != RecoverySourceTextHash(source.Text)
                || receipt.CodePage != source.CopySourceEncoding().CodePage
                || receipt.ItemType != proof.ItemType || !receipt.Frameworks.SequenceEqual(proof.Frameworks))
                throw new InvalidOperationException("Recovered source changed native owner/history, text/encoding, clean disk or evaluated membership.");
            evidence.Add(new { source.Path, descriptor.AssetId, source.ProjectPath, clean = !source.IsDirty,
                generation = entry.State!.Generation, currentHistoryIdentity = true, proof, physicalLinkProof });
        }
        return new { sources = evidence, originalSourceBaselinesRetained = true, expectedExternalConflictPath = dirtyPath };
    }

    internal ScriptSourceDocument? FindRecoverySourceVerificationDocument(string path)
    {
        RequireRecoverySourceVerificationHost();
        return documents.Find(Path.GetFullPath(path))?.Document as ScriptSourceDocument;
    }

    internal unsafe (Guid EntryId, bool InputActive, System.Numerics.Vector2? Center)? InspectRecoverySourceInput(string path)
    {
        RequireRecoverySourceVerificationHost();
        var entry = documents.Find(Path.GetFullPath(path));
        if (entry?.Document is not ScriptSourceDocument source) return null;
        var group = DocumentGroupPanel.Model.Find(entry);
        if (group == null || !scriptEditors.TryGetValue(source, out var editor)) return (entry.Id, false, null);
        var window = Hexa.NET.ImGui.ImGuiP.FindWindowByName(group.WindowName);
        var center = CaptureScriptPresentationAuthority(source)() && window.Handle != null
            ? editor.InspectRecoverySourceSurface(window.ID) : null;
        return (entry.Id, editor.InputActive, center);
    }

    internal unsafe object InspectRecoverySourceReadiness(string path)
    {
        RequireRecoverySourceVerificationHost();
        var entry = documents.Find(Path.GetFullPath(path));
        var source = entry?.Document as ScriptSourceDocument;
        var group = entry == null ? null : DocumentGroupPanel.Model.Find(entry);
        var owner = source == null ? null : authoring?.Find(source.Path);
        var window = group == null ? default : Hexa.NET.ImGui.ImGuiP.FindWindowByName(group.WindowName);
        var editor = source != null && scriptEditors.TryGetValue(source, out var retained) ? retained : null;
        var context = Hexa.NET.ImGui.ImGui.GetCurrentContext();
        var io = Hexa.NET.ImGui.ImGui.GetIO();
        var controlId = entry == null ? null : "document-tab/" + entry.Id;
        var hasControlCenter = controlId != null && Controls.Centers.TryGetValue(controlId, out _);
        var controlCenter = hasControlCenter ? Controls.Centers[controlId!] : default;
        var tabs = group == null ? null : DocumentGroupPanel.InspectNativeTabs(group.Id);
        return new
        {
            path, entryId = entry?.Id, groupId = group?.Id,
            globallySelected = entry != null && ReferenceEquals(documents.Active, entry),
            groupSelected = entry != null && ReferenceEquals(group?.Active, entry),
            activeEntryId = documents.Active?.Id, groupActiveEntryId = group?.Active?.Id,
            canStartAssetEdit = CanStartAssetEdit,
            presentationAuthority = source != null && CaptureScriptPresentationAuthority(source)(),
            documentKind = source?.GetType().FullName, sourceProjectPath = source?.ProjectPath,
            sourceVersion = source?.Version, canSave = source?.CanSave, canEditText = source?.CanEditText,
            stateGeneration = entry?.State?.Generation, published = owner?.IsPublished, disposed = owner?.IsDisposed,
            entryIsCanonicalOwner = entry != null && ReferenceEquals(entry.State, owner),
            editorCreated = editor != null,
            nativeGroupWindowId = window.Handle == null ? (uint?)null : window.ID,
            nativeGroupActive = window.Handle != null && window.Active,
            nativeGroupWasActive = window.Handle != null && window.WasActive,
            nativeGroupHidden = window.Handle != null && window.Hidden,
            nativeInput = new
            {
                frame = context.FrameCount, activeId = context.ActiveId,
                activeIdWindow = context.ActiveIdWindow.Handle == null ? (uint?)null : context.ActiveIdWindow.ID,
                activeIdAllowOverlap = context.ActiveIdAllowOverlap,
                hoveredId = context.HoveredId,
                hoveredWindow = context.HoveredWindow.Handle == null ? (uint?)null : context.HoveredWindow.ID,
                navWindow = context.NavWindow.Handle == null ? (uint?)null : context.NavWindow.ID,
                mouseX = io.MousePos.X, mouseY = io.MousePos.Y, leftDown = io.MouseDown[0], rightDown = io.MouseDown[1],
                keyCtrl = io.KeyCtrl, mouseHoveredViewport = io.MouseHoveredViewport
            },
            nativeTabs = tabs,
            nativeTabGeometry = entry == null || tabs == null ? null : InspectRecoveryNativeTabGeometry(entry.Id, tabs),
            tabControl = new { controlId, hasControlCenter,
                centerX = hasControlCenter ? (float?)controlCenter.X : null,
                centerY = hasControlCenter ? (float?)controlCenter.Y : null,
                viewportId = controlId == null ? 0 : Controls.Viewports.GetValueOrDefault(controlId) },
            surface = editor?.InspectRecoverySourceSurfaceReadiness(window.Handle == null ? 0 : window.ID)
        };
    }

    private static unsafe object? InspectRecoveryNativeTabGeometry(Guid entryId, object tabs)
    {
        using var inspection = JsonDocument.Parse(JsonSerializer.Serialize(tabs));
        var barId = inspection.RootElement.GetProperty("currentBarId").GetUInt32();
        var item = inspection.RootElement.GetProperty("documentTabs").EnumerateArray()
            .FirstOrDefault(candidate => candidate.GetProperty("document").GetGuid() == entryId);
        if (item.ValueKind != JsonValueKind.Object) return null;
        var tabId = item.GetProperty("nativeTab").GetUInt32();
        var bars = Hexa.NET.ImGui.ImGui.GetCurrentContext().TabBars.Buf;
        for (var index = 0; index < bars.Size; index++)
        {
            var bar = new Hexa.NET.ImGui.ImGuiTabBarPtr(bars.Data + index);
            if (bar.ID != barId) continue;
            var tab = Hexa.NET.ImGui.ImGuiP.TabBarFindTabByID(bar, tabId);
            if (tab.IsNull) return null;
            var rect = bar.BarRect;
            return new { barId, tabId, barFlags = bar.Flags.ToString(), tabFlags = tab.Flags.ToString(),
                selectedTabId = bar.SelectedTabId, visibleTabId = bar.VisibleTabId, nextSelectedTabId = bar.NextSelectedTabId,
                wantLayout = bar.WantLayout, barLastFrameVisible = bar.CurrFrameVisible,
                tabLastFrameVisible = tab.LastFrameVisible, tabLastFrameSelected = tab.LastFrameSelected,
                offset = tab.Offset, width = tab.Width, contentWidth = tab.ContentWidth,
                barMinX = rect.Min.X, barMinY = rect.Min.Y, barMaxX = rect.Max.X, barMaxY = rect.Max.Y,
                scrollingAnim = bar.ScrollingAnim, scrollingTarget = bar.ScrollingTarget,
                scrollingRectMinX = bar.ScrollingRectMinX, scrollingRectMaxX = bar.ScrollingRectMaxX };
        }
        return null;
    }

    private void RequireRecoverySourceVerificationHost()
    {
        if (!args.Contains("--ui-test") || !args.Contains("--workspace-recovery-fixture") || workspace == null
            || sessionHistory == null || projectLoading != null)
            throw new InvalidOperationException("Actual recovery source proofs require the loaded guarded private recovery fixture.");
    }
    private static string RecoverySourceDiskHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string RecoverySourceTextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private RecoveryExternalLinkedSourceProof? VerifyRecoveryExternalLinkedSource(RecoverySourceMembershipProof proof)
    {
        var current = workspace ?? throw new InvalidOperationException("External linked proof requires a loaded native workspace.");
        var markerPath = Path.Combine(Path.GetDirectoryName(current.OpenPath)!, ".workspace-recovery-fixture.json");
        using var marker = JsonDocument.Parse(File.ReadAllBytes(markerPath));
        var data = marker.RootElement;
        if (data.GetProperty("version").GetInt32() != 2
            || !data.TryGetProperty("externalLinkedSource", out var externalLinked) || externalLinked.ValueKind != JsonValueKind.True
            || !SamePath(proof.SourcePath, data.GetProperty("linkedSourcePath").GetString()!)) return null;
        // Recheck the exact guarded physical container before any native project query.
        _ = WorkspaceRecoveryFixtureGuard.Resolve(current.OpenPath, args);
        var root = data.GetProperty("ownedRoot").GetString()!;
        var projectDirectory = Path.GetDirectoryName(proof.ProjectPath)!;
        var physical = Path.GetFullPath(proof.SourcePath);
        if (physical.StartsWith(projectDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !physical.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("External linked proof requires a real sibling source outside the project directory, inside the owned container.");
        var item = current.AssetItems.SingleOrDefault(owner => SamePath(owner.FullPath.ToOSPath(), physical));
        if (item == null || item.Asset is not SourceCodeAsset || item.Package is not { } sourcePackage
            || sourcePackage.Container is not SolutionProject ownerProject
            || !ReferenceEquals(sourcePackage, ownerProject.Package)
            || !current.Session.Projects.Contains(ownerProject)
            || !SamePath(ownerProject.FullPath.ToOSPath(), proof.ProjectPath) || item.AlternativePath is not { } alternativePath
            || !SamePath(alternativePath.ToOSPath(), physical) || proof.CanonicalAssetId != item.Id.ToString())
            throw new InvalidOperationException("External linked source must retain the actual native asset owner and real AlternativePath.");
        foreach (var framework in proof.Frameworks)
        {
            var properties = string.IsNullOrEmpty(framework) ? null : new Dictionary<string, string> { ["TargetFramework"] = framework };
            var evaluated = VSProjectHelper.LoadProject(proof.ProjectPath, proof.Configuration, extraProperties: properties);
            try
            {
                var matches = evaluated.GetItems("Compile").Where(source => SamePath(
                    Path.GetFullPath(source.EvaluatedInclude, projectDirectory), physical)).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("External linked source requires one actual evaluated Compile item.");
                var include = matches[0].EvaluatedInclude.Replace('\\', '/');
                var link = matches[0].GetMetadataValue("Link").Replace('\\', '/');
                if (include != data.GetProperty("linkInclude").GetString() || link != data.GetProperty("linkVirtual").GetString())
                    throw new InvalidOperationException("Actual evaluated external Compile Include or Link differs from the declared private fixture.");
                if (!ReferenceEquals(workspace, current) || !current.Session.Projects.Contains(ownerProject)
                    || !current.AssetItems.Contains(item) || !ReferenceEquals(item.Package, sourcePackage))
                    throw new InvalidOperationException("External linked source native workspace or asset owner changed during evaluation.");
                return new(proof.ProjectPath, physical, include, link, alternativePath.ToOSPath(), item.Id.ToString(),
                    RecoverySourceDiskHash(physical), true, true);
            }
            finally { evaluated.ProjectCollection.UnloadAllProjects(); evaluated.ProjectCollection.Dispose(); }
        }
        throw new InvalidOperationException("External linked proof has no native evaluated framework.");
    }
}

internal sealed unsafe partial class ScriptTextEditor
{
    internal object InspectRecoverySourceSurfaceReadiness(uint parentId)
    {
        var candidates = new List<object>();
        var windows = Hexa.NET.ImGui.ImGui.GetCurrentContext().Windows;
        for (var index = 0; index < windows.Size && candidates.Count < 32; index++)
        {
            var child = windows[index];
            if (child.ParentWindow.Handle == null || child.ParentWindow.ID != parentId) continue;
            var seed = child.IDStack.Size == 0 ? child.ID : child.IDStack[child.IDStack.Size - 1];
            var clip = child.InnerClipRect;
            candidates.Add(new
            {
                id = child.ID, childId = child.ChildId, parentWindowId = child.ParentWindow.ID,
                active = child.Active, wasActive = child.WasActive, hidden = child.Hidden,
                lastFrameActive = child.LastFrameActive,
                computedSurfaceId = Hexa.NET.ImGui.ImGuiP.GetIDWithSeed("##SourceSurface", (byte*)null, child.ID),
                currentSeed = seed,
                computedCurrentSeedSurfaceId = Hexa.NET.ImGui.ImGuiP.GetIDWithSeed("##SourceSurface", (byte*)null, seed),
                minX = clip.Min.X, minY = clip.Min.Y, maxX = clip.Max.X, maxY = clip.Max.Y
            });
        }
        return new { sourceSurfaceId, sourcePath, inputActive = InputActive, sourceFocused, sourceHadFocus,
            presentationMayEdit = CanEditPresentation?.Invoke(), parentId, candidates };
    }

    internal System.Numerics.Vector2? InspectRecoverySourceSurface(uint parentId)
    {
        if (sourceSurfaceId == 0) return null;
        var windows = Hexa.NET.ImGui.ImGui.GetCurrentContext().Windows;
        for (var index = 0; index < windows.Size; index++)
        {
            var child = windows[index];
            if ((!child.Active && !child.WasActive) || child.Hidden || child.ParentWindow.Handle == null
                || child.ParentWindow.ID != parentId
                || Hexa.NET.ImGui.ImGuiP.GetIDWithSeed("##SourceSurface", (byte*)null, child.ID) != sourceSurfaceId) continue;
            var clip = child.InnerClipRect;
            if (clip.Max.X > clip.Min.X && clip.Max.Y > clip.Min.Y) return (clip.Min + clip.Max) / 2;
        }
        return null;
    }
}
