using System.Globalization;
using System.Collections.Concurrent;
using GNU.Gettext;
using Stride.Core.Assets.Editor;
using Stride.Core.Translation;

namespace Stride.Editor.ImGui;

/// <summary>Uses the reference catalogs without loading the WPF presentation assemblies.</summary>
internal sealed class EditorLocalization : ITranslationProvider
{
    private static readonly Lazy<EditorLocalization> instance = new(() =>
    {
        var provider = new EditorLocalization();
        TranslationManager.Instance.RegisterProvider(provider);
        return provider;
    });
    private readonly GettextResourceManager[] catalogs = new[]
    {
        "Stride.GameStudio", "Stride.Core.Assets.Editor.Wpf", "Stride.Assets.Presentation.Wpf",
        "Stride.Core.Presentation.Wpf", "Stride.Core.Presentation",
    }.Select(domain => new GettextResourceManager(domain, typeof(EditorLocalization).Assembly)).ToArray();
    private readonly ConcurrentDictionary<(string Culture, string Context, string Text), string> strings = new();
    private readonly ConcurrentDictionary<(string Culture, string Context, string Label), string> widgets = new();
    private readonly object widgetCacheGate = new();
    private const int MaximumWidgetCaptions = 1024;
    public string BaseName => typeof(EditorLocalization).Assembly.GetName().Name!;

    internal static void Initialize(SupportedLanguage language)
    {
        var provider = instance.Value;
        lock (provider.widgetCacheGate) provider.widgets.Clear();
        var culture = language switch
        {
            SupportedLanguage.MachineDefault => CultureInfo.InstalledUICulture,
            SupportedLanguage.English => CultureInfo.GetCultureInfo("en-US"),
            SupportedLanguage.French => CultureInfo.GetCultureInfo("fr-FR"),
            SupportedLanguage.Japanese => CultureInfo.GetCultureInfo("ja-JP"),
            SupportedLanguage.Russian => CultureInfo.GetCultureInfo("ru-RU"),
            SupportedLanguage.German => CultureInfo.GetCultureInfo("de-DE"),
            SupportedLanguage.Spanish => CultureInfo.GetCultureInfo("es-ES"),
            SupportedLanguage.ChineseSimplified => CultureInfo.GetCultureInfo("zh-Hans"),
            SupportedLanguage.Italian => CultureInfo.GetCultureInfo("it-IT"),
            SupportedLanguage.Korean => CultureInfo.GetCultureInfo("ko-KR"),
            _ => throw new ArgumentOutOfRangeException(nameof(language)),
        };
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        TranslationManager.Instance.CurrentLanguage = culture;
        provider.strings.Clear();
    }

    internal static string Text(string text, string context = "")
        => instance.Value.GetParticularString(context, text);

    internal static string Plural(string singular, string plural, long count, string context = "")
        => instance.Value.GetParticularPluralString(context, singular, plural, count);

    internal static string Widget(string label, string context = "")
    {
        if (label.StartsWith("##", StringComparison.Ordinal)) return label;
        var provider = instance.Value;
        var key = (CultureInfo.CurrentUICulture.Name, context, label);
        if (provider.widgets.TryGetValue(key, out var cached)) return cached;
        var separator = label.IndexOf("##", StringComparison.Ordinal);
        var visible = separator < 0 ? label : label[..separator];
        var translated = Text(visible, context);
        // ImGui's reset includes the ### bytes. Normalize both translated and
        // untranslated captions to the same explicit token so their native hash
        // is identical; a bare English caption must not use a different scheme.
        var normalized = translated + "###" + (label.Contains("###", StringComparison.Ordinal)
            ? label[(label.IndexOf("###", StringComparison.Ordinal) + 3)..] : label);
        lock (provider.widgetCacheGate)
            if (provider.widgets.Count < MaximumWidgetCaptions) provider.widgets.TryAdd(key, normalized);
        return normalized;
    }

    public string GetString(string text) => GetParticularString("", text);
    public string GetParticularString(string context, string text)
    {
        var key = (CultureInfo.CurrentUICulture.Name, context, text);
        if (strings.TryGetValue(key, out var result)) return result;
        foreach (var catalog in catalogs)
        {
            result = context.Length == 0 ? catalog.GetString(text) : catalog.GetParticularString(context, text);
            if (result != text) return strings[key] = result;
        }
        return strings[key] = text;
    }
    public string GetPluralString(string text, string textPlural, long count)
        => GetParticularPluralString("", text, textPlural, count);
    public string GetParticularPluralString(string context, string text, string textPlural, long count)
    {
        foreach (var catalog in catalogs)
        {
            var result = context.Length == 0 ? catalog.GetPluralString(text, textPlural, count)
                : catalog.GetParticularPluralString(context, text, textPlural, count);
            if (result != text && result != textPlural) return result;
        }
        return count == 1 ? text : textPlural;
    }
}
