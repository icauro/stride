using Stride.Core.Assets.Templates;

namespace Stride.Editor.ImGui;

internal sealed partial class EditorExtensionSession
{
    internal EditorAssetTemplateAdapterLease? AcquireAssetTemplateAdapter(TemplateAssetDescription template)
    {
        Check();
        if (stopped) return null;
        foreach (var owner in providers.Where(provider => !provider.Retiring && !IsProviderGenerationUnloading(provider.Type) && provider.Available).Reverse())
            foreach (var adapter in owner.Registration.AssetTemplates.AsEnumerable().Reverse())
                if (adapter.Generator.IsSupportingTemplate(template))
                {
                    EditorAssetTemplateAdapterLease Acquire()
                    {
                        Check();
                        if (stopped || owner.Retiring || !owner.Available) throw new InvalidOperationException("The template provider is no longer available.");
                        owner.Leases++; var released = false;
                        return new(adapter, () =>
                        {
                            Check();
                            if (released || stopped || owner.Retiring || !owner.Available) throw new InvalidOperationException("The template provider is no longer available.");
                        }, () =>
                        {
                            Check();
                            if (!released) { released = true; owner.Leases--; }
                            RetireUnused();
                        }, Acquire);
                    }
                    return Acquire();
                }
        return null;
    }
}

internal sealed class EditorAssetTemplateAdapterLease(EditorAssetTemplateAdapter adapter, Action validate, Action release, Func<EditorAssetTemplateAdapterLease> retain) : IDisposable
{
    internal EditorAssetTemplateAdapter Adapter { get; } = adapter;
    internal void Validate() => validate();
    internal EditorAssetTemplateAdapterLease Retain() { validate(); return retain(); }
    private bool disposed;
    public void Dispose() { if (disposed) return; release(); disposed = true; }
}
