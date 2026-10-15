using Stride.Core;
using Stride.Core.Assets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Stride.Core.Diagnostics;
using Stride.Core.Reflection;

namespace Stride.Editor.ImGui;

/// <summary>Shared user documentation independent of a WPF session or presentation framework.</summary>
internal sealed class AssetUserDocumentation(Func<Assembly, string?>? resolveLocation = null)
{
    private static readonly Logger Log = GlobalLogger.GetLogger(nameof(AssetUserDocumentation));
    private readonly ConditionalWeakTable<Assembly, AssemblyDocumentation> assemblies = new();
    private sealed class AssemblyDocumentation(Guid moduleId)
    {
        internal Guid ModuleId { get; } = moduleId;
        internal readonly object Gate = new();
        internal string? Location;
        internal long NextProbe;
        internal (long Time, long Length) UserDocStamp, XmlStamp;
        internal Dictionary<string, string> Entries = [];
        internal bool Loaded, HasDocument;
    }
    public string? GetMemberDocumentation(IMemberDescriptor member, Type? rootType)
    {
        ArgumentNullException.ThrowIfNull(member);
        return GetMemberDocumentation(member.MemberInfo, rootType);
    }
    public string? GetMemberDocumentation(MemberInfo member, Type? rootType = null)
    {
        ArgumentNullException.ThrowIfNull(member);
        var prefix = member is FieldInfo ? 'F' : 'P';
        if (rootType != null && Find(rootType.Assembly, $"{prefix}:{TypeName(rootType)}.{member.Name}") is { } overridden) return overridden;
        return member.DeclaringType == null ? null : Find(member.DeclaringType.Assembly, $"{prefix}:{TypeName(member.DeclaringType)}.{member.Name}");
    }
    public string? GetPropertyKeyDocumentation(PropertyKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.OwnerType == null ? null : Find(key.OwnerType.Assembly, $"F:{TypeName(key.OwnerType)}.{key.Name.Split('.').Last()}");
    }
    public void ClearCachedAssemblyDocumentation(Assembly assembly) => assemblies.Remove(assembly);
    public bool CacheAssemblyDocumentation(Assembly assembly)
    {
        var entry = Read(assembly);
        return entry?.HasDocument == true;
    }
    private string? Find(Assembly assembly, string key)
    {
        var entry = Read(assembly);
        if (entry == null) return null;
        lock (entry.Gate) return entry.Entries.GetValueOrDefault(key);
    }
    private AssemblyDocumentation? Read(Assembly assembly)
    {
        if (assembly.IsDynamic) return null;
        var entry = assemblies.GetValue(assembly, current => new(current.ManifestModule.ModuleVersionId));
        lock (entry.Gate)
        {
            // Assemblies are immutable. File versions also invalidate negative cache entries when documentation is produced later.
            if (Environment.TickCount64 < entry.NextProbe) return entry;
            entry.NextProbe = Environment.TickCount64 + 1000;
            var location = resolveLocation?.Invoke(assembly);
            if (string.IsNullOrEmpty(location)) location = assembly.Location;
            if (string.IsNullOrEmpty(location)) return entry;
            var stem = Path.Combine(Path.GetDirectoryName(location) ?? "", Path.GetFileNameWithoutExtension(location));
            var userDoc = stem + ".usrdoc"; var xml = stem + ".xml";
            var userStamp = Stamp(userDoc); var xmlStamp = Stamp(xml);
            if (entry.Loaded && entry.Location == stem && entry.UserDocStamp == userStamp && entry.XmlStamp == xmlStamp) return entry;
            var result = new Dictionary<string, string>();
            var loaded = ReadUserDocumentation(userDoc, result) || ReadXmlDocumentation(xml, result);
            entry.Entries = result; entry.Location = stem; entry.UserDocStamp = userStamp; entry.XmlStamp = xmlStamp;
            entry.Loaded = true; entry.HasDocument = loaded;
            return entry;
        }
    }
    private static (long, long) Stamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.LastWriteTimeUtc.Ticks, file.Length) : (0, 0);
        }
        catch (IOException) { return (0, 0); }
        catch (UnauthorizedAccessException) { return (0, 0); }
    }
    private static bool ReadUserDocumentation(string path, Dictionary<string, string> entries)
    {
        if (!File.Exists(path)) return false;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0 || separator == line.Length - 1) continue;
                entries[line[..separator]] = line[(separator + 1)..];
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Log.Error("Failed while caching user documentation.", error); entries.Clear(); return false; }
    }
    private static bool ReadXmlDocumentation(string path, Dictionary<string, string> entries)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            foreach (var node in document.Descendants("userdoc"))
            {
                var key = node.Parent?.Attribute("name")?.Value;
                var text = node.Value.Trim();
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(text)) continue;
                if (node.Attribute("override") is { } overrideMember && key.StartsWith("T:", StringComparison.Ordinal))
                    key = "P:" + key[2..] + "." + overrideMember.Value;
                entries[key] = text;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
        { Log.Error("Failed while caching from XML documentation.", error); entries.Clear(); return false; }
    }
    private static string TypeName(Type type) => Regex.Replace(type.FullName ?? type.Name, @"\[.*\]", "").Replace('+', '.');
}
