using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Reflection;
using Stride.Core.Assets.Templates;
using Stride.Core.Assets.Quantum.Templates;

namespace Stride.Editor.ImGui;

/// <summary>Exact CLR generator attachments; arbitrary WPF controls are never inferred.</summary>
public static class EditorSessionTemplatePreparations
{
    private sealed record Registration(Func<ITemplateGenerator<SessionTemplateGeneratorParameters>,
        SessionTemplateGeneratorParameters, CancellationToken, IEditorSessionTemplatePreparation> Factory);
    private static readonly object gate = new();
    private static readonly ConditionalWeakTable<Type, Registration> registrations = new();

    public static IDisposable Register(Type generatorType,
        Func<ITemplateGenerator<SessionTemplateGeneratorParameters>, SessionTemplateGeneratorParameters,
            CancellationToken, IEditorSessionTemplatePreparation> factory)
    {
        ArgumentNullException.ThrowIfNull(generatorType); ArgumentNullException.ThrowIfNull(factory);
        if (!typeof(ITemplateGenerator<SessionTemplateGeneratorParameters>).IsAssignableFrom(generatorType))
            throw new ArgumentException("The type is not a native session-template generator.", nameof(generatorType));
        if (EditorExtensionGenerations.IsUnloading(generatorType.Assembly))
            throw new InvalidOperationException("The template generator assembly is unloading.");
        var registration = new Registration(factory);
        lock (gate) { registrations.Remove(generatorType); registrations.Add(generatorType, registration); }
        ObserveUnload(generatorType, registration, generatorType.Assembly);
        ObserveUnload(generatorType, registration, factory.Method.Module.Assembly);
        if (factory.Target is { } target) ObserveUnload(generatorType, registration, target.GetType().Assembly);
        return new RegistrationOwner(generatorType, registration);
    }
    private static void ObserveUnload(Type type, Registration registration, Assembly assembly)
    {
        var context = AssemblyLoadContext.GetLoadContext(assembly);
        if (context?.IsCollectible != true) return;
        var key = new WeakReference<Type>(type); var value = new WeakReference<Registration>(registration);
        context.Unloading += _ =>
        {
            if (!key.TryGetTarget(out var currentType) || !value.TryGetTarget(out var owner)) return;
            lock (gate)
                if (registrations.TryGetValue(currentType, out var current) && ReferenceEquals(owner, current)) registrations.Remove(currentType);
        };
        if (EditorExtensionGenerations.IsUnloading(assembly))
            lock (gate)
                if (registrations.TryGetValue(type, out var current) && ReferenceEquals(current, registration)) registrations.Remove(type);
    }

    internal sealed class Lease(IEditorSessionTemplatePreparation resource, object provider,
        EditorExtensionGenerations.State? declaration, EditorExtensionGenerations.State? target)
    {
        // Retain the actual factory generation until the consumer's phases drain.
        private readonly object retainedProvider = provider;
        internal IEditorSessionTemplatePreparation Resource { get; } = resource;
        internal bool IsUnloading
        {
            get
            {
                var unloading = declaration != null && Volatile.Read(ref declaration.Unloading) != 0
                    || target != null && Volatile.Read(ref target.Unloading) != 0;
                GC.KeepAlive(retainedProvider); return unloading;
            }
        }
    }
    internal static Lease? Create(ITemplateGenerator<SessionTemplateGeneratorParameters> generator,
        SessionTemplateGeneratorParameters parameters, CancellationToken cancellation)
    {
        Registration? registration;
        lock (gate) registrations.TryGetValue(generator.GetType(), out registration);
        if (registration == null && generator is QuantumGraphTemplateGenerator quantum)
        {
            generator = quantum.Inner;
            lock (gate) registrations.TryGetValue(generator.GetType(), out registration);
        }
        if (registration == null) return null;
        var assembly = registration.Factory.Method.Module.Assembly;
        if (EditorExtensionGenerations.IsUnloading(assembly)
            || registration.Factory.Target is { } target && EditorExtensionGenerations.IsUnloading(target.GetType().Assembly))
            throw new InvalidOperationException("The template preparation assembly is unloading.");
        var resource = registration.Factory(generator, parameters, cancellation)
            ?? throw new InvalidOperationException("The native preparation factory returned no owner.");
        return new(resource, registration, EditorExtensionGenerations.Observe(assembly),
            registration.Factory.Target is { } owner ? EditorExtensionGenerations.Observe(owner.GetType().Assembly) : null);
    }
    private sealed class RegistrationOwner(Type type, Registration registration) : IDisposable
    {
        public void Dispose()
        {
            lock (gate)
                if (registrations.TryGetValue(type, out var current) && ReferenceEquals(current, registration)) registrations.Remove(type);
        }
    }
}
