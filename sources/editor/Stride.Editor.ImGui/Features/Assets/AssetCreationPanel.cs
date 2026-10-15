using Hexa.NET.ImGui;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Templates;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class AssetCreationPanel(string popupIdentity = "AssetCreation")
{
    internal sealed record Choice(string Name, IAssetFactory<Asset>? Factory, TemplateAssetDescription? Template, EditorAssetTemplateAdapterLease? Adapter = null)
    {
        internal Type AssetType => Adapter?.Adapter.AssetType ?? Factory!.AssetType;
        internal EditorAssetTemplateParameterContext? NativeParameters { get; set; }
        internal string? DefaultNamespace { get; set; }
    }
    private readonly List<EditorAssetTemplateAdapterLease> failedTemplateProviderCleanup = [];
    private Func<Choice, string?>? suggestTemplateName;
    internal string PopupIdentity => popupIdentity;
    internal ContentReferencePicker? ReferenceDialog { get; set; }
    private string CreationTitle => EditorLocalization.Text("Add asset...") + "###" + popupIdentity;
    private string ScriptSaveTitle => Stride.GameStudio.Helpers.StrideGameStudio.EditorName + "###" + popupIdentity + "/Scripts";
    private Choice[] choices = [];
    private Choice? selected;
    private AssetItem[] references = [];
    private AssetId? reference;
    private PackageSession? session;
    private string filter = "", name = "", sourceNamespace = "";
    private bool requestOpen, requestScriptSave, alwaysSaveScripts;
    private Action<bool, bool>? saveScript;
    private readonly EditorSelectableText scriptSaveMessage = new();
    internal Action<string, string>? NativeMessageRenderer { get; set; }
    internal Action<Choice, System.Numerics.Vector2, System.Numerics.Vector2>? NativeTemplateRowRenderer { get; set; }
    private Action<Choice, string, bool, AssetId?, string>? create;
    internal bool HasRequest => failedTemplateProviderCleanup.Count != 0 || create != null || requestOpen || resolveWithoutSource != null || saveScript != null;
    public void Open(PackageSession session, Action<Choice, string, bool, AssetId?, string> create, bool supportsProjectAssets = false, bool codeOnly = false, string defaultNamespace = "",
        Func<TemplateAssetDescription, EditorAssetTemplateAdapterLease?>? acquireAdapter = null, Package? targetPackage = null, string targetLocation = "", AssetItem[]? selectedAssets = null,
        Func<AssetItem, AssetItem>? snapshotAsset = null,
        Func<Type, (Package Package, string Location, string Namespace)>? resolveTarget = null, Func<Choice, string?>? suggestName = null)
    {
        ReleaseAdapters(); this.session = session; sourceNamespace = defaultNamespace; suggestTemplateName = suggestName;
        AssetTemplateCatalog.EnsureRegistered();
        var available = new List<Choice>();
        try
        {
        foreach (var template in TemplateManager.FindTemplates(TemplateScope.Asset, session).OfType<TemplateAssetFactoryDescription>())
        {
            if (template.GetFactory() is not { } factory || !Supports(factory, supportsProjectAssets)) continue;
            available.Add(new(template.Name, factory, template));
        }
        foreach (var factory in AssetRegistry.GetAllAssetFactories())
        {
            if (!Supports(factory, supportsProjectAssets) || available.Any(choice => choice.Factory?.GetType() == factory.GetType() && choice.AssetType == factory.AssetType)) continue;
            var title = DisplayAttribute.GetDisplayName(factory.AssetType) ?? factory.AssetType.Name;
            available.Add(new(title, factory, null));
        }
        if (supportsProjectAssets)
            foreach (var template in TemplateManager.FindTemplates(TemplateScope.Asset, session).OfType<TemplateAssetDescription>())
                if (template.AssetTypeName == nameof(Stride.Assets.Scripts.ScriptSourceFileAsset))
                    available.Add(new(template.Name, new DefaultAssetFactory<Stride.Assets.Scripts.ScriptSourceFileAsset>(), template));
        if (acquireAdapter != null && targetPackage != null)
            foreach (var template in TemplateManager.FindTemplates(TemplateScope.Asset, session).OfType<TemplateAssetDescription>())
            {
                var adapter = acquireAdapter(template);
                if (adapter == null) continue;
                var type = adapter.Adapter.AssetType;
                if (AssetRegistry.GetDefaultExtension(type) == null || typeof(IProjectAsset).IsAssignableFrom(type) && !supportsProjectAssets) { adapter.Dispose(); continue; }
                var choice = new Choice(template.Name, null, template, adapter);
                // Track the lease before constructing parameters so a failed initialization retires it.
                available.Add(choice);
                var target = resolveTarget?.Invoke(type) ?? (Package: targetPackage, Location: targetLocation, Namespace: defaultNamespace);
                choice.NativeParameters = new(new AssetTemplateGeneratorParameters(new Stride.Core.IO.UDirectory(target.Location))
                {
                    Package = target.Package, Description = template, Namespace = target.Namespace, Name = template.DefaultOutputName ?? template.Name,
                    OutputDirectory = target.Package.RootDirectory, Logger = new Stride.Core.Diagnostics.LoggerResult(), Unattended = false,
                }, selectedAssets ?? [], snapshotAsset);
                available.RemoveAll(existing => !ReferenceEquals(existing, choice) && ReferenceEquals(existing.Template, template));
            }
        foreach (var choice in available) choice.DefaultNamespace = resolveTarget?.Invoke(choice.AssetType).Namespace ?? defaultNamespace;
        choices = available.OrderBy(choice => choice.Template?.Order ?? 0).ThenBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        BuildTemplateGroups(); selected = null; sourceNamespace = defaultNamespace; SelectReferences(); filter = ""; name = ""; this.create = create; requestOpen = true; focusTemplateSearch = true;
        }
        catch (Exception initialization)
        {
            choices = []; selected = null;
            List<Exception>? cleanupFailures = null;
            foreach (var choice in available)
                try { if (choice.Adapter != null) RetireTemplateProviderLease(choice.Adapter); }
                catch (Exception cleanup) { (cleanupFailures ??= []).Add(cleanup); }
            if (cleanupFailures != null) throw new AggregateException("Template catalog initialization and provider cleanup failed.", new[] { initialization }.Concat(cleanupFailures));
            throw;
        }
    }
    internal void OpenPreparedTemplate(PackageSession current, Choice choice, string defaultNamespace,
        Action<Choice, string, bool, AssetId?, string> create)
    {
        try
        {
            ReleaseAdapters(); session = current; sourceNamespace = defaultNamespace; choices = [choice]; selected = choice; choice.DefaultNamespace = defaultNamespace;
            BuildTemplateGroups(); SelectReferences(); filter = ""; name = choice.Template?.RequireName == false ? "" : choice.Template?.DefaultOutputName ?? choice.Name; this.create = create; requestOpen = true; focusTemplateParameters = true;
        }
        catch (Exception initialization)
        {
            try
            {
                if (choices.Any(value => ReferenceEquals(value, choice))) Clear();
                else if (choice.Adapter != null) RetireTemplateProviderLease(choice.Adapter);
            }
            catch (Exception cleanup) { throw new AggregateException("Template preparation and provider cleanup failed.", initialization, cleanup); }
            throw;
        }
    }
    private static bool Supports(IAssetFactory<Asset> factory, bool supportsProjectAssets) => !typeof(SourceCodeAsset).IsAssignableFrom(factory.AssetType)
        && (!typeof(IProjectAsset).IsAssignableFrom(factory.AssetType) || supportsProjectAssets && factory.AssetType == typeof(Stride.Assets.Scripts.VisualScriptAsset)) && AssetRegistry.GetDefaultExtension(factory.AssetType) != null;
    public void Draw(Action<Action> execute)
    {
        DrainFailedTemplateProviders();
        DrawWithoutSourcePrompt(execute);
        DrawScriptSavePrompt(execute);
        if (referencePickerPending) return;
        if (requestOpen) { Gui.OpenPopup(CreationTitle); requestOpen = false; }
        using var modal = ImGuiScopes.Modal(CreationTitle, ImGuiWindowFlags.AlwaysAutoResize);
        if (!modal.DrawContents) return;
        if (create == null) { Gui.CloseCurrentPopup(); return; }
        Gui.SetNextItemWidth(668 * Gui.GetStyle().FontScaleDpi);
        if (focusTemplateSearch) { Gui.SetKeyboardFocusHere(); focusTemplateSearch = false; }
        var searchId = Gui.GetID("##AssetTemplateFilter");
        var searchOwned = Gui.GetCurrentContext().ActiveId == searchId;
        if (Gui.InputTextWithHint("##AssetTemplateFilter", EditorLocalization.Text("Search"), ref filter, 256))
        {
            selectedTemplateGroup = null; templateChoicesDirty = true;
            var first = VisibleTemplateChoices().FirstOrDefault();
            execute(() => SelectTemplateChoice(first));
            scrollTemplateChoice = first;
        }
        RecordControl?.Invoke("template/search");
        ReadTemplateSearchNavigation(searchOwned || Gui.IsItemActive(), searchId, execute);
        if (create == null) return;
        var hasGroups = templateRoot.Children.Count != 0;
        if (hasGroups) { DrawTemplateGroups(); Gui.SameLine(); }
        using (var child = ImGuiScopes.Child("Asset templates", new System.Numerics.Vector2(hasGroups ? 500 : 668, 240) * Gui.GetStyle().FontScaleDpi, ImGuiChildFlags.Borders, ImGuiWindowFlags.None))
        {
            if (child.DrawContents)
                foreach (var choice in VisibleTemplateChoices())
                {
                    Gui.PushID((choice.Adapter?.Adapter.Generator.GetType().FullName ?? choice.Factory!.GetType().FullName) + "/" + choice.Template?.Id + "/" + choice.AssetType.FullName);
                    Gui.PushStyleColor(ImGuiCol.Text, System.Numerics.Vector4.Zero);
                    bool clicked;
                    try { clicked = Gui.Selectable(choice.Name, ReferenceEquals(choice, selected), ImGuiSelectableFlags.AllowOverlap,
                        new System.Numerics.Vector2(0, 56 * Gui.GetStyle().FontScaleDpi)); }
                    finally { Gui.PopStyleColor(); }
                    if (Gui.IsItemVisible())
                    {
                        RecordControl?.Invoke(TemplateChoiceControl(choice));
                        if (NativeTemplateRowRenderer != null) NativeTemplateRowRenderer(choice, Gui.GetItemRectMin(), Gui.GetItemRectMax());
                        else Gui.GetWindowDrawList().AddText(Gui.GetItemRectMin(), Gui.GetColorU32(ImGuiCol.Text), choice.Name);
                    }
                    if (ReferenceEquals(scrollTemplateChoice, choice)) { Gui.SetScrollHereY(); scrollTemplateChoice = null; }
                    var rowId = Gui.GetID(choice.Name);
                    var context = Gui.GetCurrentContext();
                    var keyboardValidation = TemplateModalOwnsInput() && context.NavId == rowId && (context.ActiveId == 0 || context.ActiveId == rowId)
                        && (Gui.IsKeyPressed(ImGuiKey.Enter, false) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter, false));
                    if (clicked || keyboardValidation)
                    {
                        var validate = TemplateModalOwnsInput() && (Gui.IsMouseReleased(ImGuiMouseButton.Left) || keyboardValidation);
                        if (validate && keyboardValidation) { OwnTemplateKey(ImGuiKey.Enter, rowId); OwnTemplateKey(ImGuiKey.KeypadEnter, rowId); }
                        if (validate) ValidateTemplateChoice(choice, execute);
                        else execute(() => SelectTemplateChoice(choice));
                    }
                    Gui.PopID();
                    if (create == null) break;
                }
        }
        if (create == null) return;
        var formAccepted = false;
        if (focusTemplateParameters) { Gui.SetKeyboardFocusHere(); focusTemplateParameters = false; }
        if (selected?.Template?.RequireName != false)
        { Gui.SetNextItemWidth(280 * Gui.GetStyle().FontScaleDpi); var nameId = Gui.GetID(selected?.AssetType == typeof(Stride.Assets.Scripts.ScriptSourceFileAsset) ? "Class:" : "Name");
        var nameOwned = Gui.GetCurrentContext().ActiveId == nameId;
        if (NativeStringInput.Draw(selected?.AssetType == typeof(Stride.Assets.Scripts.ScriptSourceFileAsset) ? EditorLocalization.Widget("Class:") : "Name", ref name, selectAllOnFocus: selected?.AssetType == typeof(Stride.Assets.Scripts.ScriptSourceFileAsset))) templateNameDefined = name.Length != 0;
        RecordControl?.Invoke("template/name");
        formAccepted = nameOwned && TemplateModalOwnsInput() && (Gui.IsKeyPressed(ImGuiKey.Enter, false) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter, false));
        if (formAccepted) { OwnTemplateKey(ImGuiKey.Enter, nameId); OwnTemplateKey(ImGuiKey.KeypadEnter, nameId); } }
        if (selected?.AssetType == typeof(Stride.Assets.Scripts.ScriptSourceFileAsset))
        {
            Gui.SetNextItemWidth(280 * Gui.GetStyle().FontScaleDpi); var namespaceId = Gui.GetID("Namespace:");
            var namespaceOwned = Gui.GetCurrentContext().ActiveId == namespaceId;
            NativeStringInput.Draw(EditorLocalization.Widget("Namespace:"), ref sourceNamespace);
            var acceptNamespace = namespaceOwned && TemplateModalOwnsInput()
                && (Gui.IsKeyPressed(ImGuiKey.Enter, false) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter, false));
            if (acceptNamespace) { OwnTemplateKey(ImGuiKey.Enter, namespaceId); OwnTemplateKey(ImGuiKey.KeypadEnter, namespaceId); formAccepted = true; }
        }
        if (selected != null && AssetCreationReferenceKind(selected.Template?.Id) is { } kind)
        {
            if (Gui.Button(kind.Name + "###TemplateReference")) execute(() => BrowseTemplateReference(selected));
            RecordControl?.Invoke("template/reference/open");
            var selectedReference = references.FirstOrDefault(item => item.Id == reference)?.Location.ToString();
            if (selectedReference != null) NativeMessageRenderer?.Invoke("##TemplateReferenceValue", selectedReference);
        }
        if (referencePickerPending) return;
        if (selected?.NativeParameters is { } native)
        {
            native.Parameters.Name = name; native.Parameters.Namespace = sourceNamespace;
            execute(() => { selected.Adapter!.Validate(); selected.Adapter.Adapter.DrawParameters?.Invoke(native); });
        }
        using (ImGuiScopes.Disabled(selected == null || selected.Template?.RequireName != false && string.IsNullOrWhiteSpace(name)))
            if (Gui.Button(selected?.AssetType == typeof(Stride.Assets.Scripts.ScriptSourceFileAsset)
                ? EditorLocalization.Widget("Create script###Create", "Button") : EditorLocalization.Widget("Create", "Button"))) AcceptTemplate(execute);
        RecordControl?.Invoke("template/create");
        if (formAccepted) AcceptTemplate(execute);
        if (create == null) return;
        Gui.SameLine();
        var cancelKey = TemplateModalOwnsInput() && !Gui.GetIO().WantTextInput && !Gui.IsAnyItemActive() && Gui.IsKeyPressed(ImGuiKey.Escape, false);
        var cancelClicked = Gui.Button(EditorLocalization.Widget("Cancel", "Button"));
        RecordControl?.Invoke("template/cancel");
        if (cancelClicked || cancelKey)
        {
            if (cancelKey) OwnTemplateKey(ImGuiKey.Escape, Gui.GetID("AssetTemplateCancel"));
            create = null; ReleaseAdapters(); Gui.CloseCurrentPopup();
        }
    }
    internal void RequestScriptSave(Action<bool, bool> save)
    { saveScript = save; requestScriptSave = true; alwaysSaveScripts = false; }
    private void DrawScriptSavePrompt(Action<Action> execute)
    {
        if (requestScriptSave) { Gui.OpenPopup(ScriptSaveTitle); requestScriptSave = false; }
        using var modal = ImGuiScopes.Modal(ScriptSaveTitle, ImGuiWindowFlags.AlwaysAutoResize);
        if (!modal.DrawContents) return;
        if (saveScript == null) { Gui.CloseCurrentPopup(); return; }
        var message = EditorLocalization.Text("You can't use scripts until you save them. Do you want to save now?", "Message");
        if (NativeMessageRenderer != null) NativeMessageRenderer("##SaveScriptMessage", message);
        else scriptSaveMessage.Draw("##SaveScriptMessage", message, true);
        Gui.Checkbox(EditorLocalization.Widget("Always save new scripts without asking", "Settings"), ref alwaysSaveScripts);
        if (Gui.Button(EditorLocalization.Widget("Save", "Button"))) execute(() => { saveScript(true, alwaysSaveScripts); saveScript = null; Gui.CloseCurrentPopup(); });
        Gui.SameLine();
        if (Gui.Button(EditorLocalization.Widget("Cancel", "Button"))) execute(() => { saveScript(false, alwaysSaveScripts); saveScript = null; Gui.CloseCurrentPopup(); });
    }
    private void SelectReferences()
    {
        reference = null;
        var kind = AssetCreationReferenceKind(selected?.Template?.Id);
        references = kind == null || session == null ? [] : session.Packages.SelectMany(package => package.Assets)
            .Where(item => kind.Value.Types.Any(type => type.IsInstanceOfType(item.Asset))).OrderBy(item => item.Location.ToString(), StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static (string Name, Type[] Types)? AssetCreationReferenceKind(Guid? id)
    {
        if (id == Guid.Parse("F16AB154-86A6-4020-B410-88FBD87D3CA5")) return ("Texture", AssetRegistry.GetAssetTypes(typeof(Stride.Graphics.Texture)).ToArray());
        if (id == Guid.Parse("8267F08C-3DC8-48F4-81EA-4888A1CEF9CE")) return ("Material", AssetRegistry.GetAssetTypes(typeof(Stride.Rendering.Material)).ToArray());
        if (id == Guid.Parse("FDBCB65A-9866-4EF2-9A03-AF21F943BDFD") || id == Guid.Parse("5A3CD5E3-4328-36AF-A41C-8146D2D21F83")
            || id == Guid.Parse("5A3CD5E3-4318-46AF-A41C-8146D2D21F80")) return ("Model", [typeof(Stride.Assets.Models.IModelAsset)]);
        return null;
    }
    private void ReleaseAdapters()
    {
        var previous = choices; choices = []; selected = null; ClearTemplateGroups(); RetainTemplateReference(null); templateRequestEpoch++; referencePickerPending = false; suggestTemplateName = null; scrollTemplateChoice = null; focusTemplateParameters = false; focusTemplateSearch = false;
        List<Exception>? failures = null;
        foreach (var choice in previous)
            try
            {
                if (choice.Adapter != null)
                {
                    RetireTemplateProviderLease(choice.Adapter);
                }
            }
            catch (Exception error) { (failures ??= []).Add(error); }
        if (failures != null) throw new AggregateException("Template provider cleanup failed.", failures);
        DrainFailedTemplateProviders();
    }
    private void RetireTemplateProviderLease(EditorAssetTemplateAdapterLease lease)
    {
        try
        {
            if (RetireReferenceLease != null) RetireReferenceLease(lease); else lease.Dispose();
            failedTemplateProviderCleanup.Remove(lease);
        }
        catch
        {
            if (!failedTemplateProviderCleanup.Contains(lease)) failedTemplateProviderCleanup.Add(lease);
            throw;
        }
    }
    private void DrainFailedTemplateProviders()
    {
        foreach (var lease in failedTemplateProviderCleanup.ToArray()) RetireTemplateProviderLease(lease);
    }
    public void Clear() { ReleaseAdapters(); choices = []; selected = null; references = []; session = null; reference = null; create = null; saveScript = null; requestScriptSave = false; requestOpen = false; ClearWithoutSourcePrompt(); }
}
