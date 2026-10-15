using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Text.Json;
using Stride.Assets.Presentation.Templates;
using Stride.Assets.Templates;
using Stride.Core.Assets.Editor.Components.TemplateDescriptions.ViewModels;
using Stride.Core.Assets.Editor.Components.TemplateDescriptions.Views;
using Stride.Core.Assets.Editor.Services;
using Stride.Core.Assets.Editor.ViewModel;
using Stride.Core.Presentation.Controls;
using Stride.Core.Presentation.Services;
using Stride.Core.Presentation.Windows;
using Stride.Core.Translation;
using Stride.GameStudio.ViewModels;
using ModalWindow = Stride.Core.Presentation.Controls.ModalWindow;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfControl = System.Windows.Controls.Control;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Stride.GameStudio.Diagnostics.VisualCapture;

internal sealed partial class VisualCaptureEndpoint
{
    private sealed class CaptureModalOpenState { internal Exception Error; }

    private async Task<Window> CreateCaptureModal(CaptureRequest request)
    {
        var studio = GameStudioViewModel.GameStudio ?? throw new CaptureEndpointException("action_unavailable", "The actual studio view model is unavailable.");
        var session = studio.Session;
        var name = StringParameter(request, "name", true);
        switch (name)
        {
            case "new-project":
            case "project-selection":
                var selection = new ProjectSelectionWindow { Owner = Application.Current.MainWindow };
                var selectionModel = new NewOrOpenSessionTemplateCollectionViewModel(studio.ServiceProvider, selection);
                selection.Templates = selectionModel;
                selection.Closed += (_, _) => selectionModel.Destroy();
                return selection;
            case "new-package":
                if (session == null) throw new CaptureEndpointException("action_unavailable", "New package choices require an actual loaded session.");
                return (Window)session.ServiceProvider.Get<IEditorDialogService>().CreateNewProjectDialog(session);
            case "add-asset":
                if (session == null) throw new CaptureEndpointException("action_unavailable", "Asset template choices require an actual loaded session.");
                var directories = session.ActiveAssetView?.GetSelectedDirectories(false).ToArray();
                if (directories == null || directories.Length != 1)
                    throw new CaptureEndpointException("action_unavailable", "Select one actual asset directory before opening asset template choices.");
                return (Window)session.ServiceProvider.Get<IEditorDialogService>().CreateAddAssetDialog(session, directories[0]);
            case "template-parameters":
                var identity = StringParameter(request, "templateId", true);
                var registry = DotNetNewTemplateBridge.Registry ?? throw new CaptureEndpointException("action_unavailable", "The actual installed-template registry is unavailable.");
                using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    cancellation.CancelAfter(Math.Clamp(request.TimeoutMs, 100, 120000));
                    try
                    {
                        var available = await registry.GetTemplatesAsync(cancellation.Token).WaitAsync(cancellation.Token);
                        var matches = available.Where(template => template.Identity == identity).ToArray();
                        if (matches.Length != 1) throw new CaptureEndpointException("template_not_found", "Specify an actual installed template identity.");
                        return new DotNetNewTemplateParametersWindow(matches[0]);
                    }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                    {
                        throw new CaptureEndpointException("action_timeout", "Installed template discovery did not finish before the timeout.");
                    }
                }
            default: throw new CaptureEndpointException("action_not_allowed", "This modal cannot be opened through safe capture navigation.");
        }
    }

    private CaptureModalOpenState QueueCaptureModal(Window window)
    {
        window.Owner ??= Application.Current.MainWindow;
        var state = new CaptureModalOpenState();
        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(async () =>
        {
            try
            {
                // The original view collects choices; no generation/open-session
                // continuation is attached to its result in capture navigation.
                if (window is ModalWindow modal) await modal.ShowModal();
                else window.ShowDialog();
            }
            catch (Exception error) { state.Error = error; }
        }));
        return state;
    }

    private static TemplateDescriptionCollectionViewModel CaptureTemplateModel(Window window)
    {
        if (window is not (ProjectSelectionWindow or NewProjectWindow or AddItemWindow) || !window.IsVisible || !window.IsEnabled
            || window.DataContext is not TemplateDescriptionCollectionViewModel model)
            throw new CaptureEndpointException("modal_blocked", "An actual supported template chooser must be visible and unblocked.");
        return model;
    }

    private static object SelectCaptureTemplate(Window window, CaptureRequest request)
    {
        var model = CaptureTemplateModel(window);
        var name = StringParameter(request, "name"); var id = StringParameter(request, "id");
        if (name == null && id == null) throw new CaptureEndpointException("invalid_parameters", "Template selection requires a name or id.");
        var matches = model.RootGroups.SelectMany(group => group.GetTemplatesRecursively()).Distinct().Where(template =>
            (name == null || template.Name == name) && (id == null || string.Equals(template.Id.ToString(), id, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (matches.Length != 1) throw new CaptureEndpointException(matches.Length == 0 ? "template_not_found" : "ambiguous_template", "Specify one actual template name or id.");
        var selected = matches[0];
        var root = model.RootGroups.First(group => group.GetTemplatesRecursively().Contains(selected));
        model.SelectedGroup = root; model.SelectedTemplate = selected;
        foreach (var selector in Descendants(window).OfType<FilteringComboBox>())
            if (selector.Items.Contains(selected)) selector.SelectedItem = selected;
        // Do not raise Validated: AddItemWindow would accept the selected template.
        return new { templateId = selected.Id, selected.Name, group = model.SelectedGroup.Name, accepted = false };
    }

    private static object SelectCaptureTemplateGroup(Window window, string name)
    {
        var model = CaptureTemplateModel(window);
        var groups = new List<TemplateDescriptionGroupViewModel>();
        var pending = new Stack<TemplateDescriptionGroupViewModel>(model.RootGroups.Reverse());
        while (pending.TryPop(out var current))
        {
            groups.Add(current);
            foreach (var child in current.SubGroups.Reverse()) pending.Push(child);
        }
        var matches = groups.Where(group => group.Name == name || group.Path.Trim('/') == name.Trim('/')).ToArray();
        if (matches.Length != 1) throw new CaptureEndpointException(matches.Length == 0 ? "group_not_found" : "ambiguous_group", "Specify one actual template group name or path.");
        model.SelectedGroup = matches[0];
        return new { model.SelectedGroup.Name, model.SelectedGroup.Path, templates = model.Templates.Select(template => new { template.Id, template.Name }).ToArray() };
    }

    private static bool TryCancelCaptureModal(Window window)
    {
        if (window is AddItemWindow popup)
        {
            // Exact PopupModalWindow.CloseWithCancel behavior for Escape/deactivation.
            popup.Result = DialogResult.Cancel; popup.Close(); return true;
        }
        var supported = window is ProjectSelectionWindow or NewProjectWindow or DotNetNewTemplateParametersWindow
            or ScriptNameWindow or ModelAssetTemplateWindow or MessageDialogBase;
        if (!supported) return false;
        var buttons = Descendants(window).OfType<WpfButton>().Where(button => button.IsCancel && button.IsEnabled).ToArray();
        if (window is MessageDialogBase)
            buttons = buttons.Where(button => button.DataContext is DialogButtonInfo info && info.IsCancel
                && (Equals(info.Content, Tr._p("Button", "Cancel")) || Equals(info.Content, Tr._("Cancel")))).ToArray();
        if (buttons.Length != 1) throw new CaptureEndpointException("cancel_unavailable", "Exactly one original safe Cancel button must be available.");
        var cancel = buttons[0];
        if (cancel.Command != null)
        {
            if (!cancel.Command.CanExecute(cancel.CommandParameter)) throw new CaptureEndpointException("cancel_unavailable", "The original Cancel command is disabled.");
            cancel.Command.Execute(cancel.CommandParameter);
        }
        else cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, cancel));
        return true;
    }

    private static object SetCaptureTemplateOption(Window window, object source, CaptureRequest request)
    {
        if (window is not DotNetNewTemplateParametersWindow || !window.IsVisible || !window.IsEnabled
            || source is not WpfControl { IsEnabled: true } control)
            throw new CaptureEndpointException("action_not_allowed", "Select an actual enabled parameter input inside the template-parameters dialog.");
        if (request.Parameters.ValueKind != JsonValueKind.Object || !request.Parameters.TryGetProperty("value", out var value))
            throw new CaptureEndpointException("invalid_parameters", "Template option navigation requires a value.");
        switch (control)
        {
            case WpfCheckBox check when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                check.SetCurrentValue(ToggleButton.IsCheckedProperty, value.GetBoolean());
                return new { value = check.IsChecked, inputType = "bool", accepted = false };
            case WpfComboBox combo when value.ValueKind == JsonValueKind.String:
                var choice = value.GetString();
                if (!combo.Items.Contains(choice)) throw new CaptureEndpointException("invalid_parameters", "The value is not an actual template choice.");
                combo.SetCurrentValue(Selector.SelectedItemProperty, choice);
                return new { value = combo.SelectedItem, inputType = "choice", accepted = false };
            case WpfTextBox text when value.ValueKind == JsonValueKind.String:
                text.SetCurrentValue(WpfTextBox.TextProperty, value.GetString());
                return new { value = text.Text, inputType = "string", accepted = false };
            default: throw new CaptureEndpointException("action_not_allowed", "Only the template dialog's actual checkbox, choice and text inputs are permitted.");
        }
    }

    private static IReadOnlyList<CaptureTarget> ApplyModalAliases(IReadOnlyList<CaptureTarget> targets)
    {
        foreach (var target in targets)
        {
            var alias = target.Source switch
            {
                ProjectSelectionWindow => "dialogs.project-selection",
                NewProjectWindow => "dialogs.new-package",
                AddItemWindow => "dialogs.add-asset",
                DotNetNewTemplateParametersWindow => "dialogs.template-parameters",
                ScriptNameWindow => "dialogs.script-name",
                ModelAssetTemplateWindow => "dialogs.model-import",
                _ => null
            };
            if (alias != null && !target.SemanticIds.Contains(alias)) target.SemanticIds = [.. target.SemanticIds, alias];
        }
        return targets;
    }
}
