using Stride.Core.Settings;
using System.Reflection;

namespace Stride.Core.Assets;

[Flags]
public enum PackageSettingsTarget { None = 0, Executable = 1, NonExecutable = 2, All = Executable | NonExecutable }
public readonly record struct PackageSettingRegistration(SettingsKey Key, PackageSettingsTarget Target);

/// <summary>Live registrations shared by asset-editor hosts; providers retain their actual plugin-owned lists.</summary>
public static partial class PackageSettingsRegistry
{
    internal sealed record Provider(object Identity, Func<IEnumerable<PackageSettingRegistration>> Settings, Assembly Owner);
    private static readonly Dictionary<object, Provider> providers = new(ReferenceEqualityComparer.Instance);
    private static long version;
    public static long Version { get { lock (providers) return version; } }
    public static void Register(object provider, Func<IEnumerable<PackageSettingRegistration>> settings)
    {
        ArgumentNullException.ThrowIfNull(provider); ArgumentNullException.ThrowIfNull(settings);
        var owner = provider.GetType().Assembly == typeof(object).Assembly ? settings.Method.DeclaringType?.Assembly ?? typeof(PackageSettingsRegistry).Assembly : provider.GetType().Assembly;
        lock (providers) { var entry = new Provider(provider, settings, owner); providers[provider] = entry; CaptureProvider(entry); version++; }
    }
    public static void Unregister(object provider) { lock (providers) if (providers.Remove(provider)) version++; }
    public static IReadOnlyList<PackageSettingRegistration> GetRegistrations(bool executable)
    {
        Func<IEnumerable<PackageSettingRegistration>>[] sources;
        lock (providers) sources = providers.Values.Select(provider => provider.Settings).ToArray();
        var target = executable ? PackageSettingsTarget.Executable : PackageSettingsTarget.NonExecutable;
        return sources.SelectMany(source => source()).Where(entry => (entry.Target & target) != 0)
            .GroupBy(entry => entry.Key).Select(group => group.First()).ToArray();
    }
}
