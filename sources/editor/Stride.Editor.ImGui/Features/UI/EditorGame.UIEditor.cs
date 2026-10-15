using Hexa.NET.ImGui;
using Stride.Assets;
using Stride.UI.Assets;
using Stride.Assets.Presentation.SceneEditor;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.IO;
using Stride.Games;
using Stride.Graphics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private readonly Dictionary<UIDocument, UIEditorPanel> ownedUIEditors = [];
    private UIEditorPanel? ownedUIEditor => documents.Active?.Document is UIDocument source
        ? ownedUIEditors.GetValueOrDefault(source) : null;
    private UIEditorPanel GetUIEditor(UIDocument source)
    {
        if (!ownedUIEditors.TryGetValue(source, out var panel))
            ownedUIEditors.Add(source, panel = new(new UIEditorHost(this, source)));
        return panel;
    }
    private void DrawUiDocument(UIDocument source)
    {
        if (IsEditorGraphicsRecovering || IsEditorGpuRetirementPending || IsEditorGpuRetirementFinalizing)
        { if (ownedUIEditors.TryGetValue(source, out var suspended)) suspended.ResetVisibility(); return; }
        using var referenceOwners = BeginContentReferenceOwnerScope(source);
        GetUIEditor(source).Draw(source);
    }
    private void ResetUIEditorVisibility() { foreach (var panel in ownedUIEditors.Values) panel.ResetVisibility(); }
    private void RenderUIEditor(GameTime time)
    {
        if (IsEditorGraphicsRecovering || IsEditorGpuRetirementPending || IsEditorGpuRetirementFinalizing) return;
        foreach (var panel in ownedUIEditors.Values) panel.Render(time);
    }
    private void ProcessUIEditorRequests() { foreach (var panel in ownedUIEditors.Values.ToArray()) panel.ProcessRequests(); }
    private bool TryUIEditorClipboardCommand(string command) => documents.Active?.Document is UIDocument
        && !IsCurrentAssetPropertyContext && ownedUIEditor?.ExecuteClipboardCommand(command) == true;
    private bool TryUIEditorCommand(string command) => documents.Active?.Document is UIDocument source
        && !IsCurrentAssetPropertyContext && ReferenceEquals(ownedUIEditor?.Document, source)
        && ownedUIEditor?.ExecuteUICommand(command) == true;
    private void CloseUIEditor()
    {
        List<Exception>? failures = null;
        foreach (var pair in ownedUIEditors.ToArray())
        {
            try { pair.Value.Dispose(); ownedUIEditors.Remove(pair.Key); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures != null) throw new AggregateException(failures);
    }
    private void ReleaseUIEditorsGraphicsResourcesForRecovery()
    { foreach (var panel in ownedUIEditors.Values) panel.ReleaseGraphicsResourcesForRecovery(); }
    private void RebindUIEditorsGraphicsResourcesAfterRecovery()
    { foreach (var panel in ownedUIEditors.Values) panel.RebindGraphicsResourcesAfterRecovery(); }
    private void ReleaseUIEditorDocument(UIDocument source)
    {
        if (ownedUIEditors.TryGetValue(source, out var panel))
        {
            panel.ReleaseDocumentView(source);
            ownedUIEditors.Remove(source);
        }
        selectionHistory.UnregisterUI(source.Asset.Id);
    }
    private bool TryDrawUIElementProperties()
    {
        if (ownedUIEditor?.Document is not { } source || !ReferenceEquals(documents.Active?.Document, source)
            || IsCurrentAssetPropertyContext) return false;
        using var referenceOwners = BeginContentReferenceOwnerScope(source);
        if (ownedUIEditor.Selection.Count > 0 && !ownedUIEditor.ShowingAssetProperties) Execute(ownedUIEditor.DrawProperties);
        else DrawAssetMetadataDocument(source, inPropertyGrid: true);
        return true;
    }
    private sealed class UIEditorHost(EditorGame editor, UIDocument presentedSource) : IUIEditorHost
    {
        public bool IsCurrentPresentationOwner(UIDocument source)
            => ReferenceEquals(source, presentedSource) && editor.documents.Find(source) is { } entry
                && editor.IsCurrentDocumentPresentation(entry);
        public bool AskBeforeDeletingUIElements => editor.preferences is not { IsBlocked: false } preferences
            || preferences.Current.AskBeforeDeletingUIElements;
        public string UIElementDeletionTitle => WpfMessageDialogTitle("DeleteUIElementsConfirmation");
        public bool DrawConfirmationButton(string label) => editor.DrawNativeButton(label);
        public bool DrawConfirmationCheckbox(string label, ref bool value) => editor.DrawNativeCheckbox(label, ref value);
        public void DisableUIElementDeletionConfirmation()
        {
            var preferences = editor.preferences ?? throw new InvalidOperationException("Editor preferences are unavailable.");
            var previous = preferences.Current.AskBeforeDeletingUIElements;
            preferences.Current.AskBeforeDeletingUIElements = false;
            if (preferences.Save()) return;
            preferences.Current.AskBeforeDeletingUIElements = previous;
            throw new InvalidOperationException(preferences.Error ?? "Editor preferences could not be saved.");
        }
        private readonly object cameraCaptureOwner = new();
        public bool BeginCameraCapture() => !editor.IsEditorGraphicsRecovering && !editor.IsEditorGpuRetirementPending
            && !editor.IsEditorGpuRetirementFinalizing && editor.platformViewports?.BeginCameraCapture(Gui.GetWindowViewport().ID, cameraCaptureOwner) == true;
        public System.Numerics.Vector2? ConsumeCameraMotion() => editor.platformViewports?.IsCameraCaptureOwned(cameraCaptureOwner) == true
            ? editor.platformViewports.ConsumeCameraMotion(cameraCaptureOwner) : null;
        public void EndCameraCapture() => editor.platformViewports?.EndCameraCapture(cameraCaptureOwner);
        public UICameraView? LoadCameraView(UIDocument source) => editor.LoadNativeUICameraView(source);
        public void SaveCameraView(UIDocument source, UICameraView value) => editor.SaveNativeUICameraView(source, value);
        public bool DrawUIPropertiesButton() => editor.DrawNativeButton(EditorLocalization.Widget("UI properties", "Button"), new System.Numerics.Vector2(-1, 0));
        public bool DrawHierarchyExpansionButton(string icon, string tooltip) => editor.icons!.Button(icon, tooltip, 16);
        public (float Speed, float SceneUnit) CameraMotionSettings(UIDocument source)
        {
            var item = editor.workspace?.Session.FindAsset(source.Asset.Id);
            var values = item?.Package.UserSettings.GetValue(PackageSceneSettings.SceneSettings);
            var settings = values != null && values.TryGetValue(source.Asset.Id, out var saved) ? saved : SceneSettingsData.CreateDefault();
            return (settings.CamMoveSpeed, settings.SceneUnit <= Stride.Core.Mathematics.MathUtil.ZeroTolerance ? 1 : settings.SceneUnit);
        }
        private sealed class ResourceVersion { public long Imported = -1, Font = -1, Generation; }
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIDocument, ResourceVersion> resourceVersions = new();
        public IReadOnlyList<UIElementFactory> Factories
        {
            get
            {
                if (editor.workspace == null) return [];
                var result = new List<UIElementFactory>();
                foreach (var item in editor.workspace.Session.Packages.SelectMany(package => package.Assets).Where(item => item.Asset is UILibraryAsset))
                {
                    var path = item.FullPath.ToOSPath();
                    var library = (UIDocument)editor.GetCanonicalDocument(path, () => new UIDocument(path, editor.sessionHistory!, item.Package.IsReadOnly));
                    foreach (var element in ((UILibraryAsset)library.Asset).PublicUIElements)
                        result.Add(new UIElementFactory(item.Package.IsReadOnly ? library.Find(element.Key)?.GetType().Name ?? element.Value : element.Value,
                            library, element.Key, item.Location.ToString(), item.Package.IsReadOnly));
                }
                return result.OrderBy(factory => factory.Category, StringComparer.CurrentCulture)
                    .ThenBy(factory => factory.Name, StringComparer.CurrentCulture).ToArray();
            }
        }
        public UIEditorViewport CreateViewport(UIDocument source)
        {
            var item = editor.workspace!.FindAsset(source.Path);
            var colorSpace = item.Package.GetGameSettingsAssetOrDefault().Defaults.OfType<RenderingSettings>().FirstOrDefault()?.ColorSpace ?? ColorSpace.Linear;
            source.ResolveLibraries(id =>
            {
                var library = editor.workspace.AssetItems.FirstOrDefault(item => item.Id == id && item.Asset is UILibraryAsset);
                return library == null ? null : (UIDocument)editor.GetCanonicalDocument(library.FullPath.ToOSPath(),
                    () => new UIDocument(library.FullPath.ToOSPath(), editor.sessionHistory!, library.Package.IsReadOnly));
            });
            var lease = editor.AcquireImportedContent(item.Id);
            try { return new UIEditorViewport(editor, editor.renderer!, lease?.Bundle ?? editor.projectContent ?? throw new InvalidOperationException("The project has not been compiled."), colorSpace, lease, (type, url) =>
            {
                if (!typeof(Stride.Graphics.SpriteFont).IsAssignableFrom(type)) return null;
                var fontLease = editor.AcquireCompiledFont(url);
                return fontLease == null ? null : new UIEditorResourceLease(fontLease.Font, fontLease);
            }, Stride.Core.Assets.PackageTypeScope.For(item.Package)); }
            catch { lease?.Dispose(); throw; }
        }
        public long ContentGeneration(UIDocument source)
        {
            var version = resourceVersions.GetValue(source, static _ => new ResourceVersion());
            var imported = editor.GetImportedContentGeneration(source.Asset.Id); var font = editor.GetCompiledFontGeneration();
            if (version.Imported != imported || version.Font != font)
            { version.Imported = imported; version.Font = font; version.Generation++; }
            return version.Generation;
        }
        public UIDocument? ResolveLibrary(AssetId id)
        {
            var item = editor.workspace?.Session.FindAsset(id);
            if (item?.Asset is not UILibraryAsset) return null;
            return item == null ? null : (UIDocument)editor.GetCanonicalDocument(item.FullPath.ToOSPath(), () => new UIDocument(item.FullPath.ToOSPath(), editor.sessionHistory!, item.Package.IsReadOnly));
        }
        public void SelectionChanged()
        {
            editor.NotifyNativeUISelection(presentedSource);
            if (ReferenceEquals(editor.documents.Active?.Document, presentedSource)) editor.ClearAssetPropertyContext();
        }
        public void SelectionAdded(UIDocument source) => editor.NotifyNativeUISelection(source, true);
        public bool CanEditHierarchyOwner(UIDocument source)
        {
            if (!ReferenceEquals(source, presentedSource) || !ReferenceEquals(editor.documents.Active?.Document, source)
                || !editor.CanStartAssetEdit || !source.CanSave || !ReferenceEquals(source.SessionHistory, editor.sessionHistory)) return false;
            var owner = editor.authoring!.Find(source.Asset.Id);
            var item = editor.workspace!.Session.FindAsset(source.Asset.Id);
            return owner is { IsPublished: true, IsDisposed: false } && ReferenceEquals(owner.Document, source)
                && item != null && editor.IsCurrentEditableAssetPackage(item.Package);
        }
        public UIHierarchyDragOwner? AcquireHierarchyDragOwner(UIDocument source)
            => CanEditHierarchyOwner(source) ? new(editor.authoring!.Find(source.Asset.Id)!) : null;
        public UIHierarchyDragOwner? AcquireHierarchyCommandOwner(UIDocument source, bool writable)
        {
            if (writable) return AcquireHierarchyDragOwner(source);
            var owner = editor.authoring?.Find(source.Asset.Id);
            return owner is { IsPublished: true, IsDisposed: false } && ReferenceEquals(owner.Document, source)
                && editor.workspace?.Session.FindAsset(source.Asset.Id) != null ? new(owner) : null;
        }
        public bool DrawRenameTreeNode(string label, ImGuiTreeNodeFlags flags, out bool virtualized)
            => editor.DrawNativeTreeNode(label, flags | ImGuiTreeNodeFlags.Framed, true, out virtualized, caption: "Mg", paint: false);
        public UIHierarchyDragOwner? AcquireFactoryLibraryOwner(UIElementFactory factory)
        {
            var owner = editor.authoring?.Find(factory.Library.Asset.Id);
            var item = editor.workspace?.Session.FindAsset(factory.Library.Asset.Id);
            return owner is { IsPublished: true, IsDisposed: false } && ReferenceEquals(owner.Document, factory.Library)
                && item?.Asset is UILibraryAsset && IsCurrentFactoryDefinition(factory) ? new(owner) : null;
        }
        public bool IsCurrentFactoryDefinition(UIElementFactory factory)
        {
            var item = editor.workspace?.Session.FindAsset(factory.Library.Asset.Id);
            return item?.Asset is UILibraryAsset && item.Package.IsReadOnly == factory.SystemLibrary
                && StringComparer.Ordinal.Equals(item.Location.ToString(), factory.Url);
        }
        public bool DrawFactoryRow(string label, bool selected) => editor.DrawNativeSelectable(label, selected);
        public bool DrawFactoryCategoryGroup(string identity, string caption)
        {
            var bold = editor.PushPackagePropertiesBoldFont(true);
            var label = editor.CanDrawNativeWidgetText ? identity : caption + "###" + identity;
            try { return editor.DrawNativeTreeNode(label, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth,
                false, out _, caption); }
            finally { if (bold) Gui.PopFont(); }
        }
        public void ValidateHierarchyEdits() => editor.BeforeNativeSelectionChange();
        public void DrawHierarchyDragText(string text) => editor.DrawNativeLabel(text);
        public bool DrawViewSettingsButton() => editor.icons!.Button("scene-toolbar-view", null, 24);
        public bool DrawZoomButton(string icon, string tooltip)
        {
            var pressed = editor.icons!.Button(icon, tooltip, 16);
            if (editor.Controls.Enabled) editor.Controls.Record("ui/zoom/" + presentedSource.Asset.Id + "/" + icon);
            return pressed;
        }
        public void DrawViewLabel(string text, bool bold = false)
        {
            var pushed = editor.PushPackagePropertiesBoldFont(bold);
            try { editor.DrawNativeLabel(text); }
            finally { if (pushed) Gui.PopFont(); }
        }
        public string? LibraryName(AssetId id)
            => editor.workspace?.AssetItems.FirstOrDefault(item => item.Id == id && item.Asset is UILibraryAsset)?.Location.GetFileName();
        public bool DrawElementTreeNode(string label, string name, string type, string? library, int children, bool activeRoot,
            ImGuiTreeNodeFlags flags, bool forceSubmission, out bool virtualized)
            => editor.DrawUIElementTreeNode(label, name, type, library, children, activeRoot, flags, forceSubmission, out virtualized);
        public bool BeginMenu(string label, bool enabled = true) => editor.BeginNativeMenu(label, enabled);
        public bool DrawMenuItem(string label, string? shortcut = null, bool selected = false, bool enabled = true)
            => editor.DrawNativeMenuItem(label, shortcut, selected, enabled);
        public bool DrawTreeNode(string label, ImGuiTreeNodeFlags flags, bool forceSubmission, out bool virtualized)
            => editor.DrawNativeTreeNode(label, flags, forceSubmission, out virtualized, caption: label);
        public void ReleaseViewport(UIEditorViewport viewport) => editor.RetireEditorPreview(viewport);
        public void RegisterCaptureItem(string id, string kind, string name) => editor.RegisterVisualCaptureItem(id, kind, name);
        public void RecordDiagnosticControl(string id) { if (editor.Controls.Enabled) editor.Controls.Record(id); }
        public void RecordDiagnosticScrollArea(string id, System.Numerics.Vector2 center)
        { if (editor.Controls.Enabled) editor.Controls.Centers[id] = center; }
        public void DrawSelectableText(string id, string text) => editor.DrawSelectableText(id, text);
        public void DrawStringProperty(UIDocument source, Guid[] ids, string path, bool mixed)
            => editor.DrawUIPropertyString(source, ids, path, mixed);
        public void DrawRetainedStringProperty(UIDocument source, object identity, bool mixed, Func<bool> canEdit,
            Func<object?[]> read, Action<string> commit, Func<IDisposable>? acquireTarget = null)
            => editor.DrawRetainedUIString(source, identity, mixed, canEdit, read, commit, acquireTarget);
        public void DrawRetainedNumericProperty(UIDocument source, object identity, Type type, object? value, bool mixed,
            Func<bool> canEdit, Func<object?[]> read, Action<object?> commit,
            Func<PropertyNumericRange?> range, Func<IReadOnlyList<PropertyNumericRange?>> ranges,
            Func<IDisposable>? acquireTarget = null, Func<object?, object?>? formatValue = null)
            => editor.DrawRetainedUINumeric(source, identity, type, value, mixed, canEdit, read, commit, range, ranges, acquireTarget, formatValue);
        public void DrawRetainedAngleProperty(UIDocument source, object identity, Func<bool> canEdit,
            Func<object?[]> read, Action<Stride.Core.Mathematics.AngleSingle> commit, int valueCount,
            Func<IDisposable>? acquireTarget = null)
            => editor.DrawRetainedUIAngle(source, identity, canEdit, read, commit, valueCount, acquireTarget);
        public void DrawRetainedGuidProperty(UIDocument source, object identity, Func<bool> canEdit,
            Func<object?[]> read, Action<Guid> commit, int valueCount, Func<IDisposable>? acquireTarget = null)
            => editor.DrawRetainedUIGuid(source, identity, canEdit, read, commit, valueCount, acquireTarget);
        public void DrawRetainedTemporalProperty(UIDocument source, object identity, Type type, Func<bool> canEdit,
            Func<object?[]> read, Action<Func<object?, object?>> transform, int valueCount, Func<IDisposable>? acquireTarget = null)
            => editor.DrawRetainedUITemporal(source, identity, type, canEdit, read, transform, valueCount, acquireTarget);
        public void DrawRetainedPathProperty(UIDocument source, object identity, Type type, Func<bool> canEdit,
            Func<object?[]> read, Action<object?> commit, int valueCount, Func<IDisposable>? acquireTarget = null)
            => editor.DrawRetainedUIPath(source, identity, type, canEdit, read, commit, valueCount, acquireTarget);
        public void DrawRetainedCharacterProperty(UIDocument source, object identity, Func<bool> canEdit,
            Func<object?[]> read, Action<char> commit, int valueCount, Func<IDisposable>? acquireTarget = null)
            => editor.DrawRetainedUICharacter(source, identity, canEdit, read, commit, valueCount, acquireTarget);
        public void DrawRetainedColorProperty(UIDocument source, object identity, Type type, Func<bool> canEdit,
            Func<object?[]> read, Action<Func<object?, object?>> transform, int valueCount,
            Func<int, bool>? canEditAxis = null, Func<IDisposable>? acquireTarget = null)
            => editor.DrawRetainedUIColor(source, identity, type, canEdit, read, transform, valueCount, canEditAxis, acquireTarget);
        public void DrawReference(Type type, object? value, Action<object?> commit, bool mixed = false, bool allowsNull = true) => editor.DrawContentReferenceValue(type, value, allowsNull, commit, mixed);
        public void DrawReferenceFetchActions(IEnumerable<object?> values) => editor.DrawContentReferenceFetchActions(values);
        public bool DrawNullablePresence(bool present, out bool create) => editor.DrawNativeNullablePresence(present, out create);
        public bool DrawPrimitive(Type type, object? value, string label, bool canEdit, Action<object?> commit, Action endEdit)
            => editor.sessionHistory?.Extensions?.TryDrawPrimitive(new(type, value, label, canEdit, commit, endEdit)) == true;
        public void DrawDateTime(UIDocument document, DateTime value, bool mixed, bool canEdit, Action<object?> commit)
            => editor.DrawNativeScalarTemplate(document, typeof(DateTime), value, mixed, canEdit, commit);
        public bool DrawEnumImages(UIDocument document, Type type, object? value, bool mixed, Action<object?> commit)
        {
            if (editor.icons?.HasEnumImages(type) != true) return false;
            if (NativeEnumImageControl.Draw(editor.icons, document, type, value, mixed,
                Enum.GetValues(type).Cast<Enum>(), UiScale(1), out var updated)) commit(updated);
            return true;
        }
        public void DrawCombinedVirtualCommands(UIDocument document, IReadOnlyList<Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter> presenters)
            => editor.DrawUIVirtualProviderCommands(document, presenters);
        public void DrawUICollectionProviderCommands(UIDocument source, IReadOnlyList<Guid> ids, IReadOnlyList<UIPropertyCollection> collections)
            => editor.DrawUICollectionProviderCommands(source, ids, collections);
        public void DrawUICollectionItemProviderCommands(UIDocument source, IReadOnlyList<Guid> ids, IReadOnlyList<UIPropertyCollectionItem> items)
            => editor.DrawUICollectionItemProviderCommands(source, ids, items);
        public void DrawUIAttachedProviderCommands(UIDocument source, IReadOnlyList<Guid> ids, Stride.Core.PropertyKey key)
            => editor.DrawUIAttachedProviderCommands(source, ids, key);
        public void SelectUICollectionAddFactoryEntry(UIDocument document, IReadOnlyList<UIPropertyCollection> collections, Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry entry, string? key)
        {
            editor.Execute(() =>
            {
                if (!NativeAbstractEntries.Contains(NativeAbstractEntries.Combine(collections.Select(document.UICollectionEntries).ToArray()), entry)
                    || collections.Any(collection => !document.CanAddCollectionItem(collection) || !NativeAbstractEntries.Contains(document.UICollectionEntries(collection), entry))) throw new InvalidOperationException("The native collection entry is no longer available.");
                if (collections.All(collection => collection.Collection.Descriptor is not Stride.Core.Reflection.DictionaryDescriptor))
                {
                    var target = document.AcquireUICollectionAddTarget(collections); var transferred = false;
                    try
                    {
                        var command = target == null ? null : Stride.Core.Presentation.Quantum.Presenters.NodePresenterCombination.Commands(target.Presenters).FirstOrDefault(command => command.Name == "AddNewItem");
                        if (command != null)
                        {
                            if (target!.Presenters.Any(presenter => presenter.IsReadOnly)) throw new InvalidOperationException("The native collection is read-only.");
                            editor.StartEditorExtensionCommand(document, target!, command, entry); transferred = true; return;
                        }
                    }
                    finally { if (!transferred) target?.Dispose(); }
                }
                document.BatchCollectionEdits(() =>
                { foreach (var collection in collections.DistinctBy(collection => collection.Collection)) document.AddUICollectionEntry(collection, entry, key); }, "Add UI collection item");
            });
        }
        public void AddDefaultUICollectionItems(UIDocument document, IReadOnlyList<UIPropertyCollection> collections)
        {
            editor.Execute(() =>
            {
                editor.ValidatePendingPropertyStrings(); document.CommitEdits();
                if (!CanEditHierarchyOwner(document) || collections.Count == 0 || !collections.All(document.CanDefaultUICollectionAdd))
                    throw new InvalidOperationException("The native default UI collection command is no longer available.");
                var target = document.AcquireUICollectionAddTarget(collections); var transferred = false;
                try
                {
                    var command = target == null ? null : Stride.Core.Presentation.Quantum.Presenters.NodePresenterCombination.Commands(target.Presenters)
                        .FirstOrDefault(command => command.Name == "AddNewItem");
                    if (command != null)
                    {
                        if (target!.Presenters.Any(presenter => presenter.IsReadOnly)) throw new InvalidOperationException("The native collection is read-only.");
                        editor.StartEditorExtensionCommand(document, target!, command, null); transferred = true; return;
                    }
                    document.BatchCollectionEdits(() =>
                    {
                        foreach (var collection in collections.DistinctBy(collection => collection.Collection)) document.AddDefaultUICollectionItem(collection);
                    }, "Add UI collection item");
                }
                finally
                {
                    if (!transferred && target != null)
                        try { target.Dispose(); } catch { editor.packageCommandCleanup.Add(target.Dispose); throw; }
                }
            });
        }
        public void DrawRetainedUIDictionaryKey(UIDocument document, IReadOnlyList<Guid> ids, IReadOnlyList<UIPropertyCollectionItem> items)
            => editor.DrawRetainedUIDictionaryKey(document, ids, items, () => CanEditHierarchyOwner(document));
        public void DrawUIDictionaryDefaultAdd(UIDocument document, IReadOnlyList<UIPropertyCollection> collections)
            => editor.DrawUIDictionaryDefaultAdd(document, collections, () => CanEditHierarchyOwner(document));
        public void SelectUICollectionItemFactoryEntry(UIDocument document, IReadOnlyList<UIPropertyCollectionItem> items, Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry entry)
        {
            editor.Execute(() =>
            {
                if (document.CommonUICollectionItemEntries(items) is not { } entries || !NativeAbstractEntries.Contains(entries, entry)
                    || items.Any(item => !document.CanEditCollectionItem(item)) || !document.CanSelectUICollectionItemEntry(items, entry)) throw new InvalidOperationException("The native UI collection entry is no longer available.");
                var target = document.AcquireUICollectionFactoryTarget(items); var transferred = false;
                try
                {
                    var command = target == null ? null : Stride.Core.Presentation.Quantum.Presenters.NodePresenterCombination.Commands(target.Presenters).FirstOrDefault(command => command.Name == "CreateNewInstance");
                    if (command != null)
                    {
                        if (target!.Presenters.Any(presenter => presenter.IsReadOnly)) throw new InvalidOperationException("The native item is read-only.");
                        editor.StartEditorExtensionCommand(document, target!, command, entry); transferred = true; return;
                    }
                }
                finally { if (!transferred) target?.Dispose(); }
                document.ChangeUICollectionItemEntry(items, entry);
            });
        }
        public void SelectUIVirtualFactoryEntry(UIDocument document, IReadOnlyList<Stride.Core.Presentation.Quantum.Presenters.VirtualNodePresenter> presenters, Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry entry)
        {
            editor.Execute(() =>
            {
                if (document.CommonUIVirtualEntries(presenters) is not { } entries || !NativeAbstractEntries.Contains(entries, entry)
                    || presenters.Any(presenter => !document.CanEditExtensionVirtualProperty(presenter)) || !document.CanSelectUIVirtualEntry(presenters, entry)) throw new InvalidOperationException("The native UI entry is no longer available.");
                var target = document.AcquireCombinedVirtualTarget(presenters); var transferred = false;
                try
                {
                    var command = Stride.Core.Presentation.Quantum.Presenters.NodePresenterCombination.Commands(target.Presenters).FirstOrDefault(command => command.Name == "CreateNewInstance");
                    if (command != null) { editor.StartEditorExtensionCommand(document, target, command, entry); transferred = true; return; }
                }
                finally { if (!transferred) target.Dispose(); }
                document.ChangeUIVirtualEntry(presenters, entry);
            });
        }
        public void SelectUIAttachedFactoryEntry(UIDocument document, IReadOnlyList<Guid> ids, Stride.Core.PropertyKey key, Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry entry)
        {
            editor.Execute(() =>
            {
                if (ids.Any(id => !document.CanChangeDependencyProperty(id, key))) throw new InvalidOperationException("The attached UI property cannot be edited.");
                if (document.CommonUIAttachedEntries(ids, key) is not { } entries || !NativeAbstractEntries.Contains(entries, entry) || !document.CanSelectUIAttachedEntry(ids, key, entry)) throw new InvalidOperationException("The native attached entry is no longer available.");
                var target = document.AcquireAttachedFactoryTarget(ids, key); var transferred = false;
                try
                {
                    var command = target == null ? null : Stride.Core.Presentation.Quantum.Presenters.NodePresenterCombination.Commands(target.Presenters).FirstOrDefault(command => command.Name == "CreateNewInstance");
                    if (command != null)
                    {
                        if (target!.Presenters.Any(presenter => presenter.IsReadOnly)) throw new InvalidOperationException("The native property is read-only.");
                        editor.StartEditorExtensionCommand(document, target!, command, entry); transferred = true; return;
                    }
                }
                finally { if (!transferred) target?.Dispose(); }
                document.ChangeUIAttachedEntry(ids, key, entry);
            });
        }
        public void SelectUIFactoryEntry(UIDocument document, IReadOnlyList<Guid> ids, string path, Stride.Core.Assets.Editor.Quantum.NodePresenters.Commands.AbstractNodeEntry entry)
        {
            editor.Execute(() =>
            {
                if (!NativeAbstractEntries.Contains(document.CommonUIPropertyEntries(ids, path), entry) || ids.Any(id => !NativeAbstractEntries.Contains(document.UIPropertyEntries(id, path), entry))) throw new InvalidOperationException("The native UI factory entry is no longer available.");
                var fields = ids.Select(id => document.Properties(id).Single(field => field.Name == path)).ToArray();
                if (fields.Any(field => !document.CanEdit(field))) throw new InvalidOperationException("The selected UI properties cannot be edited.");
                var nodes = fields.Select(field => field.Node).ToArray();
                if (nodes.All(document.HasNativeMemberPresenter))
                {
                    var target = document.AcquireCombinedMemberTarget(nodes); var transferred = false;
                    try
                    {
                        var command = Stride.Core.Presentation.Quantum.Presenters.NodePresenterCombination.Commands(target.Presenters).FirstOrDefault(command => command.Name == "CreateNewInstance");
                        if (command != null) { editor.StartEditorExtensionCommand(document, target, command, entry); transferred = true; return; }
                    }
                    finally { if (!transferred) target.Dispose(); }
                }
                document.ChangeUIPropertyEntry(ids, path, entry);
            });
        }
        public void RegisterPathDrop(UIDocument document, Type type, long revision, Func<bool> canEdit, Action<Stride.Core.IO.UPath> assign)
            => editor.RegisterPropertyPathDropTarget(document, type, revision, canEdit, () => document.EditRevision, assign);
        public void DrawExtensionProperties(UIDocument document) => editor.DrawExtensionVirtualProperties(document);
        public void DrawPropertyCommands(UIDocument document, IReadOnlyList<Stride.Core.Quantum.IMemberNode> nodes, bool canEdit)
        {
            if (nodes.Count != 0 && nodes.All(node => ContentAssetReference.Supports(node.Type)))
                editor.DrawContentReferenceFetchActions(nodes.Select(node => node.Retrieve()));
            if (nodes.Count == 0 || editor.sessionHistory?.Extensions is not { } extensions) return;
            foreach (var command in extensions.PropertyCommands(document.AuthoredAsset, nodes[0].Type))
                if (editor.DrawNativeMenuItem(command.Name, "", false, canEdit && !editor.IsAuthoringPipelineUpdating
                    && nodes.All(node => command.CanExecute(document.AuthoredAsset, node))))
                    editor.Execute(() => document.ApplyInheritedAssetMutation(() =>
                    {
                        // Revalidate current authored selection before any command can mutate one of its members.
                        if (!document.CanEditSelectedExtensionMembers(nodes)) throw new InvalidOperationException("The selected UI properties cannot be edited.");
                        using var current = document.AcquireCombinedMemberTarget(nodes);
                        foreach (var node in nodes) command.Execute(document.AuthoredAsset, node);
                    }, command.Name));
            editor.DrawUIMemberProviderCommands(document, nodes);
        }
        public void DrawUnloadable(Stride.Core.Yaml.IUnloadable value) => editor.DrawUnloadableProperty(value);
        public void ShowDocumentation(Stride.Core.Quantum.IGraphNode node, Type? rootType = null) => editor.PropertyDocumentation.Show(node, rootType);
        public void ShowDocumentation(Stride.Core.PropertyKey key) => editor.PropertyDocumentation.Show(key);
        public void ReportFailure(Exception error)
        {
            EditorGraphicsFailures.ThrowIfDeviceLost(editor.GraphicsDevice, error);
            editor.statusMessage = error.Message;
            editor.outputLog.Append("Project", Stride.Core.Diagnostics.LogMessageType.Error, "ui-editor", error.Message,
                error.ToString(), assetId: presentedSource.AuthoredAsset.Id);
        }
        public void ReportValidationFailure(Exception error) { EditorGraphicsFailures.ThrowIfDeviceLost(editor.GraphicsDevice, error); editor.statusMessage = error.Message; }
        public void OpenSourceLibrary(AssetId assetId, Guid elementId)
        {
            if (!CanOpenSourceLibrary(assetId)) throw new InvalidOperationException("The source UI library is unavailable.");
            editor.OpenUILibraryDocuments([assetId]);
            if (elementId != Guid.Empty && editor.documents.Active?.Document is UIDocument library
                && library.Asset.Id == assetId && library.Find(elementId) != null)
                editor.GetUIEditor(library).SelectElement(library, elementId);
        }
        public bool CanOpenSourceLibrary(AssetId assetId) => editor.workspace?.Session.FindAsset(assetId)?.Asset is UILibraryAsset;
        public void OpenSourceLibraries(IReadOnlyList<AssetId> assetIds) => editor.OpenUILibraryDocuments(assetIds);
        public void SelectSourceLibrary(AssetId assetId)
        {
            if (editor.workspace == null || editor.assetBrowser.RevealAsset(editor.workspace, assetId) is not { } entry)
                throw new InvalidOperationException("The source UI library is unavailable.");
            editor.selectedAssetPath = entry.Path;
            editor.ActivateAssetPropertyContext(); editor.showAssets = true; editor.focusAssetsPanel = true;
        }
        public void PublishAsset(UIAssetExport export, UIDocument source)
            => editor.PublishUISelectionAsset(export, source);
    }
}
