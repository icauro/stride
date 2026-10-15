using Hexa.NET.ImGui;
using Stride.Core.Mathematics;
using Stride.Core.Serialization;
using Stride.Core.Reflection;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private ProceduralAssetDocument? assetDocument;
    private bool assetEditorFocused, showAssetDiscard;
    private bool assetCommandContext;
    private Action? afterAssetDiscard;
    private string? assetEditErrorValue;
    private Exception? assetEditDiagnosticOwner;
    private string? assetEditError
    {
        get => assetEditErrorValue;
        set { assetEditErrorValue = value; assetEditDiagnosticOwner = null; }
    }
    internal ProceduralAssetDocument? AssetDocument => assetDocument;
    private void OpenAssetEditor(string path, bool reload = false)
    {
        FinishEntityRename(true); document?.EndEdits();
        if (reload) RequestAssetReplace(() => OpenRetainedDocument(path, false, true));
        else Execute(() => OpenRetainedDocument(path, false, false));
    }
    private void RequestAssetReplace(Action action)
    {
        ValidatePendingPropertyStrings();
        assetDocument?.EndEdit();
        if (assetDocument?.IsDirty == true) { afterAssetDiscard = action; showAssetDiscard = true; }
        else Execute(action);
    }
    private void SaveAssetDocument()
    {
        if (IsPackagePlatformUpdating) throw new InvalidOperationException("The package platforms are being updated.");
        var source = assetDocument ?? throw new InvalidOperationException("No asset document is selected.");
        var expectedWorkspace = workspace; var expectedHistory = sessionHistory; var expectedPath = source.Path;
        var state = expectedHistory?.Registry.Find(expectedPath);
        if (state is not { IsPublished: true, IsDisposed: false } || !ReferenceEquals(state.Document, source))
            throw new InvalidOperationException("The asset document is no longer canonical.");
        using var lease = new RetainedPathLease(this, state.AcquireView());
        void Current()
        {
            if (IsPackagePlatformUpdating || !ReferenceEquals(workspace, expectedWorkspace) || !ReferenceEquals(sessionHistory, expectedHistory)
                || !ReferenceEquals(assetDocument, source) || !string.Equals(source.Path, expectedPath, StringComparison.OrdinalIgnoreCase)
                || !state.IsPublished || state.IsDisposed || !ReferenceEquals(expectedHistory!.Registry.Find(expectedPath), state))
                throw new InvalidOperationException("The asset save context changed.");
        }
        ValidatePendingPropertyStrings(); Current(); source.CommitEdits(); Current();
        source.Save(); statusMessage = "Saved " + Path.GetFileName(source.Path);
    }
    private void SaveAllDocuments()
    {
        if (IsPackageMetadataSaving) throw new InvalidOperationException("The package save is already running.");
        if (IsPackagePlatformUpdating) throw new InvalidOperationException("The package platforms are being updated.");
        if (IsPackageDependencyBusy) throw new InvalidOperationException("A project is already loading.");
        var expectedWorkspace = workspace; var expectedHistory = sessionHistory;
        void Current()
        {
            if (IsPackagePlatformUpdating || IsPackageDependencyBusy || !ReferenceEquals(workspace, expectedWorkspace)
                || !ReferenceEquals(sessionHistory, expectedHistory)) throw new InvalidOperationException("The save session changed.");
        }
        ValidatePendingPropertyStrings();
        Current(); FinishEntityRename(true); Current(); documents.SaveAll(Current);
        Current();
        SynchronizeSavedGameSettingsOrientations();
        if (workspace?.HasPackageChanges == true) SavePackageChanges();
        else sessionHistory?.NotifySaveAll(true);
    }
    private void SaveAllDocuments(Action success, Action<Exception>? failure = null)
    {
        if (IsPackagePlatformUpdating || IsPackageDependencyBusy || IsPackageMetadataSaving)
            throw new InvalidOperationException("A project operation is already running.");
        var expectedWorkspace = workspace;
        var expectedHistory = sessionHistory;
        void Current()
        {
            if (IsPackagePlatformUpdating || IsPackageDependencyBusy
                || !ReferenceEquals(workspace, expectedWorkspace) || !ReferenceEquals(sessionHistory, expectedHistory))
                throw new InvalidOperationException("The save session changed.");
        }
        ValidatePendingPropertyStrings(); Current();
        FinishEntityRename(true); Current();
        documents.SaveAll(Current); Current();
        SynchronizeSavedGameSettingsOrientations(); Current();
        if (workspace?.HasPackageChanges == true)
            RequestPackageMetadataSave(() => { Current(); success(); }, failure);
        else
        {
            sessionHistory?.NotifySaveAll(true);
            Current(); success();
        }
    }
    private void DrawProceduralDocument(ProceduralAssetDocument asset)
    {
        using var referenceOwners = BeginContentReferenceOwnerScope(asset);
        Gui.TextUnformatted(Path.GetFileNameWithoutExtension(asset.Path));
        Gui.BeginDisabled(IsPackagePlatformUpdating || !asset.CanSave);
        if (icons!.Button("save", "Save asset (Ctrl+S)")) Execute(SaveAssetDocument);
        if (Controls.Enabled) Controls.Record("asset-editor/save"); Gui.EndDisabled(); Gui.SameLine();
        Gui.BeginDisabled(IsPackagePlatformUpdating || !asset.CanUndo);
        if (icons.Button("undo", "Undo asset edit (Ctrl+Z)")) Execute(UndoCurrentCommand);
        if (Controls.Enabled) Controls.Record("asset-editor/undo"); Gui.EndDisabled(); Gui.SameLine();
        Gui.BeginDisabled(IsPackagePlatformUpdating || !asset.CanRedo);
        if (icons.Button("redo", "Redo asset edit (Ctrl+Y)")) Execute(RedoCurrentCommand);
        if (Controls.Enabled) Controls.Record("asset-editor/redo"); Gui.EndDisabled(); Gui.SameLine();
        Gui.BeginDisabled(IsPackagePlatformUpdating || !asset.CanSave);
        var updatePressed = Gui.Button("Save and update preview");
        RecordEngineAssetUpdateButton(updatePressed);
        if (updatePressed) Execute(StartAssetPreviewUpdate);
        if (Controls.Enabled) Controls.Record("asset-editor/update"); Gui.EndDisabled(); Gui.SameLine();
        if (Gui.Button(EditorLocalization.Widget("Close", "Button")) && documents.Find(asset) is { } closeEntry) RequestDocumentClose(closeEntry);
        if (Controls.Enabled) Controls.Record("asset-editor/close"); Gui.SameLine();
        Gui.TextUnformatted(asset.IsDirty ? "Unsaved changes" : "Saved");
        if (!IsPackagePlatformUpdating && assetEditorFocused && assetPreviewBuild == null && projectLoading == null && !closePromptOpen && closingDocument == null && Gui.GetIO().KeyCtrl)
        {
            if (Gui.IsKeyPressed(ImGuiKey.S, false)) Execute(() => SaveAllDocuments(() => { }));
            if (!Gui.IsAnyItemActive() && Gui.IsKeyPressed(ImGuiKey.Z, false)) Execute(UndoCurrentCommand);
            if (!Gui.IsAnyItemActive() && Gui.IsKeyPressed(ImGuiKey.Y, false)) Execute(RedoCurrentCommand);
        }
        Gui.Separator();
        var typeField = asset.PropertyField(asset.AuthoringGraph.RootNode[nameof(Stride.Assets.Models.ProceduralModelAsset.Type)]);
        var typeEntries = asset.PropertyEntries(typeField.Node);
        var customTypeEditor = TryDrawRegisteredProceduralProperty(asset, typeField);
        if (!customTypeEditor)
        {
            Gui.BeginDisabled(IsAuthoringPipelineUpdating || !asset.CanEdit || !asset.CanEditAssetSettingNode(asset.AuthoringGraph.RootNode[nameof(Stride.Assets.Models.ProceduralModelAsset.Type)])
                || !CanExecuteProceduralFactory(asset, typeField, typeEntries));
            Gui.SetNextItemWidth(200);
            if (BeginNativeCombo("Model type", typeEntries.Count != 0 ? NativeAbstractEntryControl.Label(typeEntries, asset.Asset.Type)
                : ProceduralAssetDocument.ModelDisplayName(asset.Asset.Type?.GetType())))
            {
                if (typeEntries.Count != 0)
                    NativeAbstractEntryControl.Choices(typeEntries, asset.Asset.Type, entry => SelectProceduralFactoryEntry(asset, typeField, entry));
                Gui.EndCombo();
            }
            if (Controls.Enabled) Controls.Record("asset-editor/type");
            Gui.EndDisabled();
            DrawProceduralBaseReset(asset, asset.AuthoringGraph.RootNode[nameof(Stride.Assets.Models.ProceduralModelAsset.Type)]);
        }
        if (Gui.BeginChild("Asset properties", new System.Numerics.Vector2(0, 0)))
        {
            if (asset.Asset.Type is Stride.Core.Yaml.IUnloadable unknown) DrawUnloadableProperty(unknown);
            if (!customTypeEditor) DrawProceduralFields(asset, asset.Fields);
            if (assetEditError != null) DrawSelectableText("##AssetEditError", assetEditError);
        }
        ImGuiScopes.EndChild();
    }

    private void DrawAssetEditor()
    {
        if (showAssetDiscard) { Gui.OpenPopup("Unsaved asset changes"); showAssetDiscard = false; }
        using var modal = ImGuiScopes.Modal("Unsaved asset changes", ImGuiWindowFlags.AlwaysAutoResize);
        if (!modal.DrawContents) return;
        {
            DrawSelectableText("##AssetDiscardMessage", "Save changes to " + Path.GetFileName(assetDocument!.Path) + " before continuing?");
            if (DrawNativeButton(EditorLocalization.Widget("Save", "Button"))) Execute(() => { SaveAssetDocument(); Gui.CloseCurrentPopup(); afterAssetDiscard?.Invoke(); afterAssetDiscard = null; });
            if (Controls.Enabled) Controls.Record("asset-discard/save"); Gui.SameLine();
            if (DrawNativeButton("Discard")) Execute(() => { AbandonPendingPropertyStrings(); Gui.CloseCurrentPopup(); afterAssetDiscard?.Invoke(); afterAssetDiscard = null; });
            if (Controls.Enabled) Controls.Record("asset-discard/discard"); Gui.SameLine();
            if (DrawNativeButton(EditorLocalization.Widget("Cancel", "Button"))) { Gui.CloseCurrentPopup(); afterAssetDiscard = null; }
            if (Controls.Enabled) Controls.Record("asset-discard/cancel");
        }
    }
    private void DrawAssetProperty(ProceduralAssetDocument asset, AssetPropertyField field, HashSet<Stride.Core.Quantum.IObjectNode>? ancestors = null)
    {
        if (TryDrawRegisteredProceduralProperty(asset, field)) return;
        var collectionContainer = field.Node.Descriptor is CollectionBaseDescriptor or DictionaryDescriptor;
        var objectPresence = !field.SupportsEditing && !field.Type.IsValueType && (collectionContainer || asset.PropertyEntries(field.Node).Count == 0)
            && ObjectFactoryRegistry.CanCreateInstance(field.Type);
        var nullable = field.SupportsEditing && Nullable.GetUnderlyingType(field.Node.Type) != null || objectPresence;
        using var nullableRow = NativeNullableRow.Begin("##Nullable/" + field.Name, nullable);
        if (nullable)
        {
            Gui.PushID(field.Name + "/presence");
            try
            {
                var current = field.Node.Retrieve();
                using var disabled = ImGuiScopes.Disabled(objectPresence
                    ? !ReferenceEquals(assetDocument, asset) || !asset.CanSetObjectPresence(field, current == null)
                    : !CanEditProceduralProperty(asset, field));
                if (DrawNativeNullablePresence(current != null, out var create, field.AllowsNull))
                    Execute(() =>
                    {
                        if (objectPresence)
                        {
                            ValidatePendingPropertyStrings();
                            if (!ReferenceEquals(assetDocument, asset)) throw new InvalidOperationException("The procedural selection changed.");
                            asset.SetObjectPresence(field, current, create);
                        }
                        else CommitProceduralProperty(asset, field, current, create ? CreateNullableValue(field.Type) : null);
                    });
            }
            finally { Gui.PopID(); }
            nullableRow.ValueColumn();
            if (field.Node.Retrieve() == null)
            {
                Gui.TextUnformatted(field.Label); Gui.SameLine(); Gui.TextUnformatted(EditorLocalization.Text("(null)"));
                DrawProceduralBaseReset(asset, field.Node);
                return;
            }
        }
        DrawProceduralPropertyValue(asset, field, ancestors);
        DrawProceduralBaseReset(asset, field.Node);
    }
    private void DrawProceduralBaseReset(ProceduralAssetDocument asset, Stride.Core.Quantum.IGraphNode node)
    {
        if (!Gui.BeginPopupContextItem("Property actions/" + node.Guid)) return;
        if (node is Stride.Core.Quantum.IMemberNode member)
        {
            DrawProceduralMemberPresenterCommands(asset, member);
            if (ContentAssetReference.Supports(member.Type)) DrawContentReferenceFetchActions([member.Retrieve()]);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Copy", "Menu"), "", false, asset.CanCopyProperty(member)))
                Execute(() => { ValidatePendingPropertyStrings(); Gui.SetClipboardText(asset.CopyProperty(member)); });
            var clipboard = Gui.GetClipboardTextS() ?? "";
            var canPaste = asset.CanPasteProperty(member, clipboard);
            if (DrawNativeMenuItem(EditorLocalization.Widget("Paste", "Menu"), "", false, canPaste))
                Execute(() => { ValidatePendingPropertyStrings(); asset.PasteProperty(member, clipboard, false); });
            if (DrawNativeMenuItem("Replace", "", false, canPaste))
                Execute(() => { ValidatePendingPropertyStrings(); asset.PasteProperty(member, clipboard, true); });
        }
        if (DrawNativeMenuItem(EditorLocalization.Widget("Reset to base value", "Menu"), "", false, asset.CanResetAssetSettingNode(node)))
            Execute(() => { ValidatePendingPropertyStrings(); asset.ResetAssetSettingNode(node); });
        Gui.EndPopup();
    }
    private void DrawProceduralPropertyValue(ProceduralAssetDocument asset, AssetPropertyField field, HashSet<Stride.Core.Quantum.IObjectNode>? ancestors = null)
    {
        if (TryDrawRegisteredProceduralProperty(asset, field)) return;
        Gui.PushID(field.Name);
        var value = field.Node.Retrieve(); object? updated = value; var changed = false;
        if (asset.CollectionContext(field) is { } collection)
        {
            try { DrawProceduralCollection(asset, collection, ancestors); }
            finally { Gui.PopID(); }
            return;
        }
        if (!field.SupportsEditing && DrawProceduralObjectProperty(asset, field, ancestors)) { Gui.PopID(); return; }
        Gui.TextUnformatted(asset.PropertyLabel(field.Node) ?? field.Label);
        DrawProceduralBaseReset(asset, field.Node);
        if (DrawProceduralStockProperty(asset, field)) { Gui.PopID(); return; }
        if (field.Type.IsEnum)
        {
            try { DrawProceduralEnum(asset, field); }
            finally { Gui.PopID(); }
            return;
        }
        if (AssetSettingNumericMetadata.Supports(field.Type))
        {
            try { DrawProceduralNumber(asset, field); }
            finally { Gui.PopID(); }
            return;
        }
        if (field.Type == typeof(Stride.Rendering.Material))
        {
            DrawMaterialReference(asset, field); Gui.PopID(); return;
        }
        var dimensions = NativeNumericAxes.Names(field.Type).Count;
        if (dimensions != 0)
        {
            DrawAssetVector(asset, field, value!, dimensions); Gui.PopID(); return;
        }
        if (field.Type == typeof(char) || field.Type == typeof(Guid) || field.Type == typeof(TimeSpan)
            || field.Type == typeof(DateTime) || field.Type == typeof(AngleSingle))
        {
            try
            {
                DrawNativeScalarTemplate(field.Node, field.Type, value, false,
                    ReferenceEquals(assetDocument, asset) && asset.IsPropertyFieldCurrent(field)
                        && field.SupportsEditing && asset.CanEditAssetSettingNode(field.Node), updatedValue =>
                    {
                        if (!ReferenceEquals(assetDocument, asset) || !asset.IsPropertyFieldCurrent(field)
                            || !field.SupportsEditing || !asset.CanEditAssetSettingNode(field.Node))
                            throw new InvalidOperationException("The procedural property is no longer editable.");
                        asset.EndEdit();
                        ((IEditorAssetGraphDocument)asset).ApplyInheritedAssetMutation(
                            () => field.Node.Update(updatedValue), "Edit " + field.Label);
                        assetEditError = null;
                    }, Controls.Enabled ? part => Controls.Record("asset-editor/field/" + field.Name + "/" + part) : null);
                if (Controls.Enabled) Controls.Record("asset-editor/field/" + field.Name);
            }
            catch (Exception error) { assetEditError = error.Message; statusMessage = error.Message; }
            finally { Gui.PopID(); }
            return;
        }
        Gui.SetNextItemWidth(-1); Gui.BeginDisabled(!field.SupportsEditing || !asset.CanEditAssetSettingNode(field.Node) || asset.NumericReadOnly(field.Node));
        if (field.Type == typeof(string))
        {
            try
            {
                PropertyGrid.Strings.Draw(field.Node, false,
                    () => ReferenceEquals(assetDocument, asset) && asset.IsPropertyFieldCurrent(field)
                        && field.SupportsEditing && asset.CanEditAssetSettingNode(field.Node),
                    () => [field.Node.Retrieve()], replacement =>
                    {
                        asset.EndEdit();
                        ((IEditorAssetGraphDocument)asset).ApplyInheritedAssetMutation(
                            () => field.Node.Update(replacement), "Edit " + field.Label);
                        assetEditError = null;
                    }, () => AcquirePropertyStringOwner(asset));
                if (Controls.Enabled) Controls.Record("asset-editor/field/" + field.Name);
            }
            catch (Exception error) { assetEditError = error.Message; statusMessage = error.Message; }
            finally { Gui.EndDisabled(); Gui.PopID(); }
            return;
        }
        if (field.Type == typeof(bool)) { var n = (bool)value!; changed = Gui.Checkbox("##Value", ref n); updated = n; }
        else if (field.SupportsEditing && field.Type.IsEnum)
        {
            var combo = Gui.BeginCombo("##Value", value!.ToString());
            if (Controls.Enabled) Controls.Record("asset-editor/field/" + field.Name);
            if (combo)
            {
                foreach (var name in field.EnumNames)
                {
                    if (Gui.Selectable(name, name == value.ToString())) { updated = Enum.Parse(field.Type, name); changed = true; }
                    if (Controls.Enabled) Controls.Record("asset-editor/field/" + field.Name + "/option/" + name);
                }
                Gui.EndCombo();
            }
        }
        else
        {
            var referenceValue = value is Stride.Rendering.MaterialInstance instance ? instance.Material : value;
            DrawSelectableText("##UnsupportedValue", referenceValue == null ? "None" : AttachedReferenceManager.GetAttachedReference(referenceValue)?.Url ?? field.Type.Name);
        }
        if (!field.Type.IsEnum && Gui.IsItemVisible()) if (Controls.Enabled) Controls.Record("asset-editor/field/" + field.Name);
        if (field.SupportsEditing)
        {
            if (Gui.IsItemActivated()) asset.BeginEdit(field);
            if (changed) { try { asset.Update(field, updated); assetEditError = null; } catch (Exception error) { assetEditError = error.Message; } }
            if (Gui.IsItemDeactivated() || field.Type == typeof(bool) && changed || field.Type.IsEnum && changed) asset.EndEdit();
        }
        Gui.EndDisabled(); Gui.PopID();
    }

    private void DrawAssetVector(ProceduralAssetDocument asset, AssetPropertyField field, object value, int dimensions)
    {
        if (Gui.BeginTable("##AssetAxes", Math.Min(4, dimensions), ImGuiTableFlags.SizingStretchSame))
        {
            try
            {
                Gui.TableNextRow();
                for (var axis = 0; axis < dimensions; axis++)
                {
                    if (axis % 4 == 0 && axis != 0) Gui.TableNextRow();
                    Gui.TableSetColumnIndex(axis % 4); Gui.PushID(axis);
                    try { if (field.Type != typeof(Matrix)) Gui.TextUnformatted(ExtendedMathLabel(field.Type, axis)); DrawProceduralNumber(asset, field, axis); }
                    finally { Gui.PopID(); }
                }
            }
            finally { Gui.EndTable(); }
        }
    }
}
