namespace Stride.Editor.ImGui;

internal sealed partial class OutputPanel
{
    // The original Build and Project tabs each own their TextLogViewer state.
    private sealed class ChannelState
    {
        internal bool Debug, Verbose, Info = true, Warning = true, Error = true, Fatal = true, Stacktrace = true;
        internal bool TextDirty = true, MatchesDirty = true, MatchCase, MatchWord, SearchChanged, HasNewMessages, ErrorActivationSeen;
        internal string Search = "", Text = "";
        internal string SearchDraft = "";
        internal long SearchValidationDeadline;
        internal int MatchIndex = -1;
        internal readonly List<BuildDiagnostic?> Rows = [];
        internal readonly List<(int Start, int Length)> MessageRanges = [];
        internal readonly List<WindowsTextLayout.ColorRange> Colors = [];
        internal readonly List<Stride.Core.Diagnostics.LogMessageType> ColorLevels = [];
        internal readonly List<EditorSelectableTextHighlight> Matches = [];
        internal readonly EditorSelectableText TextView = new() { FollowTailToleranceDips = 1, FollowTailWithSelection = true };
    }
    private readonly Dictionary<string, ChannelState> outputChannels = new(StringComparer.Ordinal)
    {
        ["Build"] = new(), ["Project"] = new(),
    };
    private string? requestedOutputChannel;
    private ChannelState CurrentOutput => outputChannels[outputChannel];
    private ref bool outputDebug => ref CurrentOutput.Debug;
    private ref bool outputVerbose => ref CurrentOutput.Verbose;
    private ref bool outputInfo => ref CurrentOutput.Info;
    private ref bool outputWarning => ref CurrentOutput.Warning;
    private ref bool outputError => ref CurrentOutput.Error;
    private ref bool outputFatal => ref CurrentOutput.Fatal;
    private ref bool outputStacktrace => ref CurrentOutput.Stacktrace;
    private ref bool outputTextDirty => ref CurrentOutput.TextDirty;
    private ref bool outputMatchesDirty => ref CurrentOutput.MatchesDirty;
    private ref bool outputMatchCase => ref CurrentOutput.MatchCase;
    private ref bool outputMatchWord => ref CurrentOutput.MatchWord;
    private ref bool outputSearchChanged => ref CurrentOutput.SearchChanged;
    private ref string outputSearch => ref CurrentOutput.Search;
    private ref string outputText => ref CurrentOutput.Text;
    private ref int outputMatchIndex => ref CurrentOutput.MatchIndex;
    private List<BuildDiagnostic?> outputRows => CurrentOutput.Rows;
    private List<(int Start, int Length)> outputMessageRanges => CurrentOutput.MessageRanges;
    private List<EditorSelectableTextHighlight> outputMatches => CurrentOutput.Matches;
    private EditorSelectableText outputTextView => CurrentOutput.TextView;
    private void InvalidateOutputChannels()
    {
        foreach (var state in outputChannels.Values) state.TextDirty = true;
    }
}
