using System.Text.RegularExpressions;
using System.Text;
using Hexa.NET.ImGui;
using Stride.GameStudio.Helpers;
using Gui = Hexa.NET.ImGui.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorGame
{
    private bool openAbout;
    private string aboutDocumentText = "";
    private bool openAboutDocument;
    private string? measuredAboutDocument;
    private float measuredAboutDocumentFontSize;
    private NVector2 aboutDocumentMeasure;
    private AboutLine[]? aboutBackers;
    private EditorReadonlyRichText? aboutBackersTextView;
    private string aboutBackersText = "";
    private EditorRichTextLink[] aboutBackerLinks = [];
    private EditorRichTextHeading[] aboutBackerHeadings = [];
    private sealed record AboutWord(string Text, string? Address, bool SpaceBefore);
    private sealed record AboutLine(string Text, int HeadingLevel, bool Bullet, AboutWord[] Words);

    private static AboutWord[] AboutWords(string text, string? address = null, bool leadingSpace = false) =>
        Regex.Matches(text, @"\S+").Select(match => new AboutWord(match.Value, address,
            match.Index == 0 ? leadingSpace : char.IsWhiteSpace(text[match.Index - 1]))).ToArray();

    private static string ReadAboutDocument(string name)
    {
        try { return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return "Unable to load the file."; }
    }
    private static AboutLine[] ReadAboutBackers()
    {
        var lines = ReadAboutDocument("BACKERS.md").Replace("\r", "").Split('\n');
        var result = new List<AboutLine>();
        for (var index = 0; index < lines.Length; index++)
        {
            var text = lines[index].Trim();
            if (text.Length == 0) { result.Add(new("", 0, false, [])); continue; }
            var heading = text.TakeWhile(character => character == '#').Count();
            if (index + 1 < lines.Length && lines[index + 1].Trim() is { Length: > 0 } underline
                && underline.All(character => character == '=')) { heading = 1; index++; }
            var bullet = text.StartsWith("* ");
            if (heading > 0) text = text.TrimStart('#').TrimStart();
            if (bullet) text = text[2..];
            var words = new List<AboutWord>(); var position = 0;
            foreach (Match match in Regex.Matches(text, @"\[([^\]]+)\]\((https?://[^)]+)\)"))
            {
                words.AddRange(AboutWords(text[position..match.Index]));
                words.AddRange(AboutWords(match.Groups[1].Value, match.Groups[2].Value,
                    match.Index > 0 && char.IsWhiteSpace(text[match.Index - 1])));
                position = match.Index + match.Length;
            }
            words.AddRange(AboutWords(text[position..]));
            result.Add(new(text, heading, bullet, words.ToArray()));
        }
        return result.ToArray();
    }
    private void DrawAboutDialog()
    {
        if (IsEditorGpuRetirementPending || IsEditorGpuRetirementFinalizing || IsEditorGraphicsRecovering) { aboutBackersTextView?.Suspend(); return; }
        var aboutTitle = EditorLocalization.Widget("About Stride", "About");
        if (openAbout) { Gui.OpenPopup(aboutTitle); openAbout = false; }
        var version = EditorLocalization.Text("Stride version", "About") + " " + StrideGameStudio.EditorVersionWithMetadata;
        var license = EditorLocalization.Text("MIT License", "About");
        var openSourceNotice = EditorLocalization.Text("Stride uses open-source software", "About");
        var thirdPartyLink = EditorLocalization.Text("See the list", "About");
        var privacyLink = EditorLocalization.Text("Privacy policy", "About");
        var work = Gui.GetMainViewport().WorkSize;
        var outerMargin = UiScale(20);
        // AboutPage: 424-DIP logo/backers, 20-DIP Grid and StackPanel margins;
        // reference outer bounds are 759x997 pixels at 144 DPI.
        var popupSize = new NVector2(Math.Min(UiScale(506), Math.Max(1, work.X - outerMargin * 2)),
            Math.Min(UiScale(997f / 1.5f), Math.Max(1, work.Y - outerMargin * 2)));
        Gui.SetNextWindowSize(popupSize, ImGuiCond.Appearing);
        Gui.SetNextWindowSizeConstraints(NVector2.One, NVector2.Max(NVector2.One, work - new NVector2(outerMargin * 2)));
        Gui.SetNextWindowPos(Gui.GetMainViewport().WorkPos + work / 2, ImGuiCond.Appearing, new NVector2(.5f));
        // PushFont takes an unscaled size; ImGui applies FontScaleDpi itself.
        // WPF's body is 12 DIP, including its derived Markdown heading sizes.
        using var aboutFont = ImGuiScopes.Font(12);
        using var aboutModal = ImGuiScopes.Modal(aboutTitle, ImGuiWindowFlags.NoResize);
        if (aboutModal.DrawContents)
        {
            KeepAboutModalInWorkArea(outerMargin);
            var closeHeight = Gui.GetFrameHeightWithSpacing() + UiScale(12);
            var bodySize = new NVector2(0, Math.Max(1, Gui.GetContentRegionAvail().Y - closeHeight));
            using (var aboutContent = ImGuiScopes.Child("About content", bodySize, ImGuiChildFlags.None, ImGuiWindowFlags.None))
            {
                if (aboutContent.DrawContents)
                {
                    using var contentFont = ImGuiScopes.Font(12);
                    using var contentSpacing = ImGuiScopes.Style(ImGuiStyleVar.ItemSpacing, new NVector2(Gui.GetStyle().ItemSpacing.X, UiScale(4)));
                    Gui.SetCursorPosY(Gui.GetCursorPosY() + outerMargin);
                    var width = Math.Min(UiScale(424), Math.Max(1, Gui.GetContentRegionAvail().X - outerMargin * 2));
                    var background = Gui.GetStyle().Colors[(int)ImGuiCol.PopupBg];
                    var logoName = background.X * .2126f + background.Y * .7152f + background.Z * .0722f > .45f
                        ? "about-studio-logo-black" : "about-studio-logo-white";
                    var logo = icons!.Size(logoName);
                    var contentStart = Gui.GetCursorPosX(); var contentWidth = Gui.GetContentRegionAvail().X;
                    Gui.SetCursorPosX(contentStart + Math.Max(0, (contentWidth - width) / 2));
                    Gui.SetCursorPosY(Gui.GetCursorPosY() + Math.Max(0, width * 139 / 424 - width * logo.Y / logo.X));
                    Gui.Image(icons.Get(logoName), new NVector2(width, width * logo.Y / logo.X));
                    Gui.SetCursorPosY(Gui.GetCursorPosY() + UiScale(20));
                    var legalStart = Gui.GetCursorPos();
                    var legalLeft = contentStart + Math.Max(0, (contentWidth - width) / 2);
                    var columnWidth = Math.Max(1, (width - UiScale(20)) / 2);
                    Gui.SetCursorPosX(legalLeft);
                    using (var legalGroup = ImGuiScopes.Group())
                    {
                        using var legalWrap = ImGuiScopes.TextWrap(legalLeft + columnWidth);
                        DrawNativeLabel(version);
                        DrawNativeLabel(StrideGameStudio.CopyrightText1);
                        DrawNativeLabel(StrideGameStudio.CopyrightText2);
                        if (DrawNativeTextLink(license)) OpenAboutDocument("LICENSE.md");
                    }
                    var legalBottom = Gui.GetCursorPosY();
                    var rightStart = legalLeft + columnWidth + UiScale(20);
                    Gui.SetCursorPos(new NVector2(rightStart, legalStart.Y));
                    using (var linksGroup = ImGuiScopes.Group())
                    {
                        var rightAvailable = columnWidth;
                        using var linksWrap = ImGuiScopes.TextWrap(rightStart + rightAvailable);
                        Gui.SetCursorPosX(rightStart + Math.Max(0, rightAvailable - MeasureNativeLabel(openSourceNotice).X));
                        DrawNativeLabel(openSourceNotice);
                        Gui.SetCursorPosX(rightStart + Math.Max(0, rightAvailable - MeasureNativeLabel(thirdPartyLink).X));
                        if (DrawNativeTextLink(thirdPartyLink)) OpenAboutDocument("THIRD PARTY.md");
                        Gui.Dummy(new NVector2(0, Gui.GetTextLineHeight()));
                        Gui.SetCursorPosX(rightStart + Math.Max(0, rightAvailable - MeasureNativeLabel(privacyLink).X));
                        if (DrawNativeTextLink(privacyLink)) Execute(() => OpenHelpPage("https://stride3d.net/legal/privacy-policy"));
                    }
                    Gui.SetCursorPos(new NVector2(contentStart + Math.Max(0, (contentWidth - width) / 2), Math.Max(legalBottom, Gui.GetCursorPosY()) + UiScale(20)));
                    PrepareAboutBackersText();
                    aboutBackersTextView!.GlyphImages = GetScriptGlyphImages();
                    aboutBackersTextView.GlyphCommands = GraphicsContext.CommandList;
                    var constrained = popupSize.Y < UiScale(997f / 1.5f);
                    var backersHeight = constrained ? UiScale(300)
                        : Math.Max(1, Math.Min(UiScale(300), Gui.GetContentRegionAvail().Y - Gui.GetStyle().ItemSpacing.Y));
                    aboutBackersTextView!.Draw("##BackersText", aboutBackersText, aboutBackerHeadings,
                        aboutBackerLinks, new NVector2(width, backersHeight), packagePropertiesBoldFont,
                        openLink: address => Execute(() => OpenHelpPage(address)), expandHeightToContent: constrained);
                }
            }
            var closeWidth = Math.Max(UiScale(80), MeasureNativeLabel(EditorLocalization.Text("Close", "About")).X + UiScale(40));
            Gui.SetCursorPosX(Gui.GetCursorPosX() + Math.Max(0, (Gui.GetContentRegionAvail().X - closeWidth) / 2));
            if (DrawNativeButton(EditorLocalization.Widget("Close", "About"), new NVector2(closeWidth, 0))) Gui.CloseCurrentPopup();
            DrawAboutDocumentDialog();
        }
    }
    private void OpenAboutDocument(string name)
    {
        aboutDocumentText = ReadAboutDocument(name); openAboutDocument = true;
    }
    private void PrepareAboutBackersText()
    {
        if (aboutBackersTextView != null) return;
        aboutBackers ??= ReadAboutBackers();
        var text = new StringBuilder(); var links = new List<EditorRichTextLink>();
        var headings = new List<EditorRichTextHeading>();
        void Append(string value) => text.Append(value);
        foreach (var line in aboutBackers)
        {
            var lineStart = text.Length;
            if (line.Bullet) Append("• ");
            string? address = null; var start = 0;
            foreach (var word in line.Words)
            {
                if (address != word.Address)
                {
                    if (address != null) links.Add(new(start, text.Length - start, address));
                    address = word.Address; start = -1;
                }
                if (word.SpaceBefore) Append(" ");
                if (address != null && start < 0) start = text.Length;
                Append(word.Text);
            }
            if (address != null) links.Add(new(start, text.Length - start, address));
            if (line.HeadingLevel > 0) headings.Add(new(lineStart, text.Length - lineStart, line.HeadingLevel));
            Append("\n");
        }
        aboutBackersText = text.ToString(); aboutBackerLinks = links.ToArray();
        aboutBackerHeadings = headings.ToArray(); aboutBackersTextView = new();
    }
    private void DrawAboutDocumentDialog()
    {
        var title = StrideGameStudio.EditorName + "###AboutDocument";
        if (openAboutDocument) { Gui.OpenPopup(title); openAboutDocument = false; }
        var work = Gui.GetMainViewport().WorkSize; var margin = UiScale(20);
        // WPF MessageDialogChromeStyle is content-sized (minimum 320x120,
        // maximum 768 high); its message template applies ResizeBehavior 5:3.
        if (!ReferenceEquals(measuredAboutDocument, aboutDocumentText) || measuredAboutDocumentFontSize != Gui.GetFontSize())
        {
            measuredAboutDocument = aboutDocumentText; measuredAboutDocumentFontSize = Gui.GetFontSize();
            aboutDocumentMeasure = Gui.CalcTextSize(aboutDocumentText);
        }
        var measured = aboutDocumentMeasure;
        var contentWidth = Math.Max(1, MathF.Round(MathF.Sqrt(5 * Math.Max(1, measured.X * measured.Y) / 3)));
        var contentHeight = MathF.Round(contentWidth * 3 / 5);
        var wanted = new NVector2(Math.Max(UiScale(320), contentWidth + UiScale(40)),
            Math.Clamp(contentHeight + UiScale(70), UiScale(120), UiScale(768)));
        Gui.SetNextWindowSize(NVector2.Min(wanted, NVector2.Max(NVector2.One, work - new NVector2(margin * 2))), ImGuiCond.Appearing);
        Gui.SetNextWindowSizeConstraints(NVector2.One, NVector2.Max(NVector2.One, work - new NVector2(margin * 2)));
        Gui.SetNextWindowPos(Gui.GetMainViewport().WorkPos + work / 2, ImGuiCond.Appearing, new NVector2(.5f));
        using var documentModal = ImGuiScopes.Modal(title, ImGuiWindowFlags.NoResize);
        if (!documentModal.DrawContents) return;
        KeepAboutModalInWorkArea(margin);
        using (var documentBody = ImGuiScopes.Child("Document", new NVector2(0, Math.Max(1, Gui.GetContentRegionAvail().Y - Gui.GetFrameHeightWithSpacing())), ImGuiChildFlags.None, ImGuiWindowFlags.None))
        {
            if (documentBody.DrawContents)
                DrawSelectableText("##AboutDocumentText", aboutDocumentText, size: Gui.GetContentRegionAvail(), nativeShaping: true);
        }
        var buttonWidth = Math.Max(UiScale(80), Gui.CalcTextSize("OK").X + UiScale(40));
        Gui.SetCursorPosX(Gui.GetCursorPosX() + Math.Max(0, Gui.GetContentRegionAvail().X - buttonWidth - UiScale(10)));
        if (Gui.Button(EditorLocalization.Widget("OK", "Button"), new NVector2(buttonWidth, 0))) Gui.CloseCurrentPopup();
    }
    private static void KeepAboutModalInWorkArea(float margin)
    {
        var viewport = Gui.GetMainViewport(); var size = Gui.GetWindowSize(); var position = Gui.GetWindowPos();
        var low = viewport.WorkPos + new NVector2(margin);
        var high = NVector2.Max(low, viewport.WorkPos + viewport.WorkSize - size - new NVector2(margin));
        var clamped = NVector2.Clamp(position, low, high);
        if (clamped != position) Gui.SetWindowPos(clamped);
    }
}
