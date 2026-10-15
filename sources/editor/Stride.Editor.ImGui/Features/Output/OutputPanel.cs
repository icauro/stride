using System.Text;
using Hexa.NET.ImGui;
using Stride.Core.Diagnostics;
using Gui = Hexa.NET.ImGui.ImGui;

namespace Stride.Editor.ImGui;

internal sealed partial class OutputPanel(EditorOutputLog outputLog, Action<BuildDiagnostic> navigateDiagnostic, Func<EditorTheme>? theme = null,
    Func<EditorIcons?>? currentIcons = null, Func<bool>? allowAutomaticFocus = null)
{
    internal bool Visible = true;
    internal bool FocusRequested;
    private BuildDiagnostic? requestedDiagnostic;
    private static float UiScale(float value) => value * Gui.GetStyle().FontScaleDpi;
    internal void ObserveMessages()
    {
        PumpOutputSearchValidation();
        // Log activation also applies when the docked surface is hidden.
        if (outputLog.Read(ref outputVersion, out var entries))
        {
            ObserveOutputMessages(entries);
            outputEntries = entries;
        }
    }
    internal bool Draw()
    {
        ObserveMessages();
        if (FocusRequested) Visible = true;
        if (!Visible) return false;
        // Background compiler/log arrivals may reveal Output, but must not
        // steal an active button press, drag or text gesture before it completes.
        if (FocusRequested && !Gui.IsAnyItemActive()
            && (allowAutomaticFocus?.Invoke() ?? true)
            && !Gui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
        { Gui.SetNextWindowFocus(); FocusRequested = false; }
        var docked = false;
        requestedDiagnostic = null;
        using (var window = ImGuiScopes.Window(EditorPanels.Output.DisplayWindowName, ref Visible))
        {
            if (window.DrawContents)
            {
                docked = Gui.IsWindowDocked();
                DrawOutput();
            }
        }
        var diagnostic = requestedDiagnostic;
        requestedDiagnostic = null;
        if (diagnostic != null) navigateDiagnostic(diagnostic);
        return docked;
    }
    private long outputVersion = -1;
    private EditorOutputEntry[] outputEntries = [];
    private string outputChannel = "Build";
    private float outputFontSize;
    private EditorTheme? outputTheme;
    private void DrawOutput()
    {
        using var font = ImGuiScopes.Font(11);
        using var padding = ImGuiScopes.Style(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(UiScale(3), UiScale(2)));
        using var spacing = ImGuiScopes.Style(ImGuiStyleVar.ItemSpacing, new System.Numerics.Vector2(UiScale(4), UiScale(2)));
        var fontSize = Gui.GetFontSize();
        if (outputFontSize != fontSize) { outputFontSize = fontSize; InvalidateOutputChannels(); }
        var currentTheme = theme?.Invoke() ?? EditorTheme.ExpressionDark;
        if (outputTheme != currentTheme)
        {
            foreach (var state in outputChannels.Values)
                for (var index = 0; index < state.Colors.Count; index++)
                    state.Colors[index] = state.Colors[index] with { Color = OutputMessageColor(state.ColorLevels[index], currentTheme) };
            outputTheme = currentTheme;
        }
        using var tabs = ImGuiScopes.TabBar("Output channels");
        if (tabs.DrawContents)
        {
            DrawOutputChannel("Build"); DrawOutputChannel("Project");
        }
    }
    private void DrawOutputChannel(string channel)
    {
        var state = outputChannels[channel];
        var label = EditorLocalization.Text(channel) + (state.HasNewMessages ? "*" : "") + "###output-channel/" + channel;
        var flags = requestedOutputChannel == channel ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        using var tab = ImGuiScopes.TabItem(label, flags);
        WorkflowControlRecorder?.Invoke("output/channel/" + channel);
        if (!tab.DrawContents) return;
        if (outputChannel != channel)
        {
            AcceptOutputSearch(CurrentOutput);
            state.HasNewMessages = false;
        }
        outputChannel = channel;
        if (requestedOutputChannel == channel) requestedOutputChannel = null;
        if (Gui.Button(EditorLocalization.Widget("Clear")))
        {
            outputLog.Clear(channel);
            state.HasNewMessages = state.ErrorActivationSeen = false;
            outputEntries = outputEntries.Where(entry => entry.Channel != channel).ToArray();
            outputText = ""; outputTextDirty = true;
        }
        DrawOutputLevel("Debug", ref outputDebug);
        DrawOutputLevel("Verbose", ref outputVerbose);
        DrawOutputLevel("Info", ref outputInfo);
        DrawOutputLevel("Warning", ref outputWarning);
        DrawOutputLevel("Error", ref outputError);
        DrawOutputLevel("Fatal", ref outputFatal);
        DrawOutputLevel("Show Stacktrace", ref outputStacktrace);
        var searchWidth = Math.Min(UiScale(180), Math.Max(UiScale(80), Gui.GetContentRegionAvail().X - UiScale(104)));
        var rowRight = Gui.GetCursorScreenPos().X + Gui.GetContentRegionAvail().X;
        if (Gui.GetItemRectMax().X + searchWidth + UiScale(104) < rowRight) Gui.SameLine();
        Gui.SetNextItemWidth(searchWidth);
        DrawOutputSearchInput();
        DrawSearchControls();
        if (outputTextDirty)
        {
            var text = new StringBuilder();
            outputRows.Clear();
            outputMessageRanges.Clear();
            state.Colors.Clear();
            state.ColorLevels.Clear();
            foreach (var entry in outputEntries)
            {
                if (entry.Channel != channel || !ShowOutputLevel(entry.Level)) continue;
                var prefix = entry.DisplayPrefix;
                // Selection copies the original log bytes; only row lookup splits lines.
                var message = entry.DisplayMessage(outputStacktrace);
                var messageStart = text.Length;
                text.Append(prefix).Append(message);
                text.AppendLine();
                outputMessageRanges.Add((messageStart, text.Length - messageStart));
                state.ColorLevels.Add(entry.Level);
                state.Colors.Add(new(messageStart, text.Length - messageStart, OutputMessageColor(entry.Level, outputTheme ?? EditorTheme.ExpressionDark)));
                outputRows.Add(entry.Diagnostic);
                AppendOutputLineBreaks(prefix);
                AppendOutputLineBreaks(message);
            }
            outputText = text.ToString(); outputTextDirty = false;
            outputMatchesDirty = true;
        }
        if (outputMatchesDirty)
        {
            RebuildSearchMatches();
            outputMatchesDirty = false;
            outputSearchChanged = false;
        }
        using var child = ImGuiScopes.Child("Output text", new System.Numerics.Vector2(0, 0), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);
        if (!child.DrawContents) return;
        outputTextView.NativeTextColors = state.Colors;
        Gui.PushFont(Gui.GetFont(), 11);
        try { outputTextView.Draw("##OutputText", outputText, false, Gui.GetContentRegionAvail(), followTail: true, highlights: outputMatches); }
        finally { Gui.PopFont(); }
        WorkflowInteractionRecorder?.Invoke(new { Frame = Gui.GetFrameCount(),
            outputTextView.Active, outputTextView.NativeHovered, outputTextView.CaretLine,
            DoubleClicked = Gui.IsMouseDoubleClicked(ImGuiMouseButton.Left),
            OwnedDoubleClicked = outputTextView.NativeDoubleClicked,
            Clicked = Gui.IsMouseClicked(ImGuiMouseButton.Left), Down = Gui.IsMouseDown(ImGuiMouseButton.Left),
            Released = Gui.IsMouseReleased(ImGuiMouseButton.Left),
            HasDiagnostic = outputTextView.CaretLine < outputRows.Count && outputRows[outputTextView.CaretLine] != null });
        if (outputTextView.Active && outputTextView.NativeHovered && outputTextView.NativeDoubleClicked
            && outputTextView.CaretLine < outputRows.Count && outputRows[outputTextView.CaretLine] is { } diagnostic)
            requestedDiagnostic = diagnostic;
    }
    private void AppendOutputLineBreaks(ReadOnlySpan<char> text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n')) continue;
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            outputRows.Add(null);
        }
    }
    private bool ShowOutputLevel(LogMessageType level) => level switch
    {
        LogMessageType.Debug => outputDebug, LogMessageType.Verbose => outputVerbose,
        LogMessageType.Info => outputInfo, LogMessageType.Warning => outputWarning,
        LogMessageType.Error => outputError, LogMessageType.Fatal => outputFatal, _ => true,
    };
    private void DrawOutputLevel(string label, ref bool visible, string context = "", bool affectsText = true)
    {
        var width = UiScale(20);
        var right = Gui.GetCursorScreenPos().X + Gui.GetContentRegionAvail().X;
        if (Gui.GetItemRectMax().X + Gui.GetStyle().ItemSpacing.X + width < right) Gui.SameLine();
        using (ImGuiScopes.Color(ImGuiCol.Button, visible ? Gui.GetStyle().Colors[(int)ImGuiCol.HeaderActive] : System.Numerics.Vector4.Zero))
        {
            if (Gui.Button("##output-level/" + label, new System.Numerics.Vector2(width)))
            {
                visible = !visible;
                if (affectsText) outputTextDirty = true;
                else outputMatchesDirty = true;
            }
        }
        var minimum = Gui.GetItemRectMin();
        if (label == "Show Stacktrace")
        {
            var text = "(...)";
            var size = Gui.CalcTextSize(text);
            Gui.GetWindowDrawList().AddText(minimum + (new System.Numerics.Vector2(width) - size) * .5f,
                Gui.GetColorU32(ImGuiCol.Text), text);
        }
        else if (currentIcons?.Invoke() is { } icons && Enum.TryParse<LogMessageType>(label, out var level))
        {
            var imageSize = UiScale(12);
            var origin = minimum + new System.Numerics.Vector2((width - imageSize) * .5f);
            Gui.GetWindowDrawList().AddImage(icons.Get("output-level-" + label.ToLowerInvariant()), origin,
                origin + new System.Numerics.Vector2(imageSize), System.Numerics.Vector2.Zero, System.Numerics.Vector2.One,
                OutputMessageColor(level, outputTheme ?? EditorTheme.ExpressionDark));
        }
        if (Gui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            Gui.SetTooltip(EditorLocalization.Text(label == "Show Stacktrace" ? "Toggle Exception Stack Trace" : "Toggle " + label, "ToolTip"));
        WorkflowControlRecorder?.Invoke("output/level/" + label);
    }
}
