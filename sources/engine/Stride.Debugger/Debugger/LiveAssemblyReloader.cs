// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Stride.Core;
using Stride.Core.Assets;
using Stride.Core.Assets.Visitors;
using Stride.Core.Diagnostics;
using Stride.Core.Reflection;
using Stride.Core.Serialization;
using Stride.Core.Yaml;
using Stride.Core.Yaml.Events;
using Stride.Core.Yaml.Serialization;
using Stride.Debugger.Target;
using Stride.Engine;

namespace Stride.Debugger
{
    public static class LiveAssemblyReloader
    {
        private static readonly ConditionalWeakTable<IUnloadable, List<object>> UnknownReferences = new ConditionalWeakTable<IUnloadable, List<object>>();
        private static readonly Logger Log = GlobalLogger.GetLogger("LiveAssemblyReload");
        private sealed class ComponentEntry
        {
            public Entity Entity;
            public int Index;
            public EntityComponent Before, After;
            public List<ParsingEvent> Events;
            public List<object> References;
            public bool WasLiveReloading;
        }
        private sealed class ReferenceEntry
        {
            public EntityComponent Owner;
            public MemberPath Path;
            public EntityComponent Before, After;
        }
        public static void Reload(Game game, AssemblyContainer container, List<Assembly> unregister, List<Assembly> register)
        {
            // Live clone factories must never override ordinary asset references.
            // Own a separate cache/profile for this reload and release its registry
            // subscriptions when the transaction and recovery have completed.
            using var yaml = new AssetYamlSerializer(CloneReferenceSerializer.Profile);
            var old = unregister.Distinct().ToArray(); var next = register.Distinct().ToArray();
            if (old.Intersect(next).Any()) throw new InvalidOperationException("A live reload cannot replace an assembly with itself.");
            var oldSet = new HashSet<Assembly>(old);
            if (game != null && oldSet.Contains(game.GetType().Assembly))
                throw new InvalidOperationException("The running game type requires restarting the live session before its assembly can be replaced.");
            var categories = old.ToDictionary(assembly => assembly, assembly => AssemblyRegistry.GetCategories(assembly).ToArray());
            var entries = old.ToDictionary(assembly => assembly, assembly => container.LoadedAssemblies.FirstOrDefault(entry => ReferenceEquals(entry.Assembly, assembly)));
            var components = new List<ComponentEntry>(); var references = new List<ReferenceEntry>();
            var entities = game?.SceneSystem.SceneInstance?.ToArray() ?? Array.Empty<Entity>();
            var replacements = new Dictionary<EntityComponent, EntityComponent>();
            var appliedComponents = new List<ComponentEntry>(); var appliedReferences = new List<ReferenceEntry>();
            var previousScope = CloneReferenceSerializer.References;
            try
            {
                // Capture real live values before module detachment. Never move a
                // running component into a temporary Entity for serialization.
                foreach (var entity in entities)
                    for (var index = 0; index < entity.Components.Count; index++)
                    {
                        var component = entity.Components[index];
                        var visitor = new AffectedVisitor(component, oldSet); visitor.Visit(component);
                        if (!visitor.Found) continue;
                        var entry = new ComponentEntry { Entity = entity, Index = index, Before = component,
                            WasLiveReloading = component is ScriptComponent script && script.IsLiveReloading };
                        if (component is IUnloadable missing && UnknownReferences.TryGetValue(missing, out var payload))
                        { entry.Events = missing.ParsingEvents.ToList(); entry.References = payload.ToList(); }
                        else
                        {
                            entry.References = CaptureUnknownReferencePrefix(component); CloneReferenceSerializer.References = entry.References;
                            entry.Events = new List<ParsingEvent>();
                            yaml.Serialize(new ParsingEventListEmitter(entry.Events), component, typeof(EntityComponent),
                                new SerializerContextSettings { MemberMask = DataMemberAttribute.DefaultMask | ScriptComponent.LiveScriptingMask });
                        }
                        components.Add(entry);
                    }
                foreach (var assembly in old)
                { AssemblyRegistry.Unregister(assembly); DataSerializerFactory.UnregisterSerializationAssembly(assembly); container.RemoveAssembly(assembly); }
                foreach (var assembly in next)
                { RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle); AssemblyRegistry.Register(assembly, AssemblyCommonCategories.Assets); DataSerializerFactory.RegisterSerializationAssembly(assembly); }
                // Prepare every new instance first, then restore fields against the
                // same instances. Cross-script references and cycles retain identity.
                foreach (var entry in components)
                {
                    CloneReferenceSerializer.References = entry.References; entry.After = Deserialize(yaml, entry, null);
                    if (entry.After.Id != entry.Before.Id) throw new InvalidOperationException("A live component changed identity during reload.");
                    replacements.Add(entry.Before, entry.After);
                }
                foreach (var entry in components)
                {
                    entry.References = entry.References.Select(reference => reference is EntityComponent component && replacements.TryGetValue(component, out var replacement) ? (object)replacement : reference).ToList();
                    CloneReferenceSerializer.References = entry.References;
                    var restored = entry.After is IUnloadable ? entry.After : Deserialize(yaml, entry, entry.After);
                    if (!ReferenceEquals(restored, entry.After)) throw new InvalidOperationException("Live reference restoration replaced its prepared component.");
                    var unknowns = new UnknownVisitor(restored); unknowns.Visit(restored);
                    foreach (var unknown in unknowns.Values) { UnknownReferences.Remove(unknown); UnknownReferences.Add(unknown, entry.References); }
                }
                foreach (var entity in entities)
                    foreach (var component in entity.Components)
                        if (!replacements.ContainsKey(component)) new ReferenceVisitor(component, replacements, references).Visit(component);
                foreach (var entry in components)
                {
                    if (entry.Entity.Components.Count <= entry.Index || !ReferenceEquals(entry.Entity.Components[entry.Index], entry.Before)) throw new InvalidOperationException("The live component owner changed during preparation.");
                    if (entry.Before is ScriptComponent before && entry.After is ScriptComponent after) game.Script.LiveReload(before, after);
                    appliedComponents.Add(entry); entry.Entity.Components[entry.Index] = entry.After;
                }
                foreach (var entry in references)
                { appliedReferences.Add(entry); if (!entry.Path.Apply(entry.Owner, MemberPathAction.ValueSet, entry.After)) throw new InvalidOperationException("A live reference could not be rebound."); }
            }
            catch (Exception failure)
            {
                var errors = new List<Exception>();
                foreach (var entry in appliedReferences.AsEnumerable().Reverse())
                    try { if (!entry.Path.Apply(entry.Owner, MemberPathAction.ValueSet, entry.Before)) throw new InvalidOperationException("A live reference could not be restored."); } catch (Exception error) { errors.Add(error); }
                foreach (var entry in appliedComponents.AsEnumerable().Reverse())
                    try { if (ReferenceEquals(entry.Entity.Components[entry.Index], entry.After)) entry.Entity.Components[entry.Index] = entry.Before; if (entry.Before is ScriptComponent script) script.IsLiveReloading = entry.WasLiveReloading; } catch (Exception error) { errors.Add(error); }
                foreach (var assembly in next.Reverse())
                    try { AssemblyRegistry.Unregister(assembly); DataSerializerFactory.UnregisterSerializationAssembly(assembly); } catch (Exception error) { errors.Add(error); }
                foreach (var assembly in old)
                    try
                    {
                        if (entries[assembly] is LoadedAssembly entry && !container.LoadedAssemblies.Any(candidate => ReferenceEquals(candidate.Assembly, assembly))) container.RestoreAssembly(entry);
                        AssemblyRegistry.Register(assembly, categories[assembly]); DataSerializerFactory.RegisterSerializationAssembly(assembly);
                    }
                    catch (Exception error) { errors.Add(error); }
                if (errors.Count != 0) { errors.Insert(0, failure); throw new AggregateException("Live assembly reload and recovery failed.", errors); }
                throw;
            }
            finally { CloneReferenceSerializer.References = previousScope; }
        }
        private static EntityComponent Deserialize(AssetYamlSerializer yaml, ComponentEntry entry, EntityComponent existing)
        {
            var reader = new EventReader(new MemoryParser(entry.Events.ToList()));
            return yaml.Deserialize(reader, existing, typeof(EntityComponent), out _,
                new SerializerContextSettings { Logger = Log, MemberMask = DataMemberAttribute.DefaultMask | ScriptComponent.LiveScriptingMask }) as EntityComponent
                ?? throw new InvalidOperationException("The live component could not be restored from its YAML snapshot.");
        }
        private static List<object> CaptureUnknownReferencePrefix(EntityComponent component)
        {
            var visitor = new UnknownVisitor(component); visitor.Visit(component);
            var prefix = new List<object>();
            foreach (var unknown in visitor.Values)
                if (UnknownReferences.TryGetValue(unknown, out var payload))
                {
                    for (var index = 0; index < Math.Min(prefix.Count, payload.Count); index++)
                        if (!ReferenceEquals(prefix[index], payload[index])) throw new InvalidOperationException("Moved unknown live data has incompatible reference bindings.");
                    if (payload.Count > prefix.Count) prefix.AddRange(payload.Skip(prefix.Count));
                }
            return prefix;
        }
        private sealed class UnknownVisitor : AssetVisitorBase
        {
            private readonly EntityComponent root;
            public readonly List<IUnloadable> Values = new List<IUnloadable>();
            public UnknownVisitor(EntityComponent root) { this.root = root; }
            public override void VisitObject(object value, ObjectDescriptor descriptor, bool visitMembers)
            {
                if (value is IUnloadable unknown) { Values.Add(unknown); return; }
                if (!ReferenceEquals(value, root) && (value is Entity || value is EntityComponent || AssetRegistry.IsExactContentType(value.GetType()))) return;
                base.VisitObject(value, descriptor, visitMembers);
            }
        }
        private sealed class AffectedVisitor : AssetVisitorBase
        {
            private readonly EntityComponent root; private readonly HashSet<Assembly> assemblies;
            public bool Found;
            public AffectedVisitor(EntityComponent root, HashSet<Assembly> assemblies) { this.root = root; this.assemblies = assemblies; }
            public override void VisitObject(object value, ObjectDescriptor descriptor, bool visitMembers)
            {
                if (!ReferenceEquals(value, root) && (value is Entity || value is EntityComponent || AssetRegistry.IsExactContentType(value.GetType()))) return;
                if (value is IUnloadable || assemblies.Contains(value.GetType().Assembly)) { Found = true; return; }
                if (!Found) base.VisitObject(value, descriptor, visitMembers);
            }
        }
        private sealed class ReferenceVisitor : AssetVisitorBase
        {
            private readonly EntityComponent root; private readonly Dictionary<EntityComponent, EntityComponent> replacements; private readonly List<ReferenceEntry> changes;
            public ReferenceVisitor(EntityComponent root, Dictionary<EntityComponent, EntityComponent> replacements, List<ReferenceEntry> changes) { this.root = root; this.replacements = replacements; this.changes = changes; }
            public override void VisitObject(object value, ObjectDescriptor descriptor, bool visitMembers)
            {
                if (!ReferenceEquals(value, root) && value is EntityComponent component)
                { if (replacements.TryGetValue(component, out var after)) changes.Add(new ReferenceEntry { Owner = root, Path = CurrentPath.Clone(), Before = component, After = after }); return; }
                if (!ReferenceEquals(value, root) && (value is Entity || AssetRegistry.IsExactContentType(value.GetType()))) return;
                base.VisitObject(value, descriptor, visitMembers);
            }
        }
    }
}
