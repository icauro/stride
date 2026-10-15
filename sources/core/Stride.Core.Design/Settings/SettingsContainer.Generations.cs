using System.Diagnostics;
using System.Reflection;
using Stride.Core.IO;
using Stride.Core.Diagnostics;
using Stride.Core.Reflection;
using Stride.Core.Yaml.Events;

namespace Stride.Core.Settings;

public partial class SettingsContainer
{
    private static readonly List<WeakReference<SettingsContainer>> containers = new();
    static SettingsContainer()
    {
        AssemblyRegistry.AssemblyUnregistered += (_, args) => SuspendAssemblyKeys(args.Assembly);
    }
    private readonly Dictionary<SettingsKey, Assembly> keyOwners = new();
    private readonly List<WeakReference<SettingsProfile>> liveProfiles = new();
    internal void TrackProfile(SettingsProfile profile)
    {
        lock (SettingsLock) { liveProfiles.RemoveAll(reference => !reference.TryGetTarget(out _)); liveProfiles.Add(new(profile)); }
    }
    private SettingsProfile[] LiveProfiles()
    {
        liveProfiles.RemoveAll(reference => !reference.TryGetTarget(out _));
        return liveProfiles.Select(reference => reference.TryGetTarget(out var profile) ? profile : null).Where(profile => profile != null).Cast<SettingsProfile>().ToArray();
    }
    private static void TrackContainer(SettingsContainer container)
    { lock (SettingsLock) { containers.RemoveAll(reference => !reference.TryGetTarget(out _)); containers.Add(new(container)); } }
    private static readonly AsyncLocal<SettingsKeyRegistrationCapture?> registrationCapture = new();
    private void RegisterKeyOwner(SettingsKey key, object defaultValue)
    {
        var framework = typeof(SettingsContainer).Assembly;
        var methods = new StackTrace().GetFrames()?.Select(frame => frame.GetMethod()).Where(method => method != null).ToArray() ?? [];
        bool IsExternal(Assembly assembly) => assembly != framework && assembly != typeof(object).Assembly
            && !(assembly.GetName().Name?.StartsWith("System.", StringComparison.Ordinal) ?? false)
            && !(assembly.GetName().Name?.StartsWith("Microsoft.", StringComparison.Ordinal) ?? false);
        var owner = methods.FirstOrDefault(method => method!.IsConstructor && method.IsStatic && IsExternal(method.DeclaringType!.Assembly))?.DeclaringType?.Assembly
            ?? methods.Select(method => method!.DeclaringType?.Assembly).FirstOrDefault(assembly => assembly != null && IsExternal(assembly))
            ?? key.Type.Assembly;
        keyOwners[key] = owner;
        registrationCapture.Value?.Record(new(this, key, defaultValue, owner));
    }
    /// <summary>Captures registrations made while loading one actual assembly, including failed initialization.</summary>
    public static SettingsKeyRegistrationCapture CaptureAssemblyKeys(string assemblyName)
        => new(assemblyName, registrationCapture.Value, capture => registrationCapture.Value = capture);
    /// <summary>Suspends actual keys from one assembly while retaining their generation for replay.</summary>
    public static SettingsKeyGeneration SuspendAssemblyKeys(Assembly assembly)
    {
        lock (SettingsLock)
        {
            var generation = new SettingsKeyGeneration(assembly, CaptureDefinitions(assembly));
            generation.Suspend(); return generation;
        }
    }
    internal static SettingsKeyGeneration.Definition[] CaptureDefinitions(Assembly assembly)
    {
        lock (SettingsLock)
            return containers.Select(reference => reference.TryGetTarget(out var container) ? container : null)
                .Where(container => container != null).SelectMany(container => container!.keyOwners
                    .Where(pair => ReferenceEquals(pair.Value, assembly)).Select(pair => new SettingsKeyGeneration.Definition(container, pair.Key,
                        container.RootProfile.Settings[pair.Key.Name].Value, assembly))).ToArray();
    }
    internal static void ChangeGeneration(SettingsKeyGeneration.Definition[] definitions, bool restore)
    {
        lock (SettingsLock)
        {
            var prepared = new List<(SettingsProfile Profile, UFile Name, SettingsEntry Entry)>();
            foreach (var definition in definitions)
            {
                var container = definition.Container; var key = definition.Key;
                var active = container.settingsKeys.GetValueOrDefault(key.Name);
                if (restore ? active != null && !ReferenceEquals(active, key) : !ReferenceEquals(active, key))
                    throw new InvalidOperationException("The active settings key generation changed: " + key.Name);
                foreach (var profile in container.LiveProfiles().Where(profile => !ReferenceEquals(profile, container.RootProfile)))
                {
                    if (!profile.Settings.TryGetValue(key.Name, out var existing)) continue;
                    var raw = new List<ParsingEvent>(existing.GetSerializableValue(active));
                    object value = raw;
                    if (restore)
                    {
                        try { value = key.ConvertValueStrict(raw); }
                        catch (Exception error) { container.Logger.Warning("The settings value remains in its original YAML form: " + key.Name + ". " + error.Message); }
                    }
                    var entry = SettingsEntry.CreateFromValue(profile, key.Name, value);
                    if (restore && value is not List<ParsingEvent>) PreserveRaw(entry, key, raw);
                    prepared.Add((profile, key.Name, entry));
                }
            }
            foreach (var definition in definitions)
            {
                var container = definition.Container; var key = definition.Key;
                if (restore)
                {
                    container.settingsKeys[key.Name] = key; container.keyOwners[key] = definition.Owner;
                    container.RootProfile.Settings[key.Name] = SettingsEntry.CreateFromValue(container.RootProfile, key.Name, definition.DefaultValue);
                }
                else
                {
                    container.settingsKeys.Remove(key.Name); container.keyOwners.Remove(key); container.RootProfile.Settings.Remove(key.Name);
                }
            }
            foreach (var entry in prepared) entry.Profile.Settings[entry.Name] = entry.Entry;
        }
    }
    internal static void PreserveRaw(SettingsEntry entry, SettingsKey key, List<ParsingEvent> raw)
    {
        entry.RetainedSerializedValue = null; entry.RetainedTypedFingerprint = null;
        entry.RetainedTypedFingerprint = SettingsEntrySnapshot.Fingerprint(entry.GetSerializableValue(key));
        entry.RetainedSerializedValue = new(raw);
    }
}

/// <summary>Retains original key instances/defaults; switching preserves current native profile YAML.</summary>
public sealed class SettingsKeyGeneration
{
    internal sealed record Definition(SettingsContainer Container, SettingsKey Key, object DefaultValue, Assembly Owner);
    private Definition[] definitions;
    private readonly Assembly owner;
    private bool suspended;
    internal SettingsKeyGeneration(Assembly owner, Definition[] definitions) { this.owner = owner; this.definitions = definitions; }
    public void Suspend()
    {
        if (suspended) return;
        definitions = definitions.Concat(SettingsContainer.CaptureDefinitions(owner)).GroupBy(definition => definition.Key).Select(group => group.First()).ToArray();
        SettingsContainer.ChangeGeneration(definitions, false); suspended = true;
    }
    public void Restore() { if (!suspended) return; SettingsContainer.ChangeGeneration(definitions, true); suspended = false; }
    /// <summary>Converts retained raw profile entries after the actual new assembly serializers are registered.</summary>
    public void RebindCurrent()
    {
        if (suspended) throw new InvalidOperationException("The settings generation is suspended.");
        definitions = definitions.Concat(SettingsContainer.CaptureDefinitions(owner)).GroupBy(definition => definition.Key).Select(group => group.First()).ToArray();
        SettingsContainer.ChangeGeneration(definitions, true);
    }
}

public sealed class SettingsKeyRegistrationCapture : IDisposable
{
    private readonly string assemblyName;
    private readonly SettingsKeyRegistrationCapture? previous;
    private readonly Action<SettingsKeyRegistrationCapture?> setCurrent;
    private readonly List<SettingsKeyGeneration.Definition> definitions = new();
    private bool completed;
    internal SettingsKeyRegistrationCapture(string name, SettingsKeyRegistrationCapture? previous, Action<SettingsKeyRegistrationCapture?> setCurrent)
    { assemblyName = name; this.previous = previous; this.setCurrent = setCurrent; setCurrent(this); }
    internal void Record(SettingsKeyGeneration.Definition definition)
    { if (string.Equals(definition.Owner.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)) definitions.Add(definition); previous?.Record(definition); }
    public SettingsKeyGeneration Complete(Assembly owner)
    {
        if (!string.Equals(owner.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)
            || definitions.Any(definition => !ReferenceEquals(definition.Owner, owner)))
            throw new InvalidOperationException("The settings registrations belong to a different assembly generation.");
        completed = true; return new(owner, definitions.ToArray());
    }
    public void Dispose()
    {
        setCurrent(previous);
        if (!completed && definitions.Count != 0) SettingsContainer.ChangeGeneration(definitions.ToArray(), false);
    }
}
