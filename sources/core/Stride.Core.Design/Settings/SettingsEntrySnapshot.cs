using System.Globalization;
using Stride.Core.Yaml;
using Stride.Core.Yaml.Events;

namespace Stride.Core.Settings;

/// <summary>The native YAML value and explicit presence of one profile entry.</summary>
public sealed class SettingsEntrySnapshot
{
    internal readonly List<ParsingEvent> Events;
    public bool IsPresent { get; }
    public string ContentFingerprint { get; }
    internal SettingsEntrySnapshot(bool present, IEnumerable<ParsingEvent> events)
    { IsPresent = present; Events = new(events); ContentFingerprint = present ? Fingerprint(Events) : "<absent>"; }
    internal static string Fingerprint(IEnumerable<ParsingEvent> events)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        var emitter = new Emitter(text, isCanonical: true);
        emitter.Emit(new StreamStart()); emitter.Emit(new DocumentStart());
        foreach (var item in events) emitter.Emit(item);
        emitter.Emit(new DocumentEnd(true)); emitter.Emit(new StreamEnd());
        return text.ToString();
    }
}
