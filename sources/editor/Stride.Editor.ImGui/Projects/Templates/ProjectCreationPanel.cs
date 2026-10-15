using Hexa.NET.ImGui;
using Microsoft.TemplateEngine.Abstractions;
using Stride.Assets.Templates;
using Stride.Core.Assets.Templates;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectCreationPanel(Func<string, bool> openProject, Func<string, string?> browse,
    Action<string, string> selectableText, Func<IEnumerable<string>>? conflictingNames = null, bool existingSolution = false,
    Action<string, float, bool>? drawImage = null, Func<string?>? defaultLocation = null, Action<string>? rememberLocation = null,
    IProjectCreationPresentation? presentation = null, Func<string?>? commandError = null, IProjectSelectionSource? selectionSource = null,
    Action<Stride.Core.Diagnostics.ILogMessage>? templateLog = null, Action<Exception>? templateDeviceFailure = null,
    Func<Stride.Core.Assets.PackageSession?>? templateSourceSession = null)
{
    private Task<ProjectTemplateChoice[]>? discovery;
    private ProjectTemplateChoice[] choices = [];
    private ProjectTemplateChoice? selected;
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private readonly HashSet<string> packs = new(StringComparer.Ordinal);
    private Task<IReadOnlyList<ITemplateInfo>>? packDiscovery;
    private Task<string>? generation;
    private string? generatedProject;
    private CancellationTokenSource? cancellation;
    private bool openRequested;
    private string name = "", location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Stride Projects"), filter = "";
    private string? error;
    private bool hdrChoice;
    private bool hdrProfileDependency;
    private bool nameWasEdited;
    private long discoveryVersion = -1;
    private long nativeDiscoveryVersion = -1;
    private Stride.Core.Assets.PackageSession? discoverySession;
    private ProjectTemplateGroup? groups;
    private string groupPath = "";
    private bool hasSelectedGroup = true;
    private bool resetDetailsScroll;
    private Action? acceptedLocation;
    private Action AcceptedLocation => acceptedLocation ??= RememberAcceptedLocation;
    private void RememberAcceptedLocation() => rememberLocation?.Invoke(Path.GetFullPath(location));

    private RegisteredProjectTemplateRequest? nativeGeneration;
    private string? nativeCompletedProject;
    public bool IsGenerating => generation != null || nativeGeneration != null;
    public void Open(string? requestedLocation = null)
    {
        if (IsGenerating || parameterStage != null) return;
        var sourceSession = existingSolution ? templateSourceSession?.Invoke() : null;
        if (!ReferenceEquals(sourceSession, discoverySession))
        {
            discoverySession = sourceSession; choices = []; discovery = null;
            discoveryVersion = nativeDiscoveryVersion = -1;
        }
        var initialLocation = requestedLocation ?? defaultLocation?.Invoke();
        location = !string.IsNullOrWhiteSpace(initialLocation) ? Path.GetFullPath(initialLocation)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Stride Projects");
        selected = null; values.Clear(); packs.Clear(); packDiscovery = null; generatedProject = nativeCompletedProject = null; resetDetailsScroll = true; hdrProfileDependency = false;
        name = ""; nameWasEdited = false; filter = ""; groupPath = ""; hasSelectedGroup = true;
        solutionName = solutionLocation = "";
        projectValidationMessage = null; openProjectValidationMessage = false;
        parameterStage = null; openParameterStage = closeCreationAfterParameters = false;
        ResetProjectFieldDrafts();
        ResetTemplateTreeNavigation();
        ResetTemplateListNavigation();
        ResetRecentProjectSelection();
        openRequested = true; error = null;
        if (discovery == null && choices.Length == 0)
        {
            discoveryVersion = DotNetNewTemplateBridge.ProjectTemplateVersion;
            nativeDiscoveryVersion = TemplateManager.RegistryVersion;
            discovery = ProjectTemplateService.Discover(existingSolution, discoverySession);
        }
    }

    public void Draw()
    {
        if (discovery == null && (discoveryVersion != DotNetNewTemplateBridge.ProjectTemplateVersion || nativeDiscoveryVersion != TemplateManager.RegistryVersion))
        {
            discoveryVersion = DotNetNewTemplateBridge.ProjectTemplateVersion;
            nativeDiscoveryVersion = TemplateManager.RegistryVersion;
            discovery = ProjectTemplateService.ReadChoices(existingSolution, discoverySession);
        }
        if (discovery?.IsCompleted == true)
        {
            var task = discovery; discovery = null;
            try
            {
                choices = task.GetAwaiter().GetResult();
                groups = ProjectTemplateGroup.Create(choices, existingSolution);
                if (!recentProjectCategory && groups.Find(groupPath) == null) groupPath = "";
                if (selected != null)
                {
                    var replacement = choices.FirstOrDefault(choice => choice.Identity == selected.Identity);
                    if (replacement != null)
                    {
                        if (!ReferenceEquals(selected, replacement)) resetDetailsScroll = true;
                        selected = replacement;
                        UpdateHdrProfileDependency();
                    }
                    else { selected = null; values.Clear(); packs.Clear(); packDiscovery = null; }
                }
            }
            catch (Exception failure) { error = failure.Message; }
        }
        string? created = nativeCompletedProject; nativeCompletedProject = null;
        if (generation?.IsCompleted == true)
        {
            var task = generation; generation = null;
            try
            {
                var path = task.GetAwaiter().GetResult();
                if (cancellation?.IsCancellationRequested != true) generatedProject = created = path;
            }
            catch (OperationCanceledException) { }
            catch (Exception failure) { error = failure.Message; }
            cancellation?.Dispose(); cancellation = null;
        }
        var title = selectionSource == null ? EditorLocalization.Widget("New package")
            : string.Format(EditorLocalization.Text("Project selection - {0}", "Title"), "Stride Game Studio") + "###ProjectSelection";
        if (openRequested) { Gui.OpenPopup(title); openRequested = false; }
        var dialogMetrics = EditorProjectDialogSizing.Resolve(title);
        Gui.SetNextWindowSize(Vector2.Min(new Vector2(1024, 768) * dialogMetrics.Scale, dialogMetrics.WorkSize), ImGuiCond.Appearing);
        using var modal = ImGuiScopes.Modal(title, ImGuiWindowFlags.None);
        if (!modal.DrawContents)
        {
            if (created != null && !TryOpenGeneratedProject()) openRequested = true;
            return;
        }
        DrawProjectValidationMessage(presentation, selectableText);
        var validationBlocked = projectValidationMessage != null || projectValidationAcceptedFrame == Gui.GetFrameCount();
        if (created != null && TryOpenGeneratedProject()) { Gui.CloseCurrentPopup(); return; }
        using (ImGuiScopes.Disabled(IsGenerating || generatedProject != null || validationBlocked || parameterStage != null))
        {
            Gui.SetNextItemWidth(-1); NativeStringInput.DrawWithHint("##Templates", EditorLocalization.Text("Search"), ref filter);
            if (!existingSolution && !recentProjectCategory && TemplateDownloads.Current is { } download)
            {
                selectableText("Template download", TemplateDownloads.Describe(download));
                if (!download.Failed) Gui.ProgressBar(download.Fraction is { } fraction ? (float)fraction : -1, new Vector2(-1, 6 * Gui.GetStyle().FontScaleDpi), "");
            }
            var browserOrigin = Gui.GetCursorScreenPos();
            var browserLayout = ResolveTemplateBrowserLayout();
            using (var hierarchy = ImGuiScopes.Child("Template groups", new Vector2(Math.Max(1, browserLayout.Groups), browserLayout.Height), ImGuiChildFlags.Borders, ImGuiWindowFlags.None))
                if (hierarchy.DrawContents)
                {
                    BeginTemplateTreeNavigation();
                    if (selectionSource != null) DrawRecentProjectGroup();
                    if (groups != null) DrawGroup(groups);
                    EndTemplateTreeNavigation();
                }
            DrawTemplateGroupSplitter(browserOrigin, browserLayout);
            Gui.SetCursorScreenPos(browserOrigin + new Vector2(browserLayout.Groups + browserLayout.Splitter, 0));
            using (var list = ImGuiScopes.Child("Templates", new Vector2(Math.Max(1, browserLayout.Templates), browserLayout.Height), ImGuiChildFlags.Borders, ImGuiWindowFlags.None))
            {
                if (list.DrawContents)
                {
                    if (recentProjectCategory) DrawRecentProjects();
                    else
                    {
                        BeginTemplateListNavigation();
                        foreach (var choice in visibleTemplateChoices) DrawTemplateChoice(choice);
                        EndTemplateListNavigation();
                    }
                }
            }
            DrawTemplateDetailsSplitter(browserOrigin, browserLayout);
            Gui.SetCursorScreenPos(browserOrigin + new Vector2(browserLayout.Groups + browserLayout.Templates + browserLayout.Splitter * 2, 0));
            using (var details = ImGuiScopes.Child("Template parameters", new Vector2(Math.Max(1, browserLayout.Details), browserLayout.Height), ImGuiChildFlags.None, ImGuiWindowFlags.None))
            {
                if (details.DrawContents && recentProjectCategory && selectedRecentProject != null)
                {
                    if (presentation != null) presentation.Heading(Path.GetFileNameWithoutExtension(selectedRecentProject));
                    else Gui.TextUnformatted(Path.GetFileNameWithoutExtension(selectedRecentProject));
                }
                else if (details.DrawContents && selected != null)
                {
                    if (resetDetailsScroll) { Gui.SetScrollY(0); resetDetailsScroll = false; }
                    if (presentation != null) presentation.Heading(selected.Description.Name);
                    else Gui.TextUnformatted(selected.Description.Name);
                    var description = selected.Description.FullDescription ?? selected.Description.Description;
                    if (!string.IsNullOrWhiteSpace(description)) selectableText("Template description", description);
                    foreach (var screenshot in selected.Description.Screenshots)
                        if (ProjectTemplateArtwork.Resolve(selected.Description, screenshot) is { } imagePath) drawImage?.Invoke(imagePath, 192, false);
                }
            }
            Gui.SetCursorScreenPos(browserOrigin + new Vector2(0, browserLayout.Height));
            Gui.Dummy(Vector2.Zero);
            var submitFields = false; var validateFields = false;
            using (ImGuiScopes.Disabled(selected == null))
            {
                var labelWidth = new[] { "Name:", "Location:", "Solution name:", "Solution location:" }
                    .Take(selectionSource == null ? 2 : 4).Max(label => (presentation?.MeasureCaption(EditorLocalization.Text(label)) ?? Gui.CalcTextSize(EditorLocalization.Text(label))).X);
                if (Gui.BeginTable("##ProjectDestinationFields", 2, ImGuiTableFlags.SizingStretchProp))
                {
                    try
                    {
                        Gui.TableSetupColumn("##Labels", ImGuiTableColumnFlags.WidthFixed, labelWidth);
                        Gui.TableSetupColumn("##Values", ImGuiTableColumnFlags.WidthStretch);
                        BeginProjectFieldRow("Name:");
                        Gui.SetNextItemWidth(-1);
                        DrawProjectField("###Name", nameDraft, ref name, true, ref validateFields, ref submitFields);
                        BeginProjectFieldRow("Location:");
                        var browseWidth = Gui.GetFrameHeight();
                        Gui.SetNextItemWidth(Math.Max(1, Gui.GetContentRegionAvail().X - browseWidth - Gui.GetStyle().ItemSpacing.X));
                        DrawProjectField("###Location", locationDraft, ref location, false, ref validateFields, ref submitFields);
                        Gui.SameLine();
                        if (Gui.Button("...###Browse", new Vector2(browseWidth, 0)) && CommitProjectFieldDrafts(validate: false))
                        { var path = browse(location); if (path != null) { location = path; UpdateDefaultName(); ResetProjectFieldDrafts(); validateFields = true; } }
                        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Browse directory", "ToolTip"));
                        if (selectionSource != null)
                        {
                            BeginProjectFieldRow("Solution name:");
                            Gui.SetNextItemWidth(-1);
                            DrawProjectField("###SolutionName", solutionNameDraft, ref solutionName, false,
                                ref validateFields, ref submitFields, true, EditorLocalization.Text("(Auto-generate solution name)"));
                            BeginProjectFieldRow("Solution location:");
                            Gui.SetNextItemWidth(Math.Max(1, Gui.GetContentRegionAvail().X - browseWidth - Gui.GetStyle().ItemSpacing.X));
                            DrawProjectField("###SolutionLocation", solutionLocationDraft, ref solutionLocation, false,
                                ref validateFields, ref submitFields, true, EditorLocalization.Text("(Same location)"));
                            Gui.SameLine();
                            if (Gui.Button("...###SolutionBrowse", new Vector2(browseWidth, 0)) && CommitProjectFieldDrafts(validate: false))
                            {
                                var path = browse(solutionLocation);
                                if (path != null) { solutionLocation = path; solutionLocationDraft.Reset(path); validateFields = true; }
                            }
                            if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Browse directory", "ToolTip"));
                        }
                    }
                    finally { Gui.EndTable(); }
                }
            }
            if (validateFields && !submitFields) ValidateProjectDestination();
            if (submitFields) Begin();
            if (error != null) selectableText("Project creation error", error);
        }
        DrawProjectSelectionSourceActions(validationBlocked);
        using (ImGuiScopes.Disabled(validationBlocked || parameterStage != null || IsGenerating
            || generatedProject == null && (recentProjectCategory ? selectedRecentProject == null : selected == null || discovery != null)))
        {
            if (DrawProjectSelectionButton(EditorLocalization.Widget("Select", "Button")))
            {
                if (generatedProject != null)
                {
                    if (TryOpenGeneratedProject()) Gui.CloseCurrentPopup();
                }
                else Begin();
            }
            if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Use the selected template", "ToolTip"));
        }
        Gui.SameLine();
        var cancelKey = !validationBlocked && parameterStage == null && projectFieldCancelledFrame != Gui.GetFrameCount() && templateTreeCancelledFrame != Gui.GetFrameCount()
            && ProjectModalOwnsInput() && !Gui.GetIO().WantTextInput && !Gui.IsAnyItemActive()
            && Gui.IsKeyPressed(ImGuiKey.Escape, false);
        if (DrawProjectSelectionButton(EditorLocalization.Widget("Cancel", "Button")) || cancelKey)
        {
            if (cancelKey) ImGuiP.SetKeyOwner(ImGuiKey.Escape, Gui.GetID(EditorLocalization.Widget("Cancel", "Button")),
                (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
            if (IsGenerating) { cancellation?.Cancel(); nativeGeneration?.Cancel(); }
            else { ResetProjectFieldDrafts(); generatedProject = null; Gui.CloseCurrentPopup(); }
        }
        if (Gui.IsItemHovered()) Gui.SetTooltip(EditorLocalization.Text("Cancel (Esc)", "ToolTip"));
        if (IsGenerating) Gui.ProgressBar(-1, new Vector2(-1, 0));
        nativeGeneration?.DrawPreparation();
        DrawProjectParameterStage();
        if (closeCreationAfterParameters) { closeCreationAfterParameters = false; Gui.CloseCurrentPopup(); }
    }

    private void Select(ProjectTemplateChoice choice)
    {
        if (ReferenceEquals(selected, choice)) return;
        if (!CommitProjectFieldDrafts(validate: true)) return;
        resetDetailsScroll = true;
        selected = choice; values.Clear(); packs.Clear(); error = null;
        UpdateDefaultName();
        if (choice.Template != null)
            foreach (var parameter in ProjectTemplateParameters.Visible(choice.Template))
                values[parameter.Name] = ProjectTemplateParameters.InitialValue(parameter);
        UpdateHdrProfileDependency();
        var hdrKey = hdrProfileDependency ? "HDR" : null;
        hdrChoice = hdrKey != null && bool.TryParse(values[hdrKey], out var enabled) && enabled;
        packDiscovery = null;
    }

    private void UpdateHdrProfileDependency()
    {
        var definitions = selected?.Template?.ParameterDefinitions;
        hdrProfileDependency = definitions != null && values.ContainsKey("HDR") && values.ContainsKey("graphicsProfile")
            && definitions.Any(parameter => parameter.Name == "HDR" && string.Equals(parameter.DataType, "bool", StringComparison.OrdinalIgnoreCase))
            && definitions.Any(parameter => parameter.Name == "graphicsProfile" && ProjectTemplateParameters.IsChoice(parameter)
                && !parameter.AllowMultipleValues && parameter.Choices is { Count: > 1 });
    }

    private void DrawGroup(ProjectTemplateGroup group)
    {
        var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.DefaultOpen;
        if (hasSelectedGroup && group.Path == groupPath) flags |= ImGuiTreeNodeFlags.Selected;
        if (group.Children.Count == 0) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        var id = group.Name + "###TemplateGroup/" + group.Path;
        ApplyTemplateTreeExpansion(group);
        bool expanded;
        using (EditorTemplateTreeInput.SuppressSelectionKeyToggle(id)) expanded = Gui.TreeNodeEx(id, flags);
        RememberTemplateTreeRow(group);
        if (Gui.IsItemClicked() && !Gui.IsItemToggledOpen()) SelectTemplateGroup(group.Path, Gui.GetIO().KeyCtrl);
        else if (EditorTemplateTreeInput.SelectsCurrentRow()) SelectTemplateGroup(group.Path, Gui.GetIO().KeyCtrl);
        if (expanded && group.Children.Count != 0)
        {
            foreach (var child in group.Children) DrawGroup(child);
            Gui.TreePop();
        }
    }

    private void DrawTemplateChoice(ProjectTemplateChoice choice)
    {
        Gui.PushID(choice.WidgetIdentity);
        var scale = Gui.GetStyle().FontScaleDpi;
        var origin = Gui.GetCursorScreenPos();
        bool clicked, deselect;
        var input = Gui.GetIO();
        using (EditorTemplateTreeInput.SuppressListTextSearchSpace("##Template", templateListSearch.Prefix.Length != 0
            && !input.KeyCtrl && !input.KeyShift && !input.KeyAlt && !input.KeySuper))
            clicked = EditorIconStripInput.Draw("##Template", ReferenceEquals(selected, choice), out deselect,
                new Vector2(0, 56 * scale), ImGuiSelectableFlags.AllowOverlap);
        RememberTemplateListRow(choice);
        var submit = clicked && Gui.IsItemHovered() && ProjectModalOwnsInput()
            && Gui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        if (!Gui.IsItemVisible())
        {
            if (clicked) { if (deselect) ClearTemplateSelection(); else Select(choice); }
            if (submit) Begin();
            Gui.PopID();
            return;
        }
        var end = Gui.GetItemRectMax();
        var next = Gui.GetCursorScreenPos();
        if (presentation != null)
        {
            presentation.TemplateRow(choice, origin, end);
            if (clicked) { if (deselect) ClearTemplateSelection(); else Select(choice); }
            if (submit) Begin();
            Gui.PopID();
            return;
        }
        Gui.SetCursorScreenPos(origin + new Vector2(2, 4) * scale);
        drawImage?.Invoke(ProjectTemplateArtwork.Resolve(choice.Description, choice.Description.Icon)
            ?? Path.Combine(AppContext.BaseDirectory, "Icons", "template-default.png"), 48, true);
        var textOrigin = origin + new Vector2(68, 2) * scale;
        var draw = Gui.GetWindowDrawList();
        draw.PushClipRect(textOrigin, end, true);
        draw.AddText(textOrigin, Gui.GetColorU32(ImGuiCol.Text), choice.Description.Name);
        if (!string.IsNullOrWhiteSpace(choice.Description.Description))
        {
            Gui.SetCursorScreenPos(textOrigin + new Vector2(0, Gui.GetTextLineHeightWithSpacing()));
            Gui.PushTextWrapPos(Gui.GetCursorPosX() + Math.Max(1, end.X - textOrigin.X));
            Gui.TextWrapped(choice.Description.Description);
            Gui.PopTextWrapPos();
        }
        draw.PopClipRect();
        Gui.SetCursorScreenPos(next);
        if (clicked) { if (deselect) ClearTemplateSelection(); else Select(choice); }
        if (submit) Begin();
        Gui.PopID();
    }

    private void UpdateDefaultName()
    {
        if (nameWasEdited || selected == null) return;
        try
        {
            var existing = conflictingNames?.Invoke().ToList() ?? [];
            if (Directory.Exists(location)) existing.AddRange(Directory.EnumerateDirectories(location).Select(Path.GetFileName).OfType<string>());
            name = Stride.Core.NamingHelper.ComputeNewName(selected.Description.DefaultOutputName ?? "MyGame", existing, value => value, "{0}{1}");
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { error = failure.Message; }
    }

    private void DrawParameters(ProjectParameterStage stage)
    {
        var values = stage.Values;
        var parameterIndex = 0;
        foreach (var parameter in stage.Choice.Template!.ParameterDefinitions.Where(parameter => values.ContainsKey(parameter.Name)))
        {
            Gui.Dummy(new Vector2(0, 6 * Gui.GetStyle().FontScaleDpi));
            Gui.PushID(parameterIndex++);
            try
            {
                var displayName = DotNetNewTemplatePresentation.Humanize(parameter.Name);
                var label = string.IsNullOrWhiteSpace(parameter.Description) ? displayName : displayName + " — " + parameter.Description;
                Gui.TextWrapped(label);
                Gui.Dummy(new Vector2(0, 3 * Gui.GetStyle().FontScaleDpi));
                var value = values[parameter.Name];
                var hdrDisabled = stage.HdrProfileDependency && parameter.Name == "HDR" && values.GetValueOrDefault("graphicsProfile") == "9.0";
                using (ImGuiScopes.Disabled(hdrDisabled))
                {
                    if (string.Equals(parameter.DataType, "bool", StringComparison.OrdinalIgnoreCase))
                    {
                        var enabled = !hdrDisabled && value.Equals("true", StringComparison.OrdinalIgnoreCase);
                        if (DrawParameterCheckbox("Enabled", ref enabled)) { value = enabled ? "true" : "false"; if (parameter.Name == "HDR") stage.HdrChoice = enabled; }
                    }
                    else if (ProjectTemplateParameters.IsChoice(parameter) && parameter.Choices is { Count: > 0 } options)
                    {
                        var multiple = parameter.AllowMultipleValues;
                        if (multiple)
                        {
                            var selectedValues = value.Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
                            var optionIndex = 0;
                            foreach (var option in options.Keys)
                            {
                                if (parameter.Name == "platforms" && option == "host") continue;
                                Gui.Dummy(new Vector2(0, 2 * Gui.GetStyle().FontScaleDpi));
                                var enabled = selectedValues.Contains(option);
                                var caption = options[option].Description;
                                var platform = parameter.Name == "platforms" ? option switch
                                {
                                    "windows" => Stride.Core.PlatformType.Windows,
                                    "linux" => Stride.Core.PlatformType.Linux,
                                    "macos" => Stride.Core.PlatformType.macOS,
                                    "ios" => Stride.Core.PlatformType.iOS,
                                    "android" => Stride.Core.PlatformType.Android,
                                    _ => (Stride.Core.PlatformType?)null,
                                } : null;
                                Gui.PushID(optionIndex++);
                                try
                                {
                                    var text = string.IsNullOrEmpty(caption) ? DotNetNewTemplatePresentation.Humanize(option) : caption;
                                    if (DrawLiteralParameterCheckbox("##Choice", text, ref enabled, platform)) { if (enabled) selectedValues.Add(option); else selectedValues.Remove(option); }
                                }
                                finally { Gui.PopID(); }
                                Gui.Dummy(new Vector2(0, 2 * Gui.GetStyle().FontScaleDpi));
                            }
                            value = string.Join("|", options.Keys.Where(selectedValues.Contains));
                        }
                        else
                        {
                            Gui.SetNextItemWidth(-1);
                            bool choiceOpen;
                            using (EditorTemplateChoiceInput.BeforeClosed("##Value", stage.Choices[parameter.Name], ref value))
                                choiceOpen = presentation?.BeginChoice("##Value", value) ?? EditorLiteralTemplateControls.BeginCombo("##Value", value);
                            var choiceInput = stage.Choices[parameter.Name];
                            if (!choiceOpen) EditorTemplateChoiceInput.AfterClosed(choiceInput, ref value);
                            if (choiceOpen)
                            {
                                try
                                {
                                    var closeChoice = EditorTemplateChoiceInput.BeforePopup(choiceInput, value);
                                    using var searchInput = EditorTemplateChoiceInput.BeforePopupRows(choiceInput, closeChoice);
                                    var optionIndex = 0;
                                    foreach (var option in options.Keys)
                                    {
                                        Gui.PushID(optionIndex++);
                                        try
                                        {
                                            var selectedOption = value == option;
                                            if (presentation?.ChoiceOption("##Option", option, selectedOption) ?? EditorLiteralTemplateControls.Selectable("##Option", option, selectedOption)) value = option;
                                            EditorTemplateChoiceInput.ObserveOption(choiceInput, optionIndex - 1);
                                            if (selectedOption && choiceInput.SearchFocus < 0) Gui.SetItemDefaultFocus();
                                        }
                                        finally { Gui.PopID(); }
                                    }
                                    EditorTemplateChoiceInput.AfterPopup(choiceInput, closeChoice, ref value);
                                }
                                finally { Gui.EndCombo(); }
                            }
                        }
                    }
                    else { Gui.SetNextItemWidth(-1); NativeStringInput.Draw("##Value", ref value); }
                }
                values[parameter.Name] = hdrDisabled ? "false" : value;
            }
            finally { Gui.PopID(); }
            Gui.Dummy(new Vector2(0, 6 * Gui.GetStyle().FontScaleDpi));
        }
        var hdrKey = stage.HdrProfileDependency ? "HDR" : null;
        if (hdrKey != null) values[hdrKey] = values.GetValueOrDefault("graphicsProfile") == "9.0" ? "false" : stage.HdrChoice ? "true" : "false";
    }

    private void DrawPacks(ProjectParameterStage stage)
    {
        var packDiscovery = stage.PackDiscovery;
        var packs = stage.Packs;
        if (packDiscovery == null) return;
        var scale = Gui.GetStyle().FontScaleDpi;
        Gui.Dummy(new Vector2(0, 6 * scale));
        Gui.TextWrapped("Asset packs — Ready-made asset collections added to your project");
        Gui.Dummy(new Vector2(0, 3 * scale));
        if (!packDiscovery.IsCompleted)
        {
            var download = TemplateDownloads.Current;
            Gui.Dummy(new Vector2(0, 2 * scale));
            selectableText("Asset pack download", download == null ? "Preparing the asset packs..." : TemplateDownloads.Describe(download));
            Gui.Dummy(new Vector2(0, 3 * scale));
            Gui.ProgressBar(download?.Fraction is { } fraction ? (float)fraction : -1, new Vector2(-1, 6 * scale), "");
            Gui.Dummy(new Vector2(0, 6 * scale));
            return;
        }
        if (packDiscovery.IsCanceled)
        {
            Gui.Dummy(new Vector2(0, 2 * scale));
            selectableText("Asset packs unavailable", "No asset packs are available. The AssetPacks package could not be downloaded.");
            Gui.Dummy(new Vector2(0, 8 * scale)); return;
        }
        if (packDiscovery.IsFaulted) { error = packDiscovery.Exception?.GetBaseException().Message; return; }
        if (packDiscovery.Result.Count == 0)
        {
            Gui.Dummy(new Vector2(0, 2 * scale));
            selectableText("Asset packs unavailable", "No asset packs are available. The AssetPacks package could not be downloaded.");
            Gui.Dummy(new Vector2(0, 2 * scale));
        }
        var packIndex = 0;
        foreach (var pack in packDiscovery.Result)
        {
            Gui.Dummy(new Vector2(0, 2 * scale));
            Gui.PushID(packIndex++); var enabled = packs.Contains(pack.Identity);
            try
            {
                var label = string.IsNullOrEmpty(pack.Description) ? pack.Name : $"{pack.Name} — {pack.Description}";
                if (DrawLiteralParameterCheckbox("##Pack", label, ref enabled)) { if (enabled) packs.Add(pack.Identity); else packs.Remove(pack.Identity); }
            }
            finally { Gui.PopID(); }
            Gui.Dummy(new Vector2(0, 2 * scale));
        }
        Gui.Dummy(new Vector2(0, 6 * scale));
    }

    private bool DrawParameterCheckbox(string label, ref bool value, Stride.Core.PlatformType? platform = null)
        => presentation != null ? presentation.Checkbox(label, ref value, platform) : Gui.Checkbox(label, ref value);
    private bool DrawLiteralParameterCheckbox(string identity, string text, ref bool value, Stride.Core.PlatformType? platform = null)
        => presentation != null ? presentation.LiteralCheckbox(identity, text, ref value, platform) : EditorLiteralTemplateControls.Checkbox(identity, text, ref value);

    private static bool ProjectModalOwnsInput() => !Gui.GetIO().AppFocusLost
        && Gui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
        && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)
        && !Gui.GetIO().KeyCtrl && !Gui.GetIO().KeyAlt && !Gui.GetIO().KeySuper && !Gui.GetIO().KeyShift;

    private static bool SubmitProjectField()
    {
        if (!Gui.IsItemFocused() || !ProjectModalOwnsInput()
            || !(Gui.IsKeyPressed(ImGuiKey.Enter, false) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter, false))) return false;
        var owner = Gui.GetItemID();
        ImGuiP.SetKeyOwner(ImGuiKey.Enter, owner, (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
        ImGuiP.SetKeyOwner(ImGuiKey.KeypadEnter, owner, (ImGuiInputFlags)ImGuiInputFlagsPrivate.LockThisFrame);
        return true;
    }

    private bool TryOpenGeneratedProject()
    {
        if (generatedProject == null) return false;
        if (!openProject(generatedProject))
        {
            error = commandError?.Invoke();
            return false;
        }
        generatedProject = null;
        error = null;
        return true;
    }

    private void Begin()
    {
        if (recentProjectCategory)
        {
            if (selectedRecentProject != null && selectionSource?.OpenRecentProject(selectedRecentProject, AcceptedLocation) == true) closeCreationAfterParameters = true;
            else error = commandError?.Invoke();
            return;
        }
        if (selected == null || IsGenerating || generatedProject != null || parameterStage != null) return;
        if (!CommitProjectFieldDrafts(validate: false)) return;
        if (!ValidateProjectDestination()) return;
        try { AcceptedLocation(); }
        catch (Exception failure) { error = failure.Message; return; }
        if (selected.Template == null)
        {
            nativeGeneration = new(selected, name, location, selectionSource == null ? null : new ProjectSolutionSettings(solutionName, solutionLocation),
                templateLog, templateDeviceFailure, discoverySession, templateSourceSession);
            return;
        }
        packDiscovery ??= selected.OffersAssetPacks ? DotNetNewTemplateBridge.GetAssetPackTemplatesAsync() : null;
        parameterStage = new(selected, name, location, values, packs, packDiscovery, hdrProfileDependency, hdrChoice,
            selectionSource == null ? null : new ProjectSolutionSettings(solutionName, solutionLocation));
        openParameterStage = true;
    }

    // Called by the owner actor while rendering is suspended. Cancelled creation
    // never adopts or opens a generated project, even if generation won the race.
    internal void PumpCancelledGeneration()
    {
        if (nativeGeneration is { } native)
        {
            native.Pump();
            if (native.Complete)
            {
                nativeGeneration = null;
                if (native.Failure is not OperationCanceledException && native.Failure != null) error = native.Failure.Message;
                if (native.Result != null && !native.IsCancelled) generatedProject = nativeCompletedProject = native.Result;
            }
        }
        if (cancellation?.IsCancellationRequested != true || generation?.IsCompleted != true) return;
        var task = generation;
        try { _ = task.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception failure) { error = failure.Message; }
        finally
        {
            generation = null;
            cancellation.Dispose(); cancellation = null;
        }
    }

    public void Cancel()
    {
        nativeCompletedProject = null;
        if (!IsGenerating) generatedProject = null;
        if (parameterStage != null) closeCreationAfterParameters = true;
        parameterStage = null; openParameterStage = false; cancellation?.Cancel();
        nativeGeneration?.Cancel();
    }

    internal void CancelAndRetireGeneration()
    {
        if (parameterStage != null) closeCreationAfterParameters = true;
        parameterStage = null; openParameterStage = false;
        if (nativeGeneration is { } native)
        {
            // Normal shutdown drains through the actor before this terminal seam.
            // Unexpected teardown retains the owner; it cannot move UI cleanup
            // to a background continuation or pretend cancellation completed it.
            native.Cancel();
            if (!native.Complete) throw new InvalidOperationException("Native template preparation is still draining on the editor actor.");
            nativeGeneration = null;
        }
        if (generation is not { } task) return;
        // The service owns its unpublished staging directory until it exits.
        // A project already published when cancellation arrives stays on disk;
        // retirement observes completion without invoking the former UI callback.
        EditorBackgroundJobRetirement.Retire(task, cancellation, static _ => { }, "Project template generation");
        generation = null;
        cancellation = null;
    }
}
