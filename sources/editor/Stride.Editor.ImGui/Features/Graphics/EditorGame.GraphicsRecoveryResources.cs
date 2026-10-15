using Stride.Core.Serialization.Contents;
using Stride.Engine;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Skyboxes;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private void ReleaseEditorGraphicsForRecovery()
    {
        EnsureSceneCpuRetirementCompleted();
        EnsureEditorGpuRetirementCompleted();
        ReleaseDrainedScriptGlyphImages();
        var state = editorGraphicsRecovery!;
        if (state.ReleasePosition == 0)
        {
            // Native detached presenters must stop submitting before their shared device.
            platformViewports?.Dispose(); platformViewports = null;
            state.ReleasePosition = 1;
        }
        if (state.ReleasePosition == 1)
        {
            ReleaseSceneDocumentGraphicsForRecovery(state);
            ReleaseUIEditorsGraphicsResourcesForRecovery();
            engineViewport?.Dispose(); engineViewport = null;
            texturePreview?.Dispose(); texturePreview = null;
            modelPreview?.Dispose(); modelPreview = null;
            materialPreview?.Dispose(); materialPreview = null;
            animationPreview?.Dispose(); animationPreview = null;
            skyboxPreview?.Dispose(); skyboxPreview = null;
            spriteSheetPreview?.Dispose(); spriteSheetPreview = null;
            fontPreview?.Dispose(); fontPreview = null;
            CloseCatalogAssetPreviews(true);
            foreach (var canvas in spriteCanvases.Values)
            {
                canvas.Preview?.Dispose(); canvas.Preview = null;
                // CPU decoded pixels and tool state remain; the existing guarded decoder
                // recreates only the native source image after recovery.
                canvas.SourceRevision = -1;
            }
            state.ReleasePosition = 2;
        }
        if (state.ReleasePosition == 2)
        {
            ClearRetiredEditorPreviews();
            if (retiredEditorPreviews.Count != 0) throw new PreviewResourceDrainPendingException();
            ClearAssetThumbnails();
            while (retiredSkyboxResources.Count != 0)
            { retiredSkyboxResources[0].Resource.Dispose(); retiredSkyboxResources.RemoveAt(0); }
            foreach (var owner in retiredImportContentOwners.ToArray())
            {
                if (Volatile.Read(ref owner.PreviewLeases) != 0)
                    throw new InvalidOperationException("Imported content still has a retained preview lease.");
                if (KeepGraphicsRecoveryBundle(owner.Bundle, state)) owner.ReleaseGraphicsResourcesForRecovery();
                else { owner.Dispose(); retiredImportContentOwners.Remove(owner); }
            }
            foreach (var retired in retiredFonts.ToArray())
            {
                if (Volatile.Read(ref retired.Owner.Leases) != 0)
                    throw new InvalidOperationException("A compiled font still has a retained preview lease.");
                retired.Owner.Content.Dispose(); retiredFonts.Remove(retired);
            }
            foreach (var retired in retiredSpriteSheets.ToArray())
            {
                if (KeepGraphicsRecoveryBundle(retired.Content.Bundle, state)) retired.Content.ReleaseGraphicsResourcesForRecovery();
                else { retired.Content.Dispose(); retiredSpriteSheets.Remove(retired); }
            }
            while (retiredSceneTextures.Count != 0)
            { var retired = retiredSceneTextures[0]; ReleaseSceneTextureContent(retired.Cache, retired.Texture); retiredSceneTextures.RemoveAt(0); }
            foreach (var retired in retiredModels.ToArray())
            {
                if (KeepGraphicsRecoveryBundle(retired.Bundle, state) && !state.RetainedModelUrls.ContainsKey(retired.Model))
                    state.RetainedModelUrls.Add(retired.Model, Stride.Core.Serialization.AttachedReferenceManager.GetAttachedReference(retired.Model)?.Url
                        ?? throw new InvalidOperationException("A retained model has no content URL."));
                if (!state.Released.Contains(retired.Model)) { retired.Cache.Unload(retired.Model); state.Released.Add(retired.Model); }
                if (!KeepGraphicsRecoveryBundle(retired.Bundle, state)) { retired.Bundle.Dispose(); retiredModels.Remove(retired); }
            }
            foreach (var retired in retiredTextureContent.ToArray())
                if (!KeepGraphicsRecoveryBundle(retired.Content, state)) { retired.Content.Dispose(); retiredTextureContent.Remove(retired); }
            foreach (var retired in retiredCompositorContent.ToArray())
                if (!KeepGraphicsRecoveryBundle(retired.Bundle, state)) { retired.Bundle.Dispose(); retiredCompositorContent.Remove(retired); }
            state.ReleasePosition = 3;
        }
        if (state.ReleasePosition == 3)
        {
            // Retain actual installed bundle mounts/content IDs and compiler generations.
            // Only their native object caches are released; no saved/dirty marker changes.
            foreach (var owner in importedContentOwners.Values.Distinct()) owner.ReleaseGraphicsResourcesForRecovery();
            foreach (var installed in refreshedFonts.Values.Distinct())
            {
                if (Volatile.Read(ref installed.Leases) != 0)
                    throw new InvalidOperationException("A compiled font still has an active resource lease.");
                installed.Content.ReleaseGraphicsResourcesForRecovery();
            }
            foreach (var installed in refreshedSpriteSheets.Values.Distinct()) installed.Content.ReleaseGraphicsResourcesForRecovery();
            foreach (var installed in installedSkyboxes.Values)
                if (!state.Released.Contains(installed.Skybox))
                { installed.Cache.Unload(installed.Skybox); state.Released.Add(installed.Skybox); }
            foreach (var resource in refreshedModels)
                if (!state.Released.Contains(resource.Model))
                { resource.Cache.Unload(resource.Model); state.Released.Add(resource.Model); }
            foreach (var resource in refreshedSceneTextures)
                if (!state.Released.Contains(resource.Value.Texture))
                { ReleaseSceneTextureGraphicsForRecovery(resource.Value.Cache, resource.Value.Texture); state.Released.Add(resource.Value.Texture); }
            state.ReleasePosition = 4;
        }
        if (state.ReleasePosition == 4)
        {
            if (!TryReleaseEngineGraphicsResourcesForRecovery())
                throw new InvalidOperationException("Engine streaming is still draining for graphics recovery.");
            icons?.Dispose(); icons = null;
            ClearTemplateImages();
            renderer?.ReleaseDeviceTextures(Hexa.NET.ImGui.ImGui.GetDrawData());
            renderer?.Dispose(); renderer = null;
            state.ReleasePosition = 5;
        }
    }

    private void RebuildEditorGraphicsAfterRecovery()
    {
        var state = editorGraphicsRecovery!;
        if (state.RebindPosition == 0)
        {
            if (GraphicsDevice.Features.RequestedProfile != state.Profile || GraphicsDevice.ColorSpace != state.ColorSpace)
                throw new NotSupportedException("The replacement graphics device requires recompiling the installed content for different rendering capabilities.");
            RebindEngineGraphicsResourcesAfterRecovery();
            renderer ??= new ImGuiRenderer(GraphicsDevice);
            icons ??= new EditorIcons(GraphicsDevice, renderer);
            state.RebindPosition = 1;
        }
        if (state.RebindPosition == 1)
        {
            foreach (var owner in importedContentOwners.Values.Concat(retiredImportContentOwners).Distinct()) owner.RebindGraphicsResourcesAfterRecovery();
            foreach (var installed in refreshedFonts.Values.Distinct()) installed.Content.RebindGraphicsResourcesAfterRecovery();
            foreach (var installed in refreshedSpriteSheets.Values.Distinct()) installed.Content.RebindGraphicsResourcesAfterRecovery();
            foreach (var retired in retiredSpriteSheets) retired.Content.RebindGraphicsResourcesAfterRecovery();
            foreach (var entry in installedSkyboxes.ToArray())
            {
                if (!state.Released.Contains(entry.Value.Skybox)) continue;
                var cache = entry.Value.Content.CreateObjectCache();
                var skybox = cache.Load<Skybox>(entry.Value.Url, ContentManagerLoaderSettings.StreamingDisabled);
                installedSkyboxes[entry.Key] = entry.Value with { Cache = cache, Skybox = skybox };
            }
            for (var index = 0; index < refreshedModels.Count; index++)
            {
                var previous = refreshedModels[index];
                if (!state.Released.Contains(previous.Model)) continue;
                var cache = previous.Bundle.CreateObjectCache();
                var model = cache.Load<Model>(previous.Url, ContentManagerLoaderSettings.StreamingDisabled);
                refreshedModels[index] = (previous.Bundle, cache, model, previous.Url);
            }
            for (var index = 0; index < retiredModels.Count; index++)
            {
                var previous = retiredModels[index];
                if (!state.Released.Contains(previous.Model)) continue;
                var cache = previous.Bundle.CreateObjectCache();
                // Its actual content identity supplies the URL; no fabricated replacement model.
                var url = state.RetainedModelUrls[previous.Model];
                var model = cache.Load<Model>(url, ContentManagerLoaderSettings.StreamingDisabled);
                retiredModels[index] = (previous.Bundle, cache, model, previous.Frame);
            }
            foreach (var entry in refreshedSceneTextures.ToArray())
            {
                if (!state.Released.Contains(entry.Value.Texture)) continue;
                refreshedSceneTextures[entry.Key] = RebindSceneTextureGraphicsAfterRecovery(entry.Key, entry.Value.Texture);
            }
            fontContentEpoch++; spriteCompiledContentEpoch++;
            foreach (var owner in importedContentOwners.Values.Distinct()) owner.Generation = ++importedContentGeneration;
            state.RebindPosition = 2;
        }
        if (state.RebindPosition == 2)
        {
            if (!RestoreSceneDocumentGraphicsRecovery(state)) RestoreGraphicsRecoveryScene(state);
            RebindUIEditorsGraphicsResourcesAfterRecovery();
            RestoreGraphicsRecoveryPreview(state);
            state.RebindPosition = 3;
        }
        if (state.RebindPosition == 3)
        {
            if (state.PlatformWindows && platformViewports == null)
                platformViewports = new WindowsImGuiViewports(GraphicsDevice, Window.NativeWindow.Handle, message => statusMessage = message, ApplyViewportStyle, PlatformViewportPlacementFile,
                    syntheticInput: args.Contains("--native-document-groups-test"));
            ClearSceneSynchronizationFailure();
            state.RebindPosition = 4;
        }
    }

    private bool KeepGraphicsRecoveryBundle(CompiledProjectContent bundle, GraphicsRecoveryState state)
        => KeepSceneDocumentGraphicsRecoveryBundle(bundle, state)
            || sceneTextureContentOwners.Values.Any(owner => ReferenceEquals(owner.Content, bundle))
            || ReferenceEquals(materialCompiledContent, bundle) || ReferenceEquals(state.PreviewBundle, bundle)
            || ReferenceEquals(state.AnimationBundle, bundle) || ReferenceEquals(state.SceneBundle, bundle);

    private void RestoreGraphicsRecoveryScene(GraphicsRecoveryState state)
    {
        if (engineViewport != null) return;
        var next = new EngineViewport(this, renderer!)
        {
            ResolveChildSceneDocument = ResolveSceneHierarchyDocument, ResolveSceneWorldOffset = GetSceneHierarchyOffset,
            SceneLoaded = IsSceneLoaded, SceneLocked = IsSceneLocked, EntityLocked = IsHierarchyEntityLocked,
            ScenePresentationVersion = () => scenePresentationVersion,
        };
        try
        {
            if (state.PresentedDocument != null && workspace != null && state.SceneBundle != null)
            {
                next.Load(state.PresentedDocument, workspace, state.SceneBundle);
                if (state.Width > 0 && state.Height > 0) next.Resize(state.Width, state.Height);
                engineViewport = next;
                RestoreRefreshedSceneModels(document);
                foreach (var owner in importedContentOwners.Values.OrderBy(owner => owner.Generation))
                    next.ReplaceRuntimeImportedContent(owner.Values, document);
                foreach (var resource in refreshedSceneTextures) next.ReplaceRuntimeTexture(resource.Key, resource.Value.Texture, document);
                foreach (var installed in refreshedSpriteSheets) next.ReplaceRuntimeSpriteSheet(installed.Key, installed.Value.Content.Sheet, document);
                foreach (var installed in refreshedFonts) next.ReplaceRuntimeFont(installed.Key, installed.Value.Content.Font, document);
                foreach (var installed in installedSkyboxes.Values) next.ReplaceRuntimeSkybox(installed.Url, installed.Skybox, document);
                if (state.Camera != null)
                {
                    next.Camera.Restore(state.Camera); next.ShowCameraPreview = state.Camera.CameraPreview;
                    next.Grid?.ApplySettings(state.Camera.Grid ?? SceneGridSettings.Default);
                }
                next.ShowSelection = showSceneSelection;
                if (document != null) next.Synchronize(document);
            }
            engineViewport = next;
            UpdatePickingOptions();
        }
        catch { engineViewport = null; next.Dispose(); throw; }
    }
}
