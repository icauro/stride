# Source layout and development guide

The experimental ImGui editor lives in this project. Start with
[Program.cs](Program.cs), then [Application/EditorGame.cs](Application/EditorGame.cs).

## Directory ownership

See [architecture.md](../../../docs/editor/imgui/architecture.md) for panel ownership,
explicit host contracts, shared services, native adapters and development profiles.
New presentation state belongs to the concrete feature panel, rather than another
`EditorGame` field. Existing compatibility adapters preserve authoring behavior.

```text
Stride.Editor.ImGui/
  Program.cs                       Entry point and asset registry bootstrap
  Stride.Editor.ImGui.csproj        Build, references and packaged resources
  Application/                     Host lifetime, composition, shell and menus
  Projects/                        Package sessions, compiler jobs and content mounts
    Platforms/                     Captured platform inputs, private native preparation and file publication receipts
    Templates/                     Registered generator preparation, owned sessions and project publication
  Documents/
    IEditorDocument.cs              Shared authored-document command contract
    EditorDocumentSession.cs        Retained document ownership and tab identities
    Entities/                      Shared actual entity hierarchy graph and transactions
    Scene/                         Typed SceneAsset document
    Prefabs/                       Typed PrefabAsset document
    Procedural/                    Authored procedural asset graph and history
    Curves/                        Canonical curve tracks and retained point edit certificates
  Properties/                      Shared validation and numeric metadata
  Features/
    Documents/                     Shared tab UI and per-document close prompt
    Assets/                        Asset browser
      Compilation/                 Asset rebuild/refresh coordination
      Procedural/                  Procedural document controls and material picker
    Hierarchy/                     Entity tree, rename, factories and drag/drop
    Inspector/                     Property controls and reference pickers
    Output/                        Complete channel log histories and Output UI
    History/                       Actual transaction lists and guarded navigation
    Curves/                        Curve canvas, selection, axis views and numeric draft presentation
    Scripts/                       Native source editing and real Roslyn workspace services
    VisualScripts/                 Actual VisualScriptAsset editor controls and graph canvas
    Selection/                     Selection navigation across actual hierarchy owners
    Sprites/                       Sprite-sheet canvas, pixel tools and native import controls
    Skyboxes/                      Real skybox compilation, preview and runtime refresh
    Build/                         User project build/run controls
    Scene/
      Camera/                      Editor camera input, lens and local preferences
      Gizmos/                      Shared WPF gizmo sources, viewport adapter and toolbar
      Lighting/                    Actual light-probe capture, volumes and lifecycle
      Navigation/                  Native static/dynamic navigation visualization
    Previews/
      Models/                      Real model preview and its controls
      Textures/                    Shared-engine texture preview adapter
      Thumbnails/                  Visible-first engine thumbnail providers and bounded caches
  Infrastructure/
    Graphics/                      ImGui renderer and development shader setup
    Input/                         Stride input to ImGui adapter
    Platform/                      Native Windows clipboard, cursors and file chooser
    Preferences/                   Versioned local editor/dialog settings
    Presentation/                  Icon texture loading
  Resources/Icons/                  Editable SVGs, generated PNGs and icon builder
  Diagnostics/
    Automation/                    Existing UI harness and capture hooks
    Regression/                    Existing authored-data regressions
    Measurements/                  Existing frame/allocation measurement helpers
    Smoke/                         Legacy smoke fixture, distinct from real scenes
```

`Infrastructure/Scheduling/EditorBackgroundJobRetirement.cs` owns completion
observation and cancellation-token lifetime for detached CPU/session/compiler
jobs. The host supplies the real result-release operation; project template
panels retire generation without adopting into a closed UI. Graphics retirement
and authored actor publication retain their separate ownership paths.

`Projects/Templates/ProjectTemplateParameters.cs` owns registered parameter
visibility, initial values and the native default-omitting submission map.
`ProjectCreationPanel` owns editing and selection; `ProjectTemplateService`
owns actual generation, staging and destination publication.

`Infrastructure/Presentation/EditorGame.TemplateRows.cs` owns the native template
row artwork, shaped name/description and WordEllipsis layout shared by project
and asset creation. Feature adapters retain their actual descriptors, item IDs,
selection and command behavior; the existing template image cache and native
glyph owner retain all resource lifetime responsibilities.

Generated `bin/` and `obj/` are build outputs. The root contains the entry point,
project file and developer documentation; feature implementations belong in their
owned directory. Add specialized feature directories when implementing them;
do not create empty scaffolding for every backlog topic.

Native thumbnail services live in the sibling `Stride.Editor.Thumbnails`
assembly. Both hosts reference its original contracts and compiler/generator
implementation; WPF forwards the original public types. Custom thumbnail work
runs through the native compiler process with immutable captured inputs, while
canonical graph ownership and final GPU publication stay on the editor actor.

`Infrastructure/Presentation/EditorLocalization.cs` loads the original Gettext
catalogs without WPF assemblies. Use the original catalog context for fixed
captions and `Widget` for interactive labels; authored names and paths remain
data. `EditorPanelIdentity.DisplayWindowName` translates captions while retaining
their original ImGui hash, docking identity and independent capture ID.

## Find the code for a task

| Task | Start here | Related authored/runtime code |
|---|---|---|
| Host initialization, drawing and disposal | [Application/EditorGame.cs](Application/EditorGame.cs) | Application/EditorGame.State.cs holds shared composition references |
| Frame UI orchestration | [Application/EditorGame.Shell.cs](Application/EditorGame.Shell.cs) | Calls each feature panel; contains no tree/inspector/browser implementation |
| Menu actions and global shortcuts | [Application/EditorGame.MainMenu.cs](Application/EditorGame.MainMenu.cs) | Documents/EditorGame.DocumentCommands.cs |
| Docking and default layout | [Application/EditorGame.Docking.cs](Application/EditorGame.Docking.cs) | Application/EditorGame.WorkspacePanel.cs |
| Project open/load/cancel | [Projects/EditorGame.ProjectLoading.cs](Projects/EditorGame.ProjectLoading.cs) | ProjectWorkspace, CompiledProjectBuild, ProjectLoadState |
| Recent projects, active-project menu and Close Project | [Projects/EditorGame.ProjectCommands.cs](Projects/EditorGame.ProjectCommands.cs) | ProjectWorkspace owns actual solution/package state; preferences retain local history |
| Shared project content ownership | [Projects/CompiledProjectContent.cs](Projects/CompiledProjectContent.cs) | Features/Scene/EngineViewport.cs |
| User project build/run and diagnostics | [Features/Build/EditorGame.ProjectBuild.cs](Features/Build/EditorGame.ProjectBuild.cs) | Projects/DesktopProjectBuild.cs and BuildDiagnostic.cs |
| IDE integration | [Projects/ProjectIdeLauncher.cs](Projects/ProjectIdeLauncher.cs) | Shared Stride IDE discovery, preferences and External tools settings |
| Asset rename/move/delete | [Features/Assets/EditorGame.AssetRelocation.cs](Features/Assets/EditorGame.AssetRelocation.cs) | Projects/AssetRelocationHistory.cs, AssetDeletionHistory.cs and retained document callbacks |
| Scene save/history/metadata | [Documents/Entities/EntityHierarchyDocument.cs](Documents/Entities/EntityHierarchyDocument.cs) | EntityHierarchyDocument.HierarchyCreation/Deletion/Reparenting/ComponentEditing.cs |
| Document ownership, activation and closure | [Documents/EditorGame.DocumentSession.cs](Documents/EditorGame.DocumentSession.cs) | Features/Documents/EditorGame.DocumentTabs.cs hosts actual editor contents |
| Hierarchy UI | [Features/Hierarchy/EditorGame.Hierarchy.cs](Features/Hierarchy/EditorGame.Hierarchy.cs) | Owned hierarchy operations in Documents/Entities |
| Component/property UI | [Features/Inspector/EditorGame.ComponentInspector.cs](Features/Inspector/EditorGame.ComponentInspector.cs) | Documents/Entities/ComponentField.cs |
| Entity transform UI | [Features/Inspector/EditorGame.Inspector.cs](Features/Inspector/EditorGame.Inspector.cs) | Documents/Entities/TransformState.cs |
| Scene camera/navigation | [Features/Scene/Camera/EditorGame.Camera.cs](Features/Scene/Camera/EditorGame.Camera.cs) | EditorViewportCamera, CameraLens, SceneCameraSettingsStore |
| Actual scene rendering and synchronization | [Features/Scene/EngineViewport.cs](Features/Scene/EngineViewport.cs) | Features/Scene/EditorGame.SceneViewport.cs hosts the output |
| Asset tiles/view size | [Features/Assets/EditorGame.AssetTiles.cs](Features/Assets/EditorGame.AssetTiles.cs) | Native clipped tile rows request real engine thumbnails; preferences preserve WPF zoom bounds |
| Packaged editor shaders | [Infrastructure/Graphics/EditorGame.ShaderSources.cs](Infrastructure/Graphics/EditorGame.ShaderSources.cs) | Build/publish copies all engine shader sources under Shaders/Engine |
| Help links | [Application/EditorGame.Help.cs](Application/EditorGame.Help.cs) | Shared WPF product URLs/version constants |
| Package properties and dependencies | [Features/Assets/EditorGame.PackageDependencies.cs](Features/Assets/EditorGame.PackageDependencies.cs) | Projects/PackageDependencies.cs and canonical ProjectBuildSettingsDocument XML/history ownership |
| Game settings | [Features/Settings/EditorGame.GameSettings.cs](Features/Settings/EditorGame.GameSettings.cs) | Documents/Settings/AssetMetadataDocument.GameSettings.cs and real configuration graphs |
| Prefab extraction | [Features/Prefabs/EditorGame.PrefabCreation.cs](Features/Prefabs/EditorGame.PrefabCreation.cs) | Documents/Entities/EntityHierarchyDocument.PrefabExtraction.cs uses real base-part links and shared history |
| Inherited scalar properties | [Features/Inspector/EditorGame.InheritedProperties.cs](Features/Inspector/EditorGame.InheritedProperties.cs) | Actual hierarchy inheritance APIs, override flags and retained base graphs |
| Asset browser | [Features/Assets/EditorGame.AssetBrowser.cs](Features/Assets/EditorGame.AssetBrowser.cs) | Projects/ProjectWorkspace.cs |
| Native browser tile/table text | [Features/Assets/AssetBrowserPanel.NativeLabels.cs](Features/Assets/AssetBrowserPanel.NativeLabels.cs), [Features/Assets/AssetBrowserPanel.TileLabels.cs](Features/Assets/AssetBrowserPanel.TileLabels.cs) | Actual shaped measure/paint host callbacks; canonical content-version metric cache, coupled tile/table/clipper geometry and grapheme-safe displayed wrap/ellipsis |
| Native filter suggestions/chips | [Features/Assets/AssetBrowserPanel.Filters.cs](Features/Assets/AssetBrowserPanel.Filters.cs), [Features/Assets/AssetFilterSearch.cs](Features/Assets/AssetFilterSearch.cs) | Actual native popup dimensions and chip caption/button bounds; original query focus, stable IDs and canonical filter commands; ordinary editable query and menu text remain separate |
| Native compact buttons | [Infrastructure/Presentation/EditorGame.NativeWidgets.cs](Infrastructure/Presentation/EditorGame.NativeWidgets.cs) | SmallButton follows native zero vertical padding/current-line baseline and original IDs/ButtonBehavior; primary filter controls borrow the shared glyph owner |
| Native menus | [Infrastructure/Presentation/EditorGame.NativeMenus.cs](Infrastructure/Presentation/EditorGame.NativeMenus.cs) | Coupled native label/shortcut/menu-column geometry, original selectable IDs/flags and actual popup/submenu/navigation lifecycle; opted-in main-menu call sites retain original commands and EndMenu |
| Native document close cohorts | [Features/Documents/EditorGame.DocumentCloseBatch.cs](Features/Documents/EditorGame.DocumentCloseBatch.cs), [Features/Documents/EditorGame.DocumentTabs.cs](Features/Documents/EditorGame.DocumentTabs.cs) | Original Close All / Close All But This capture actual ordered entries/document owners/workspace/history and drive existing dirty-close consent/view cleanup from normal and inactive lifecycle; cancel/failure/replacement/clear guards, explicit Discard-only generation acceptance |
| UI selection export publication | [Features/UI/EditorGame.UIAssetExport.cs](Features/UI/EditorGame.UIAssetExport.cs), [Documents/UI/UIDocument.cs](Documents/UI/UIDocument.cs), [Documents/UI/EditorUiLibraryGraphs.cs](Documents/UI/EditorUiLibraryGraphs.cs) | Actual source-directory/root-name policy, source revision/layout/path/provider certificates, one compensating canonical create+relink composite and existing selection restoration; inactive native retained bases reuse exact held owner generations for real undo/compensation, never active missing-base replacement |
| UI element Delete confirmation | [Features/UI/UIEditorPanel.Deletion.cs](Features/UI/UIEditorPanel.Deletion.cs) | Original singular/plural question, Cancel default and persisted Don't ask again policy; actual canonical request leases/common-root/parent/ordered selection/revision/layout certificates cross pending validation into the existing queued Delete journal; activation/disposal cancel leases, Cut remains separate |
| UI hierarchy native text | [Features/UI/UIEditorPanel.Hierarchy.cs](Features/UI/UIEditorPanel.Hierarchy.cs), [Features/UI/IUIEditorHost.cs](Features/UI/IUIEditorHost.cs) | Actual native tree layout/paint/virtualizer; original element IDs/selection/library commands and recursion; open popup rows force submission; ordinary input and tab shaping separate |
| UI hierarchy drag/move | [Features/UI/UIEditorPanel.HierarchyDragDrop.cs](Features/UI/UIEditorPanel.HierarchyDragDrop.cs), [Documents/UI/UIDocument.HierarchyTransfer.cs](Documents/UI/UIDocument.HierarchyTransfer.cs) | Actual owner certificates/leases and row geometry, subtree insertion/empty-root targets, native hierarchy clone/ID map and one real shared source/destination history capture |
| UI factory palette drag/create | [Features/UI/UIEditorPanel.FactoryDragDrop.cs](Features/UI/UIEditorPanel.FactoryDragDrop.cs), [Documents/UI/UIDocument.FactoryCreation.cs](Documents/UI/UIDocument.FactoryCreation.cs), [Features/UI/UIEditorViewport.EditorPicking.cs](Features/UI/UIEditorViewport.EditorPicking.cs) | Actual canonical public library factories with native system DisplayAttribute/user URL categories, nested category/name ordering and original Panel-category group/layout eligibility; retained native payload/queued target certificates, shared hierarchy DropZone geometry, real engine intersections and native clone/instance Quantum creation |
| UI context/keyboard/inline rename | [Features/UI/UIEditorPanel.Commands.cs](Features/UI/UIEditorPanel.Commands.cs), [Documents/UI/UIDocument.RenameEdit.cs](Documents/UI/UIDocument.RenameEdit.cs), [Documents/UI/UIDocument.Ungroup.cs](Documents/UI/UIDocument.Ungroup.cs) | Original actual canvas/row/keyboard command routing, retained current owner/factory command certificates, document-owned dirty-safe inline Name drafts and native clone/remove/append ungroup history |
| UI retained path drafts/pickers | [Features/UI/EditorGame.UIPathDrafts.cs](Features/UI/EditorGame.UIPathDrafts.cs) | Typed file/directory drafts, real native session-relative dialogs and per-window path drop targets retain actual canonical full baselines/leases and validate before normalized shared-journal application |
| UI paired character/Unicode drafts | [Features/UI/EditorGame.UICharacterDrafts.cs](Features/UI/EditorGame.UICharacterDrafts.cs) | Existing native paired character controller uses actual UI target/provider/item baselines and owner leases through pending validation and combined journals |
| UI retained temporal drafts | [Features/UI/EditorGame.UITemporalDrafts.cs](Features/UI/EditorGame.UITemporalDrafts.cs) | Native duration/date component helpers and popup presentation retain full canonical target/ticks/kind baselines and owner/provider leases through pending validation; component replacement uses actual normalized journals |
| UI retained native color drafts | [Features/UI/EditorGame.UIColorDrafts.cs](Features/UI/EditorGame.UIColorDrafts.cs) | Shared native byte/palette/hex/channel/picker behavior over actual ordinary/attached/virtual/item UIValueTargets, full parent/provider certificates and combined presenter leases; normalized canonical transforms and pending Save/close validation |
| UI retained GUID drafts | [Features/UI/EditorGame.UIGuidDrafts.cs](Features/UI/EditorGame.UIGuidDrafts.cs) | Existing native retained GUID mask controller receives actual canonical target/provider/item baselines and leases; pending Save/close validation and native combined journals preserve mask/caret/conversion/readonly behavior |
| UI retained degree drafts | [Features/UI/EditorGame.UIAngleDrafts.cs](Features/UI/EditorGame.UIAngleDrafts.cs) | Existing native retained-angle controller consumes actual UI canonical target/provider/part/item baselines and leases; full pending Save/close validation and typed combined journals preserve original degree precision/stepping/readonly behavior |
| UI retained scalar/math numeric drafts | [Features/UI/EditorGame.UINumericDrafts.cs](Features/UI/EditorGame.UINumericDrafts.cs), [Features/UI/UIEditorPanel.NumericTargets.cs](Features/UI/UIEditorPanel.NumericTargets.cs) | Actual canonical member/item/attached/provider target certificates and owner leases, full-parent axis baselines, native parser/range/stepping and slider snapshots feed shared pending validation/journals; per-part native Euler views restore snapshots on failed application |
| UI member/attached/virtual/collection string drafts | [Features/UI/EditorGame.UIPropertyStrings.cs](Features/UI/EditorGame.UIPropertyStrings.cs) | Shared pending-string Save/close validation with actual canonical owner/presenter leases, selected property-node/item/ancestor/value baseline guards and native combined normalization/history; readonly selection/copy remains available |
| Compositor retained numeric targets/drafts | [Features/Compositor/EditorGame.CompositorDraftTargets.cs](Features/Compositor/EditorGame.CompositorDraftTargets.cs), [Features/Compositor/EditorGame.CompositorNumericDrafts.cs](Features/Compositor/EditorGame.CompositorNumericDrafts.cs) | Shared actual canonical accessor/item/parent/provider epoch and owner certificates; native typed parsing/ranges/steps and active slider values feed pending validation and normalized single/combined compositor journals |
| Compositor retained math drafts/presenters | [Features/Compositor/EditorGame.CompositorMathDrafts.cs](Features/Compositor/EditorGame.CompositorMathDrafts.cs), [Documents/Compositor/AssetMetadataDocument.CompositorNumericPresentation.cs](Documents/Compositor/AssetMetadataDocument.CompositorNumericPresentation.cs) | Full parent retained per-axis numeric drafts, actual native child ranges/readonly rules and presenter leases; per-target authored-node/item Euler state with failure restoration; normalized canonical single/combined journals |
| Compositor retained color drafts | [Features/Compositor/EditorGame.CompositorColorDrafts.cs](Features/Compositor/EditorGame.CompositorColorDrafts.cs) | Shared native byte/palette/swatch/hex/HSB/picker controls over actual compositor target values, canonical item/provider certificates and presenter leases; native single/combined journals and pending Save/close validation |
| Compositor retained special scalar drafts | [Features/Compositor/EditorGame.CompositorSpecialDrafts.cs](Features/Compositor/EditorGame.CompositorSpecialDrafts.cs) | Shared native AngleSingle degree, GUID mask and paired immediate-glyph/retained-Unicode controls over actual canonical compositor targets; owner/presenter leases, source baselines and normalized single/combined journals |
| Compositor retained temporal drafts | [Features/Compositor/EditorGame.CompositorTemporalDrafts.cs](Features/Compositor/EditorGame.CompositorTemporalDrafts.cs) | Actual native TimeSpan components and DateTime summary/popup over retained canonical target drafts; whole-value/tick/Kind certificates and owner/presenter leases, own-control popup cancellation and normalized native journals |
| Compositor retained path drafts | [Features/Compositor/EditorGame.CompositorPathDrafts.cs](Features/Compositor/EditorGame.CompositorPathDrafts.cs) | Shared native retained UFile/UDirectory text, browse and file-drop controls over actual compositor owner/presenter leases and source certificates; modal source revalidation and normalized native journals |
| Compositor name drafts/command preflight | [Features/Compositor/EditorGame.CompositorListEditing.cs](Features/Compositor/EditorGame.CompositorListEditing.cs), [Features/Compositor/EditorGame.CompositorCommands.cs](Features/Compositor/EditorGame.CompositorCommands.cs) | Dynamic retained native stage/camera Name bindings with canonical item/presenter certificates; structural commands lease current owners, validate pending drafts and recertify native graph/provider eligibility before existing journals |
| Compositor list keyboard/selection scopes | [Features/Compositor/EditorGame.CompositorListKeyboard.cs](Features/Compositor/EditorGame.CompositorListKeyboard.cs) | Actual retained ItemId focus, extended keyboard/range navigation and row scrolling; canonical pending-draft preflight and native exclusive list/node scope handling for list, graph-node and marquee selection |
| Compositor selection navigation scopes | [Features/Compositor/EditorGame.CompositorSelectionHistory.cs](Features/Compositor/EditorGame.CompositorSelectionHistory.cs), [Features/Selection/EditorSelectionHistory.cs](Features/Selection/EditorSelectionHistory.cs) | Canonical compositor asset/scope/item IDs join the existing frame-coalesced global navigation through one independent Core SelectionOperation transaction stack; actual current-view resolution, stale-ID skipping and scope unregister preserve authored history separation |
| Compositor whole-selection delete | [Features/Compositor/EditorGame.CompositorDeleteSelection.cs](Features/Compositor/EditorGame.CompositorDeleteSelection.cs) | One captured actual list/block/link scope command with current canonical identity/target recertification after draft preflight; existing real graph operations publish through compensating shared capture before selection cleanup; per-list toolbar removal recertifies actual selected ItemIds/nodes after drafts, uses separate Add/Remove eligibility and clears native Inspector presenter roots after success |
| Compositor native graph pointer selection | [Features/Compositor/EditorGame.CompositorEditor.cs](Features/Compositor/EditorGame.CompositorEditor.cs), [Features/Compositor/EditorGame.CompositorListKeyboard.cs](Features/Compositor/EditorGame.CompositorListKeyboard.cs) | Original single node/link click policies, retained group press/drag and exact graph-space release collapse, canonical draft recertification and independent list/link scope handling; a new link clears native Inspector presenter roots while preserving registered list primary chronology and navigation history; zero-hit graph releases preserve registered lists and only update Inspector after real graph collection changes; native zero-width/height areas use first-containing point selection rather than line intersection; original marquee modifier rules remain separate |
| Compositor retained native link selection | [Features/Compositor/EditorGame.CompositorLinkSelection.cs](Features/Compositor/EditorGame.CompositorLinkSelection.cs) | Actual graph/source/target/member/path-ancestor certificates with tracked collection ItemIds retain and rebind the same selected edge across unrelated edits; removed/replaced native ownership rejects, and pending deletion resolves real current slots |
| Compositor canonical connection gestures | [Features/Compositor/EditorGame.CompositorConnections.cs](Features/Compositor/EditorGame.CompositorConnections.cs) | Real output slot/path/item/value and target renderer-instance/node certificates cross pending-draft/current-owner boundaries; native slot rebinding/type acceptance and existing graph connection journal preserve actual output-to-input workflow |
| Compositor native collection Add | [Documents/Compositor/AssetMetadataDocument.CompositorCollectionFactories.cs](Documents/Compositor/AssetMetadataDocument.CompositorCollectionFactories.cs), [Features/Compositor/EditorGame.CompositorCollectionFactories.cs](Features/Compositor/EditorGame.CompositorCollectionFactories.cs) | Actual absent/supplied native collection metadata, null-parameter/default or real factory AddNewItem dispatch, retained compositor owner/presenter targets and atomic native list/set/reference value preparation journals |
| Compositor dictionary key-only Add | [Documents/Compositor/AssetMetadataDocument.CompositorDictionaryDefaultAdd.cs](Documents/Compositor/AssetMetadataDocument.CompositorDictionaryDefaultAdd.cs), [Features/Compositor/EditorGame.CompositorDictionaryDefaultAdd.cs](Features/Compositor/EditorGame.CompositorDictionaryDefaultAdd.cs) | Shared native key-only controls, retained whole-owner/key/item/provider certificates, actual combined AddPrimitiveKey priority and alias-safe atomic native converter/default/reference-null/no-op journals |
| Compositor retained key/native item commands | [Features/Compositor/EditorGame.CompositorKeyDrafts.cs](Features/Compositor/EditorGame.CompositorKeyDrafts.cs), [Features/Compositor/EditorGame.CompositorReferenceLabels.cs](Features/Compositor/EditorGame.CompositorReferenceLabels.cs) | Retained native RenameStringKey text with full item/key/provider certificates and duplicate no-op journals; registered context commands over actual canonical presenters plus native reference labels |
| Compositor native enum artwork | [Features/Compositor/EditorGame.CompositorEnumArtwork.cs](Features/Compositor/EditorGame.CompositorEnumArtwork.cs) | Shared native image-enum and flag selected-list semantics over actual authored node/view identities, normalized single/combined journals and canonical commit failure-state restoration |
| Compositor native value factories | [Documents/Compositor/AssetMetadataDocument.CompositorValueFactories.cs](Documents/Compositor/AssetMetadataDocument.CompositorValueFactories.cs), [Features/Compositor/EditorGame.CompositorValueFactories.cs](Features/Compositor/EditorGame.CompositorValueFactories.cs) | Actual absent/empty/supplied member/item factory metadata and groups take priority; retained canonical CreateNewInstance presenter ownership or exact native entry generation over existing normalized journals, preserving inline/child/reference editors |
| Compositor selected-root native presentation | [Documents/Compositor/AssetMetadataDocument.CompositorPresenters.cs](Documents/Compositor/AssetMetadataDocument.CompositorPresenters.cs), [Documents/Compositor/AssetMetadataDocument.CompositorPresenterRows.cs](Documents/Compositor/AssetMetadataDocument.CompositorPresenterRows.cs) | Actual root-type providers/native trees over the same canonical graph, exact entry-point filtering, member/item/factory/numeric/virtual resolution, native display/visibility/category/inline metadata and epoch/lifetime refresh |
| Compositor native authored/virtual rows | [Features/Compositor/EditorGame.CompositorPresenterRows.cs](Features/Compositor/EditorGame.CompositorPresenterRows.cs), [Features/Compositor/EditorGame.CompositorVirtualRows.cs](Features/Compositor/EditorGame.CompositorVirtualRows.cs) | Actual native row ordering/categories/combined construction, retained scalar/provider controls and canonical owner/presenter/selection/source leases, normalized alias-safe virtual journals and native context commands |
| Compositor native complex children | [Documents/Compositor/AssetMetadataDocument.CompositorNativeChildren.cs](Documents/Compositor/AssetMetadataDocument.CompositorNativeChildren.cs), [Features/Compositor/EditorGame.CompositorNativeChildren.cs](Features/Compositor/EditorGame.CompositorNativeChildren.cs) | Cached adapters over actual emitted native member/item accessors and tracked collection IDs, exact native metadata/presenter ownership, real combination/expansion, existing retained controls and normalized structural/value journals; no shadow graph or invented virtual collection mutators |
| Compositor retained property string drafts | [Features/Compositor/EditorGame.CompositorStringDrafts.cs](Features/Compositor/EditorGame.CompositorStringDrafts.cs) | Dynamic shared text drafts retain actual canonical accessor/item/parent/provider epochs and owner leases through Save/close validation; real normalized single/combined compositor journals preserve source and readonly copying |
| UI collection structural eligibility | [Documents/UI/UIDocument.CollectionProperties.cs](Documents/UI/UIDocument.CollectionProperties.cs), [Features/UI/UIEditorPanel.CollectionProperties.cs](Features/UI/UIEditorPanel.CollectionProperties.cs) | Actual native separate Add/Remove descriptor/factory eligibility, readonly/item/ancestor guards and shared journals; grid definitions consume those same targets after retained draft validation |
| UI default collection Add | [Documents/UI/UIDocument.FactoryPresentation.cs](Documents/UI/UIDocument.FactoryPresentation.cs), [Features/UI/EditorGame.UIEditor.cs](Features/UI/EditorGame.UIEditor.cs) | Actual absent-versus-present native entry metadata selects null-parameter AddNewItem rather than a synthetic type chooser; retained canonical presenter command targets and atomic native default/reference construction journals |
| UI dictionary key-only Add | [Documents/UI/UIDocument.DictionaryDefaultAdd.cs](Documents/UI/UIDocument.DictionaryDefaultAdd.cs), [Features/UI/EditorGame.UIDictionaryDefaultAdd.cs](Features/UI/EditorGame.UIDictionaryDefaultAdd.cs) | Shared native string/numeric/enum/default-key controls retain exact selected UI owners/collection/item/provider certificates; actual AddPrimitiveKey command ownership or native converter/default/reference-null fallback with atomic no-op-safe journals |
| UI retained dictionary key rename | [Documents/UI/UIDocument.DictionaryKeys.cs](Documents/UI/UIDocument.DictionaryKeys.cs), [Features/UI/EditorGame.UIKeyDrafts.cs](Features/UI/EditorGame.UIKeyDrafts.cs) | Native RenameStringKey priority and unrestricted retained keys with real ordered selection/item/parent/provider certificates, actual owner/presenter lifetime, duplicate no-ops and atomic ItemId/override-preserving fallback journals |
| UI registered presenter commands/value lifetime | [Documents/UI/UIDocument.PresenterCommands.cs](Documents/UI/UIDocument.PresenterCommands.cs), [Features/UI/EditorGame.UIRegisteredCommands.cs](Features/UI/EditorGame.UIRegisteredCommands.cs) | Actual native member/collection/item/attached/virtual context commands retain original combination/parameter rules and canonical selection/provider identities; exact presenter command ownership and full journals, with real element-root leases only for value lifetime when filtered/default keys lack indexed presenters |
| UI toolbar structural command guards | [Features/UI/UIEditorPanel.Commands.cs](Features/UI/UIEditorPanel.Commands.cs), [Features/UI/UIEditorPanel.cs](Features/UI/UIEditorPanel.cs) | Existing current canonical/factory owner and pending-draft guards cover toolbar/keyboard clipboard, create/group/delete/duplicate; real destination index/capacity and native sibling/standard-panel policy are revalidated before actual graph journals; grouping constructs the native panel type rather than cloning a preset, preserves selected-child chronology and appends the group after sibling removal |
| UI deferred provider/collection/export ownership | [Features/UI/UIEditorPanel.Commands.cs](Features/UI/UIEditorPanel.Commands.cs), [Features/UI/UIEditorPanel.CollectionProperties.cs](Features/UI/UIEditorPanel.CollectionProperties.cs), [Features/UI/UIEditorPanel.Extensions.cs](Features/UI/UIEditorPanel.Extensions.cs) | Current canonical part/owner certificates plus pre-commit graph revision for captured native targets; real provider revalidation/collection journals and guarded export preparation replace the unowned pending Action queue |
| UI deferred property context actions | [Features/UI/UIEditorPanel.cs](Features/UI/UIEditorPanel.cs), [Features/UI/UIEditorPanel.Commands.cs](Features/UI/UIEditorPanel.Commands.cs) | Actual property clipboard/reset/enum/attached-entry targets retain canonical part IDs, source generation/revision/layout and pending-draft guards through the common command queue |
| UI child-layout commands | [Features/UI/UIEditorPanel.ChildLayout.cs](Features/UI/UIEditorPanel.ChildLayout.cs), [Documents/UI/UIDocument.ChildLayout.cs](Documents/UI/UIDocument.ChildLayout.cs) | Retained current-owner/draft-safe command queue, actual pin/grid dependency nodes and native StackPanel child collection reorder without hierarchy publication changes |
| UI library public-root export policy | [Documents/UI/UIDocument.LibraryExports.cs](Documents/UI/UIDocument.LibraryExports.cs) | Actual current graph root/Name events update native PublicUIElements nodes within the existing canonical journal; replay/compensation/readonly/stale/reentrancy gates preserve real history and untouched loaded metadata |
| UI canvas projection and layout snapping | [Features/UI/UIEditorViewport.EditorPicking.cs](Features/UI/UIEditorViewport.EditorPicking.cs), [Features/UI/UIEditorPanel.LayoutDrag.cs](Features/UI/UIEditorPanel.LayoutDrag.cs) | Actual engine render/picking axis conversion and virtual intersections; inverse-projected pointer positions, captured native SnapValue and adjusted UILayoutHelper movement within the canonical layout transaction |
| UI view colors and hover | [Features/UI/UIEditorPanel.ViewColors.cs](Features/UI/UIEditorPanel.ViewColors.cs) | Native Colors popup and weak per-document view preferences; configured actual canvas selection/guidelines/sizing bounds and engine-intersection hover highlighting |
| UI orthographic zoom commands | [Features/UI/UIEditorViewport.cs](Features/UI/UIEditorViewport.cs), [Resources/Icons/zoom-default.svg](Resources/Icons/zoom-default.svg) | Native default-size reset and positive +/-1 camera zoom preserving position; resize retains camera size; exact ImageZoomDefault WPF source geometry and prepared PNG |
| UI native camera input | [Features/UI/UIEditorPanel.CameraInput.cs](Features/UI/UIEditorPanel.CameraInput.cs), [Features/UI/UIEditorViewport.CameraInput.cs](Features/UI/UIEditorViewport.CameraInput.cs) | Original elapsed-time wheel/pan/modifier policy with actual package camera settings and per-window relative capture; distinct scene/UI owner tokens preserve input lifetime and forced global release |
| UI canvas pointer selection/transfer | [Features/UI/UIEditorPanel.CanvasPointer.cs](Features/UI/UIEditorPanel.CanvasPointer.cs), [Features/UI/UIEditorPanel.HierarchyDragDrop.cs](Features/UI/UIEditorPanel.HierarchyDragDrop.cs) | Native release threshold/Ctrl selection with real owner certificates; existing selected-adornment Alt source shares actual canonical hierarchy payload/target/history transfer and cancellation |
| UI camera user-settings persistence | [Features/UI/UIEditorViewport.CameraState.cs](Features/UI/UIEditorViewport.CameraState.cs), [Features/UI/UIEditorPanel.CameraPersistence.cs](Features/UI/UIEditorPanel.CameraPersistence.cs), [Features/UI/EditorGame.UICameraPersistence.cs](Features/UI/EditorGame.UICameraPersistence.cs) | Actual engine pose/lens captured and restored through native per-asset SceneSettingsData, full state across renderer generations, canonical .user ownership/guarded save and close failure preservation |
| UI selected-adornment gestures | [Features/UI/UIEditorPanel.SelectedAdornments.cs](Features/UI/UIEditorPanel.SelectedAdornments.cs) | Actual native multi-selection layer/depth ordering, shared projected handle/body/border hit geometry, hit-element-only move/resize and native directional/forbidden cursors |
| UI whole-asset properties command | [Features/UI/UIEditorPanel.AssetProperties.cs](Features/UI/UIEditorPanel.AssetProperties.cs) | Original UI properties button changes actual canonical inspector context with guarded queued ownership; selected authored part IDs/adorners remain and later part selection restores presenter roots |
| UI hierarchy native keyboard/range selection | [Features/UI/UIEditorPanel.HierarchyKeyboard.cs](Features/UI/UIEditorPanel.HierarchyKeyboard.cs) | Actual native tree row focus/geometry/depth and expanded projection drive keyboard/expansion/scroll; native Ctrl/Shift policies and ordered canonical part/parent/index/selection certificates cross existing queued owner/draft preflight before Inspector context refresh |
| Sprite native selection scopes/history | [Documents/Sprites/AssetMetadataDocument.SpriteSelectionScopes.cs](Documents/Sprites/AssetMetadataDocument.SpriteSelectionScopes.cs), [Features/Sprites/EditorGame.SpriteSelectionHistory.cs](Features/Sprites/EditorGame.SpriteSelectionHistory.cs) | Actual native collection-add notifications, pure tracked frame identities and selection-only clear/restore join the existing global canonical selection navigation; current-frame filtering, anchor order and release unregister keep graph journals separate |
| Native registered selection policy | [Features/Selection/EditorNativeSelectionScopes.cs](Features/Selection/EditorNativeSelectionScopes.cs), [Features/Selection/EditorGame.NativeSelectionScopes.cs](Features/Selection/EditorGame.NativeSelectionScopes.cs), [Features/Selection/EditorSelectionService.NativeScopes.cs](Features/Selection/EditorSelectionService.NativeScopes.cs) | Event-only Add exclusivity over actual canonical asset/UI/compositor/sprite/hierarchy collection owners and real hierarchy aliases; pure identity certificates, owner/view checks, pending preflight, replay/remap suppression and scoped native event cleanup; no additional selection/history model |
| UI canonical selection navigation scopes | [Features/UI/UIEditorPanel.SelectionHistory.cs](Features/UI/UIEditorPanel.SelectionHistory.cs), [Features/UI/EditorGame.UISelectionHistory.cs](Features/UI/EditorGame.UISelectionHistory.cs) | Existing active/inactive open-tab UI scopes capture ordered canonical asset/part IDs into the single global Core navigation stack; current native presenter selection restore, stale-part filtering and successful-close unregister preserve view/camera/authored history separation |
| UI ordered selection/primary scope | [Features/UI/UIElementSelection.cs](Features/UI/UIElementSelection.cs) | Actual ordered selected part IDs track native last-selected primary and active library root; property/context/transfer results and valid same-asset generation selection share this editor state |
| UI canvas View commands | [Features/UI/UIEditorPanel.ViewCommands.cs](Features/UI/UIEditorPanel.ViewCommands.cs) | Original native context zoom/reset-camera actions retain current source/viewport ownership, cancel actual gestures and update only the real camera; native .user persistence remains separate |
| UI source-library batch navigation | [Features/UI/UIEditorPanel.LibraryCommands.cs](Features/UI/UIEditorPanel.LibraryCommands.cs), [Features/UI/EditorGame.UILibraryNavigation.cs](Features/UI/EditorGame.UILibraryNavigation.cs) | Native all-selection distinct library resolution, actual live/canonical target ownership and path checks, explicit existing-tab activation, original missing-link/single-selection permissions |
| UI editor tab state | [Features/UI/UIEditorPanel.EditorState.cs](Features/UI/UIEditorPanel.EditorState.cs) | Actual ordered selection/display root/property scope retained weakly per editor document across tab activation; real view close resets transient state without asset/history writes |
| UI hierarchy recursive expansion | [Features/UI/UIEditorPanel.HierarchyExpansion.cs](Features/UI/UIEditorPanel.HierarchyExpansion.cs) | Original tree heading/toolbar, real native GUID/row storage scopes and bounded owner-certified expansion/collapse of hidden descendants; no authored graph/open-state shadow journal |
| Procedural editing | [Features/Assets/Procedural/EditorGame.ProceduralAssets.cs](Features/Assets/Procedural/EditorGame.ProceduralAssets.cs) | Documents/Procedural/ProceduralAssetDocument.cs and AssetPropertyField.cs |
| Compiled asset refresh | [Features/Assets/Compilation/EditorGame.AssetRefresh.cs](Features/Assets/Compilation/EditorGame.AssetRefresh.cs) | Projects compiler/content services |
| Texture import settings | [Features/Previews/Textures/EditorGame.TextureSettings.cs](Features/Previews/Textures/EditorGame.TextureSettings.cs) | AssetMetadataDocument.TextureSettings.cs and actual Quantum history |
| Thumbnail scheduling/cache | [Features/Previews/Thumbnails/AssetThumbnailService.cs](Features/Previews/Thumbnails/AssetThumbnailService.cs) | Shared engine passes and actual compiled content identities |
| Source text and semantic services | [Features/Scripts/ScriptTextEditor.cs](Features/Scripts/ScriptTextEditor.cs) | Documents/Scripts/ScriptSourceDocument.cs and ScriptSemanticWorkspace.cs |
| Source find/replace | [Features/Scripts/ScriptTextEditor.Search.cs](Features/Scripts/ScriptTextEditor.Search.cs) | ScriptSourceSearch.cs owns detached matching; canonical source document operations own replacement/history |
| Source editing commands | [Features/Scripts/ScriptTextEditor.Commands.cs](Features/Scripts/ScriptTextEditor.Commands.cs) | Canonical source edits/selection/gutter; Documents/Scripts/ScriptSourceDocument.Clipboard.cs preserves authored text and Infrastructure/Platform/WindowsScriptClipboard.cs owns actual native clipboard formats |
| Source input/geometry | [Features/Scripts/ScriptTextEditor.NativeSource.cs](Features/Scripts/ScriptTextEditor.NativeSource.cs) | SourceGeometry caches true grapheme/control geometry; Rectangles owns actual per-row source selection/editing; shared canonical document owns raw text/history/save |
| Source edit spans/newlines | [Documents/Scripts/ScriptSourceDocument.TextChanges.cs](Documents/Scripts/ScriptSourceDocument.TextChanges.cs) | Actual normalized edit spans preserve all untouched raw line separators, use native local newline insertion policy and publish one canonical compensating text operation |
| Source native drag/drop | [Features/Scripts/ScriptTextEditor.DragDrop.cs](Features/Scripts/ScriptTextEditor.DragDrop.cs) | Documents/Scripts/ScriptSourceDocument.DragDrop.cs guards real owner/version/raw/disk certificates; WindowsSourceTextDragDrop owns STA OLE data/target/source/timer lifetimes and forwards CF_HDROP to WindowsFileDrop; EditorGame.ScriptTextDragDrop owns completed-frame modal feedback ticks |
| Installed glyph fallback | [Infrastructure/Presentation/WindowsLinkedFonts.cs](Infrastructure/Presentation/WindowsLinkedFonts.cs) | Real Windows font-link order and SFNT collection face lookup feed explicit UI/source-font merges in EditorFontSetup; complex shaping is a separate native-layout concern |
| Native shaping foundation | [Infrastructure/Presentation/WindowsTextLayout.cs](Infrastructure/Presentation/WindowsTextLayout.cs) and its Glyphs/Inline/Colors/Raster partials | Counted original UTF16 DirectWrite layout, native bidi hit geometry, retained actual glyph/font-face snapshots, original-span inline boxes and native grayscale coverage; integrated with source viewport drawing/geometry; global-widget shaping remains separate |
| Native glyph GPU generation | [Infrastructure/Presentation/WindowsGlyphImages.cs](Infrastructure/Presentation/WindowsGlyphImages.cs) | Native coverage texture cache scoped to one renderer/device; real submitted-work completion markers, async drain and physical-loss proof preserve GPU ownership; host submission/drain/recovery hooks wired |
| Native source line adapter | [Infrastructure/Presentation/WindowsSourceLine.cs](Infrastructure/Presentation/WindowsSourceLine.cs) | Cached native glyph/color/inline-box rendering and physical caret/range/point geometry share the original source layout; source viewport, overlays, rectangular edits and native drag targets use this adapter |
| Native code-action source preview | [Features/Scripts/ScriptTextEditor.PreviewNativeSource.cs](Features/Scripts/ScriptTextEditor.PreviewNativeSource.cs) | Provider source snapshots use bounded native line/tile caches and the current glyph GPU owner; separate line-number gutters preserve original text and bidi layout |
| Native formatted read-only text | [Infrastructure/Presentation/WindowsFormattedText.cs](Infrastructure/Presentation/WindowsFormattedText.cs), [WindowsGlyphRunImage.cs](Infrastructure/Presentation/WindowsGlyphRunImage.cs), [EditorReadonlyRichText.cs](Infrastructure/Presentation/EditorReadonlyRichText.cs) | Wrapped real DirectWrite font/heading/link layout, native selection/hit/caret geometry, original-source Unicode copy and shared certified GPU tiles; About Backers actively uses it |
| Shared byte-indexed native text adapter | [Infrastructure/Presentation/EditorSelectableText.Native.cs](Infrastructure/Presentation/EditorSelectableText.Native.cs) | Native read-only geometry maps actual UTF8 source bytes to UTF16 for selection/link/highlights/CaretLine; About, Output, source assistance and shared host messages opt in while independent legacy callers retain the existing path |
| Output native text ownership | [Features/Output/OutputPanel.NativeText.cs](Features/Output/OutputPanel.NativeText.cs) | Build/Project channels borrow the shared renderer generation, retain real byte-indexed search/navigation, and suspend/dispose CPU views through host text lifecycle |
| Native common widget text | [Infrastructure/Presentation/EditorGame.NativeWidgets.cs](Infrastructure/Presentation/EditorGame.NativeWidgets.cs), [WindowsWidgetText.cs](Infrastructure/Presentation/WindowsWidgetText.cs) | Bounded real native label/link/button layout with original ImGui identities and native item/activation APIs; opt-in About/Inspector/Compositor routing, ordinary input/menu/tree/tab controls remain separate |
| Native checkbox/combo presentation | [Infrastructure/Presentation/EditorGame.NativeChoiceWidgets.cs](Infrastructure/Presentation/EditorGame.NativeChoiceWidgets.cs) | Native measured text with actual checkbox edited/mixed state and combo popup identity/lifecycle; scoped Settings/credentials/Inspector collection routing retains canonical/null ownership |
| Native dropdown selectable text | [Infrastructure/Presentation/EditorGame.NativeSelectable.cs](Infrastructure/Presentation/EditorGame.NativeSelectable.cs) | Original final ID and real native text dimensions with the actual toolkit Selectable owning selection/focus/navigation/popup closing; Settings and Inspector collection options opt in |
| Coupled native hierarchy tree text | [Infrastructure/Presentation/EditorNativeTreeGeometry.cs](Infrastructure/Presentation/EditorNativeTreeGeometry.cs), [EditorGame.NativeTrees.cs](Infrastructure/Presentation/EditorGame.NativeTrees.cs), [Features/Hierarchy/HierarchyPanel.Rows.cs](Features/Hierarchy/HierarchyPanel.Rows.cs) | Same actual native height for node behavior, row bands and virtualized rows; original tree state/IDs, icons, annotations and canonical hierarchy actions remain owned by existing services |
| Solution Explorer native tree text | [Infrastructure/Presentation/EditorTreeView.cs](Infrastructure/Presentation/EditorTreeView.cs), [Features/Assets/SolutionExplorerPanel.cs](Features/Assets/SolutionExplorerPanel.cs) | Opt-in native measure/paint/selectable callbacks, real tree virtualizer and stable folder/package/dependency identities; range chronology and rename/reveal submission preserved |
| Named edit history | [Features/History/EditorGame.EditHistory.cs](Features/History/EditorGame.EditHistory.cs) | Actual IEditorHistoryDocument transaction descriptors and guarded grouped operations |
| Composite selection navigation | [Features/Selection/EditorGame.SelectionHistory.cs](Features/Selection/EditorGame.SelectionHistory.cs) | Actual presented hierarchy owners, stable asset/entity identities and selection snapshots |
| Sprite-sheet authoring | [Features/Sprites/EditorGame.SpriteCanvas.cs](Features/Sprites/EditorGame.SpriteCanvas.cs) | Documents/Sprites holds canonical Quantum edits; real decoded textures use the shared engine renderer |
| Skybox authoring and preview | [Features/Skyboxes/EditorGame.SkyboxSettings.cs](Features/Skyboxes/EditorGame.SkyboxSettings.cs) | Documents/Skyboxes, production SkyboxCompiler and actual LightSkybox resources |
| Canonical asset inheritance | [Documents/EditorAssetGraphs.cs](Documents/EditorAssetGraphs.cs) | EditorAuthoringRegistry retains real base generations and EditorSessionHistory records actual Quantum operations |
| Visual-script authored members | [Documents/VisualScripts/VisualScriptDocument.cs](Documents/VisualScripts/VisualScriptDocument.cs) | Typed canonical AssetMetadataDocument subclass, actual VisualScriptAsset and shared Quantum/history ownership |
| Preview visibility and dispatch | [Features/Previews/EditorGame.AssetPreview.cs](Features/Previews/EditorGame.AssetPreview.cs) | Models/ and Textures/ own the actual engine adapters |
| Platform input or rendering | [Infrastructure/Input/ImGuiInput.cs](Infrastructure/Input/ImGuiInput.cs) | Infrastructure/Graphics/ImGuiRenderer.cs |
| Native clipboard/cursor/file chooser | [Infrastructure/Platform](Infrastructure/Platform) | Application/EditorGame.cs owns GUI-context lifetime |
| Shared local preferences | [Infrastructure/Preferences/EditorPreferencesStore.cs](Infrastructure/Preferences/EditorPreferencesStore.cs) | Application/EditorGame.Preferences.cs coordinates user settings |
| Icons | [Resources/Icons](Resources/Icons) | Infrastructure/Presentation/EditorIcons.cs |
| Existing diagnostic scenarios | [Diagnostics](Diagnostics) | External runners remain under tests/editor-imgui |

## Naming and boundaries

- Use `TypeName.cs` for a primary concrete type, such as `EngineViewport.cs`.
  Give reusable value descriptors their own discoverable files.
- Use `TypeName.Responsibility.cs` for partial implementations:
  `EditorGame.HierarchyDragDrop.cs`, `EntityHierarchyDocument.HierarchyDeletion.cs`,
  `UiWorkflowTest.CameraProjection.cs`. Name the responsibility, rather than
  creating another vaguely named `Panels`, `Helpers` or `Utils` file.
- Keep a feature's UI state next to its controls. Application state owns shared
  workspace/document/runtime references; it is not a bucket for feature-local fields.
- Keep authored mutations, Quantum graphs, identity, history and persistence in
  Documents. UI files invoke these operations; they do not implement another
  serialization or scene data model.
- Keep actual engine view/resource ownership with the scene/preview adapter.
  Infrastructure handles graphics/input transport, not authored scene semantics.
- Keep project compilation, cancellation and mounted bundle ownership in Projects.
  Feature refresh code coordinates those services.
- Shared property validation belongs in Properties. A scene-specific descriptor
  stays in Documents/Scene; a procedural descriptor stays in Documents/Procedural.
- Keep tests, capture hooks, benchmark helpers and the legacy procedural smoke scene
  under Diagnostics. This move preserves the current harness assembly/entry points;
  it does not establish a separate test project or change test behavior.
- Existing source retains the `Stride.Editor.ImGui` namespace because EditorGame
  and its diagnostic partials must remain one type across folders. Folder ownership
  is explicit; do not change one partial's namespace independently. Future extracted
  independent services may adopt subnamespaces deliberately.
- The shell orchestrates panel calls. New substantial surfaces get their own feature
  file/directory rather than returning to a monolithic DrawPanels method.
- Keep one responsibility per file where practical. Extract actual shared behavior
  when two workflows need it; avoid speculative service/framework layers.

## Resources and external paths

Generate icons from this project directory with:

```text
node Resources/Icons/build-icons.cjs [absolute-path-to-sharp-module]
```

SVGs and PNGs stay together so the builder's relative-path behavior is retained.
MSBuild maps source `Resources/Icons/*.png` to output `Icons/*.png`; the runtime
resource path is unchanged. The project/solution paths and executable output path
are also unchanged. C# sources are discovered recursively by the SDK.

The visibility measurement script's source-hash paths now point to the relocated
host and measurement files. Existing diagnostic scenario bodies are moved without
adding or running tests.

## Former filename map

The large `EditorPanels.cs` was split among Shell, MainMenu, WorkspacePanel,
ProjectLoading, DocumentCommands, Hierarchy, SceneViewport, Inspector and
AssetBrowser files. The table identifies the other primary relocations; use the
task table above for extracted methods and descriptors.

| Former project-root file | Current project-relative path |
|---|---|
| EditorGame.cs | [Application/EditorGame.cs](Application/EditorGame.cs) |
| EditorPanels.cs | [Application/EditorGame.Shell.cs](Application/EditorGame.Shell.cs) |
| EditorClosing.cs | [Application/EditorGame.Closing.cs](Application/EditorGame.Closing.cs) |
| CompiledProjectBuild.cs | [Projects/CompiledProjectBuild.cs](Projects/CompiledProjectBuild.cs) |
| CompiledProjectContent.cs | [Projects/CompiledProjectContent.cs](Projects/CompiledProjectContent.cs) |
| ProjectWorkspace.cs | [Projects/ProjectWorkspace.cs](Projects/ProjectWorkspace.cs) |
| SceneDocument.cs | [Documents/Entities/EntityHierarchyDocument.cs](Documents/Entities/EntityHierarchyDocument.cs) |
| HierarchyEditing.cs | [Documents/Entities/EntityHierarchyDocument.HierarchyCreation.cs](Documents/Entities/EntityHierarchyDocument.HierarchyCreation.cs) |
| HierarchyDeletion.cs | [Documents/Entities/EntityHierarchyDocument.HierarchyDeletion.cs](Documents/Entities/EntityHierarchyDocument.HierarchyDeletion.cs) |
| HierarchyReparenting.cs | [Documents/Entities/EntityHierarchyDocument.HierarchyReparenting.cs](Documents/Entities/EntityHierarchyDocument.HierarchyReparenting.cs) |
| ComponentEditing.cs | [Documents/Entities/EntityHierarchyDocument.ComponentEditing.cs](Documents/Entities/EntityHierarchyDocument.ComponentEditing.cs) |
| ProceduralAssetDocument.cs | [Documents/Procedural/ProceduralAssetDocument.cs](Documents/Procedural/ProceduralAssetDocument.cs) |
| PropertyNumericRange.cs | [Properties/PropertyNumericRange.cs](Properties/PropertyNumericRange.cs) |
| PropertyValueValidation.cs | [Properties/PropertyValueValidation.cs](Properties/PropertyValueValidation.cs) |
| EngineViewport.cs | [Features/Scene/EngineViewport.cs](Features/Scene/EngineViewport.cs) |
| EditorViewportCamera.cs | [Features/Scene/Camera/EditorViewportCamera.cs](Features/Scene/Camera/EditorViewportCamera.cs) |
| CameraLens.cs | [Features/Scene/Camera/CameraLens.cs](Features/Scene/Camera/CameraLens.cs) |
| SceneCameraSettingsStore.cs | [Features/Scene/Camera/SceneCameraSettingsStore.cs](Features/Scene/Camera/SceneCameraSettingsStore.cs) |
| SceneCameraPanels.cs | [Features/Scene/Camera/EditorGame.Camera.cs](Features/Scene/Camera/EditorGame.Camera.cs) |
| CameraSettingsPersistence.cs | [Features/Scene/Camera/EditorGame.CameraPersistence.cs](Features/Scene/Camera/EditorGame.CameraPersistence.cs) |
| EntityRenaming.cs | [Features/Hierarchy/EditorGame.EntityRename.cs](Features/Hierarchy/EditorGame.EntityRename.cs) |
| EntityCreationFactories.cs | [Features/Hierarchy/EntityCreationFactories.cs](Features/Hierarchy/EntityCreationFactories.cs) |
| EntityFactoryPanels.cs | [Features/Hierarchy/EditorGame.EntityFactories.cs](Features/Hierarchy/EditorGame.EntityFactories.cs) |
| HierarchyDeletionPanels.cs | [Features/Hierarchy/EditorGame.HierarchyDeletion.cs](Features/Hierarchy/EditorGame.HierarchyDeletion.cs) |
| HierarchyMovePanels.cs | [Features/Hierarchy/EditorGame.HierarchyMove.cs](Features/Hierarchy/EditorGame.HierarchyMove.cs) |
| HierarchyDragDrop.cs | [Features/Hierarchy/EditorGame.HierarchyDragDrop.cs](Features/Hierarchy/EditorGame.HierarchyDragDrop.cs) |
| ComponentInspector.cs | [Features/Inspector/EditorGame.ComponentInspector.cs](Features/Inspector/EditorGame.ComponentInspector.cs) |
| ComponentColors.cs | [Features/Inspector/EditorGame.ComponentColors.cs](Features/Inspector/EditorGame.ComponentColors.cs) |
| ComponentFlags.cs | [Features/Inspector/EditorGame.ComponentFlags.cs](Features/Inspector/EditorGame.ComponentFlags.cs) |
| ComponentIntegers.cs | [Features/Inspector/EditorGame.ComponentIntegers.cs](Features/Inspector/EditorGame.ComponentIntegers.cs) |
| ComponentVectors.cs | [Features/Inspector/EditorGame.ComponentVectors.cs](Features/Inspector/EditorGame.ComponentVectors.cs) |
| SceneReferenceInspector.cs | [Features/Inspector/EditorGame.SceneReferences.cs](Features/Inspector/EditorGame.SceneReferences.cs) |
| AssetEditingPanels.cs | [Features/Assets/Procedural/EditorGame.ProceduralAssets.cs](Features/Assets/Procedural/EditorGame.ProceduralAssets.cs) |
| MaterialReferencePanels.cs | [Features/Assets/Procedural/EditorGame.MaterialPicker.cs](Features/Assets/Procedural/EditorGame.MaterialPicker.cs) |
| AssetPreviewUpdating.cs | [Features/Assets/Compilation/EditorGame.AssetRefresh.cs](Features/Assets/Compilation/EditorGame.AssetRefresh.cs) |
| AssetPreviewPanels.cs | [Features/Previews/EditorGame.AssetPreview.cs](Features/Previews/EditorGame.AssetPreview.cs) |
| ModelPreviewPanels.cs | [Features/Previews/Models/EditorGame.ModelPreview.cs](Features/Previews/Models/EditorGame.ModelPreview.cs) |
| ModelAssetPreview.cs | [Features/Previews/Models/ModelAssetPreview.cs](Features/Previews/Models/ModelAssetPreview.cs) |
| TextureAssetPreview.cs | [Features/Previews/Textures/TextureAssetPreview.cs](Features/Previews/Textures/TextureAssetPreview.cs) |
| ImGuiRenderer.cs | [Infrastructure/Graphics/ImGuiRenderer.cs](Infrastructure/Graphics/ImGuiRenderer.cs) |
| ImGuiInput.cs | [Infrastructure/Input/ImGuiInput.cs](Infrastructure/Input/ImGuiInput.cs) |
| DevelopmentShaders.cs | [Infrastructure/Graphics/EditorGame.ShaderSources.cs](Infrastructure/Graphics/EditorGame.ShaderSources.cs) |
| EditorIcons.cs | [Infrastructure/Presentation/EditorIcons.cs](Infrastructure/Presentation/EditorIcons.cs) |
| DocumentRegression.cs | [Diagnostics/Regression/DocumentRegression.cs](Diagnostics/Regression/DocumentRegression.cs) |
| CameraProjectionRegression.cs | [Diagnostics/Regression/CameraProjectionRegression.cs](Diagnostics/Regression/CameraProjectionRegression.cs) |
| CameraSettingsRegression.cs | [Diagnostics/Regression/CameraSettingsRegression.cs](Diagnostics/Regression/CameraSettingsRegression.cs) |
| HierarchyCreationRegression.cs | [Diagnostics/Regression/HierarchyCreationRegression.cs](Diagnostics/Regression/HierarchyCreationRegression.cs) |
| HierarchyDeletionRegression.cs | [Diagnostics/Regression/HierarchyDeletionRegression.cs](Diagnostics/Regression/HierarchyDeletionRegression.cs) |
| HierarchyReparentingRegression.cs | [Diagnostics/Regression/HierarchyReparentingRegression.cs](Diagnostics/Regression/HierarchyReparentingRegression.cs) |
| HierarchyOrderingRegression.cs | [Diagnostics/Regression/HierarchyOrderingRegression.cs](Diagnostics/Regression/HierarchyOrderingRegression.cs) |
| PickingRequestRegression.cs | [Diagnostics/Regression/PickingRequestRegression.cs](Diagnostics/Regression/PickingRequestRegression.cs) |
| ProceduralAssetRegression.cs | [Diagnostics/Regression/ProceduralAssetRegression.cs](Diagnostics/Regression/ProceduralAssetRegression.cs) |
| UiWorkflowTest.cs | [Diagnostics/Automation/UiWorkflowTest.cs](Diagnostics/Automation/UiWorkflowTest.cs) |
| UiControls.cs | [Diagnostics/Automation/UiControls.cs](Diagnostics/Automation/UiControls.cs) |
| UiAssetEditorWorkflowTest.cs | [Diagnostics/Automation/UiWorkflowTest.AssetEditor.cs](Diagnostics/Automation/UiWorkflowTest.AssetEditor.cs) |
| UiCameraProjectionWorkflowTest.cs | [Diagnostics/Automation/UiWorkflowTest.CameraProjection.cs](Diagnostics/Automation/UiWorkflowTest.CameraProjection.cs) |
| UiHierarchyCreationWorkflowTest.cs | [Diagnostics/Automation/UiWorkflowTest.HierarchyCreation.cs](Diagnostics/Automation/UiWorkflowTest.HierarchyCreation.cs) |
| UiHierarchyDeletionWorkflowTest.cs | [Diagnostics/Automation/UiWorkflowTest.HierarchyDeletion.cs](Diagnostics/Automation/UiWorkflowTest.HierarchyDeletion.cs) |
| UiHierarchyMoveWorkflowTest.cs | [Diagnostics/Automation/UiWorkflowTest.HierarchyMove.cs](Diagnostics/Automation/UiWorkflowTest.HierarchyMove.cs) |
| UiModelTypeWorkflowTest.cs | [Diagnostics/Automation/UiWorkflowTest.ModelType.cs](Diagnostics/Automation/UiWorkflowTest.ModelType.cs) |
| UiNumericRangeWorkflowTest.cs | [Diagnostics/Automation/UiWorkflowTest.NumericRanges.cs](Diagnostics/Automation/UiWorkflowTest.NumericRanges.cs) |
| UiPlaneAssetWorkflowTest.cs | [Diagnostics/Automation/UiWorkflowTest.PlaneAsset.cs](Diagnostics/Automation/UiWorkflowTest.PlaneAsset.cs) |
| UiPreviewVisibilityTest.cs | [Diagnostics/Automation/UiWorkflowTest.PreviewVisibility.cs](Diagnostics/Automation/UiWorkflowTest.PreviewVisibility.cs) |
| UiScenePickingWorkflowTest.cs | [Diagnostics/Automation/UiWorkflowTest.ScenePicking.cs](Diagnostics/Automation/UiWorkflowTest.ScenePicking.cs) |
| UiVectorWorkflowTest.cs | [Diagnostics/Automation/UiWorkflowTest.Vectors.cs](Diagnostics/Automation/UiWorkflowTest.Vectors.cs) |
| UiControlMeasurements.cs | [Diagnostics/Measurements/EditorGame.UiControlMeasurements.cs](Diagnostics/Measurements/EditorGame.UiControlMeasurements.cs) |
| SceneDrawMeasurements.cs | [Diagnostics/Measurements/SceneDrawMeasurements.cs](Diagnostics/Measurements/SceneDrawMeasurements.cs) |
| ScenePreview.cs | [Diagnostics/Smoke/SmokeTestScene.cs](Diagnostics/Smoke/SmokeTestScene.cs) |

Organization record: 2026-10-10. The first compile also exposed a missing
Stride.Engine.Processors import in the previously uncompiled entity factory; that import
was corrected. The final Release/D3D11 solution build passes with zero errors.
All source-guide links resolve and all 15 icon PNGs copy to the unchanged output
Icons directory. No application tests or UI workflows ran. Build evidence is
recorded separately from workflow verification.

Shared native text GPU ownership lives in `Infrastructure/Presentation/EditorGame.NativeTextRendering.cs`. Source editors and opted-in read-only/About controls borrow one device/renderer generation; the host submits real work before retirement certificates, suspends borrowing before drain, and releases owners only after certified completion. Feature controls own their CPU layouts and authored byte mappings.

Optional native tab/title/overflow text hooks live in `Infrastructure/Platform/WindowsImGuiTabTextHooks.cs` and `Infrastructure/Presentation/EditorGame.NativeTabs.cs`. They bind only the already loaded exact native context/version and borrow the same shaped text/glyph owners; the package renderer remains fallback. Reproducible source/ABI approval and opt-in native build metadata live under `build/native/editor-imgui/`, with exact source revisions and deferred toolchain/evidence documented there.

`Infrastructure/Graphics/ImGuiRenderer.Textures.cs` owns borrowed texture registrations, atlas request processing and device-texture detachment. The main renderer file owns geometry uploads and draw submission; both share one device-generation owner. Atlas retries retain allocation identities, and pixel buffer/format/dimension checks precede native uploads.

`Projects/EditorGame.LoadProgress.cs` presents project-operation progress. `ProjectLoadProgressCapture.cs` owns thread-safe bounded compiler-line snapshots for a single actual task; loading/switching callers retain cancellation and adoption decisions.

Source document-group mutation authorization lives in `Features/Scripts/EditorGame.Scripts.cs` and `ScriptTextEditor` partials. The host captures the actual open entry/published canonical owner and active group; custom native source input, retained assistance, clipboard/drop and undo consumers revalidate that callback before authored changes. Read-only selection/copy/navigation retain their own view state. OLE source moves retain original authorized canonical ownership/version/hash certificates across focus changes rather than borrowing another active document. Group integration is a separate source checkpoint.

`Features/Scene/EditorGame.SceneToolbarContext.cs` binds scene toolbar presentation to the exact `SceneDocumentView` and `EngineViewport`. Existing control families accept an optional explicit context without changing global authored document pointers. `Camera/EditorGame.SceneDisplaySettings.cs` and `EditorGame.SceneToolsPersistence.cs` read/write actual native per-asset package view settings using that same viewport; the protected host must call `SaveSceneDocumentViewSettingsBeforeRetirement` before clearing native grid/gizmo objects. `Camera/EditorGame.SceneDocumentCamera.cs` owns per-view native relative capture and readonly camera gestures. Actual actor activation captures/restores control state, and same-owner viewport recreation transfers transient controls; authored picking/gizmo/capture commands retain the canonical active-owner boundary.

`Features/Previews/Textures/CompiledTextureConsumers.Usage.cs` owns readonly identity traversal of actual native material parameter objects and serialized runtime consumer families. `EditorGame.TextureConsumers.cs` queries active and physically retiring owners; `Features/Scene/EngineViewport.TextureConsumers.cs` includes loaded caches and child/prepared runtime owners. Detached `SceneViewportResources` retain real consumer roots until completed native cleanup. The native `ContentManager.GetLoadedAssetsSnapshot` lock-protected API exposes only actual not-yet-released cache assets. `EditorGame.SceneTextureOwnership.cs` owns real mounted-content leases and retryable pre-unload consumer guards; the ordinary preview retirement actor still requires native GPU completion proof.

`Features/Previews/Sprites/SpriteSheetCacheResources.cs` owns each actual native cache and mounted sprite bundle lease, including failed/recovery loading generations. `CompiledSpriteSheetContent.cs` retains the immutable compiled mount across resource rebinding and guards native cache release against real sheet/atlas/preview consumers. `Features/Sprites/EditorGame.SpriteCompilation.cs` owns installed-generation binding retries and per-success retirement transfers; `SpriteSheetAssetPreview.cs` holds an independent bundle/cache lease through completed native preview disposal. Elapsed-frame scheduling is never GPU completion evidence.

- `Features/Fonts/EditorGame.FontRetirement.cs` owns explicit font graphics pre-retirement and exact source/preview restoration snapshots. `retireFonts` is opt-in on protected GPU barriers: generic barriers preserve installed fonts; project, terminal, platform, empty-workspace and device transitions drain native bitmap workers and actual font resources before final proof.
