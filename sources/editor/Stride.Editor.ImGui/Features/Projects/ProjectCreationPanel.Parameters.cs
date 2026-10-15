using Microsoft.TemplateEngine.Abstractions;
using Hexa.NET.ImGui;
using Gui = Hexa.NET.ImGui.ImGui;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace Stride.Editor.ImGui;

internal sealed partial class ProjectCreationPanel
{
    private sealed class ProjectParameterStage(ProjectTemplateChoice choice, string name, string location,
        IReadOnlyDictionary<string, string> values, IEnumerable<string> packs, Task<IReadOnlyList<ITemplateInfo>>? packDiscovery,
        bool hdrProfileDependency, bool hdrChoice, ProjectSolutionSettings? solution)
    {
        internal readonly ProjectTemplateChoice Choice = choice;
        internal readonly string Name = name, Location = location;
        internal readonly Dictionary<string, string> Values = new(values, StringComparer.Ordinal);
        internal readonly HashSet<string> Packs = new(packs, StringComparer.Ordinal);
        internal readonly Task<IReadOnlyList<ITemplateInfo>>? PackDiscovery = packDiscovery;
        internal readonly bool HdrProfileDependency = hdrProfileDependency;
        internal bool HdrChoice = hdrChoice;
        internal readonly ProjectSolutionSettings? Solution = solution;
        internal readonly Dictionary<string, EditorTemplateChoiceInput.State> Choices = choice.Template!.ParameterDefinitions
            .Where(parameter => values.ContainsKey(parameter.Name) && ProjectTemplateParameters.IsChoice(parameter)
                && !parameter.AllowMultipleValues && parameter.Choices is { Count: > 0 })
            .ToDictionary(parameter => parameter.Name, parameter => new EditorTemplateChoiceInput.State(parameter.Choices!.Keys.ToArray()), StringComparer.Ordinal);
        internal float AutoHeight;
        internal bool ManualSize;
    }
    private ProjectParameterStage? parameterStage;
    private bool openParameterStage, closeCreationAfterParameters;
    private int parameterStageOpenedFrame = -1;

    private void DrawProjectParameterStage()
    {
        if (parameterStage is not { } stage) return;
        var templateName = stage.Choice.Template!.Name ?? stage.Choice.Template.Identity;
        var title = $"New project — {templateName}###ProjectTemplateParameters";
        if (openParameterStage)
        { Gui.OpenPopup(title); openParameterStage = false; parameterStageOpenedFrame = Gui.GetFrameCount(); }
        var dialogMetrics = EditorProjectDialogSizing.Resolve(title);
        var scale = dialogMetrics.Scale;
        var width = Math.Min(520 * scale, dialogMetrics.WorkSize.X);
        if (!stage.ManualSize)
            Gui.SetNextWindowSize(new Vector2(width, stage.AutoHeight > 0 ? stage.AutoHeight * scale : Math.Min(400 * scale, dialogMetrics.WorkSize.Y)), ImGuiCond.Always);
        Gui.SetNextWindowSizeConstraints(Vector2.Zero, dialogMetrics.WorkSize);
        Gui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20) * scale);
        Gui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Gui.GetStyle().ItemSpacing.X, 0));
        try
        {
            using var modal = ImGuiScopes.Modal(title, ImGuiWindowFlags.None);
            if (!modal.DrawContents) return;
            scale = Gui.GetStyle().FontScaleDpi;
            if (EditorProjectDialogSizing.IsUserResizing()) stage.ManualSize = true;
            // Dropdown dismissal owns its key even when it closes during row drawing.
            var childPopupOwnedInput = Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);
            var heading = Gui.GetCursorScreenPos();
            var headerSize = new Vector2(Math.Max(1, Gui.GetContentRegionAvail().X), 36 * scale);
            // Exact CommonResources EmphasisColorBrush used by the native window.
            Gui.GetWindowDrawList().AddRectFilled(heading, heading + headerSize,
                Gui.GetColorU32(new Vector4(49 / 255f, 107 / 255f, 143 / 255f, 1)));
            Gui.SetCursorScreenPos(heading + new Vector2(20, 10) * scale);
            if (presentation != null) presentation.ParameterHeading(templateName);
            else
            {
                Gui.PushFont(Gui.GetFont(), 16);
                try { Gui.TextUnformatted(templateName); }
                finally { Gui.PopFont(); }
            }
            Gui.SetCursorScreenPos(heading + new Vector2(0, headerSize.Y));
            Gui.Dummy(Vector2.Zero);
            if (!string.IsNullOrEmpty(stage.Choice.Template.Description))
            {
                Gui.Dummy(new Vector2(0, 5 * scale));
                selectableText("##TemplateParameterDescription", stage.Choice.Template.Description);
                Gui.Dummy(new Vector2(0, 10 * scale));
            }
            var openingFrame = parameterStageOpenedFrame == Gui.GetFrameCount();
            using var openingInput = ImGuiScopes.Disabled(openingFrame);
            DrawParameters(stage);
            DrawPacks(stage);
            // ParametersPanel bottom10 plus the native button row top10 margin.
            Gui.Dummy(new Vector2(0, 20 * scale));
            var buttonSize = new Vector2(80 * scale, 0);
            Gui.SetCursorPosX(Gui.GetCursorPosX() + Math.Max(0, Gui.GetContentRegionAvail().X - 168 * scale));
            var okLabel = EditorLocalization.Widget("OK");
            var cancelLabel = EditorLocalization.Widget("Cancel");
            var accept = presentation?.ParameterButton(okLabel, buttonSize) ?? Gui.Button(okLabel, buttonSize);
            Gui.SameLine(0, 8 * scale);
            var cancel = presentation?.ParameterButton(cancelLabel, buttonSize) ?? Gui.Button(cancelLabel, buttonSize);
            stage.AutoHeight = (Gui.GetCursorPosY() + Gui.GetStyle().WindowPadding.Y) / scale;
            var choiceHandledInput = false;
            foreach (var choice in stage.Choices.Values) choiceHandledInput |= choice.HandledFrame == Gui.GetFrameCount();
            if (!openingFrame && !childPopupOwnedInput && !choiceHandledInput && ProjectModalOwnsInput())
            {
                accept |= Gui.IsKeyPressed(ImGuiKey.Enter, false) || Gui.IsKeyPressed(ImGuiKey.KeypadEnter, false);
                cancel |= Gui.IsKeyPressed(ImGuiKey.Escape, false);
            }
            if (cancel)
            {
                parameterStage = null;
                closeCreationAfterParameters = true;
                Gui.CloseCurrentPopup();
            }
            else if (accept)
            {
                parameterStage = null;
                StartAcceptedProjectGeneration(stage);
                Gui.CloseCurrentPopup();
            }
        }
        finally { Gui.PopStyleVar(2); }
    }

    private void StartAcceptedProjectGeneration(ProjectParameterStage stage)
    {
        try
        {
            var parameters = ProjectTemplateParameters.Capture(stage.Choice.Template!, stage.Values);
            var assetPacks = stage.Packs.ToArray();
            // Retain accepted values for a genuine generator failure/retry;
            // cancelled stage drafts never replace the outer selection state.
            values.Clear(); foreach (var pair in stage.Values) values.Add(pair.Key, pair.Value);
            packs.Clear(); packs.UnionWith(stage.Packs); hdrChoice = stage.HdrChoice;
            cancellation = new(); var token = cancellation.Token;
            generation = Task.Run(() => ProjectTemplateService.Generate(stage.Choice, stage.Name, stage.Location,
                parameters, assetPacks, token, stage.Solution), token);
        }
        catch (Exception failure)
        {
            error = failure.Message;
            cancellation?.Dispose(); cancellation = null;
        }
    }
}
