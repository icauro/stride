namespace Stride.Editor.ImGui;

internal sealed partial class EditorExtensionSession
{
    internal bool HasEnumImages(Type type)
    {
        Check(); if (stopped) return false;
        return providers.Any(provider => !provider.Retiring && !IsProviderGenerationUnloading(provider.Type)
            && (provider.Available || provider.Type.Assembly == type.Assembly)
            && provider.Registration.EnumImages.Keys.Any(value => value.GetType() == type));
    }
    internal EditorComponentImageLease? AcquireEnumImage(Enum value)
    {
        Check(); if (stopped) return null;
        var owner = providers.LastOrDefault(provider => !provider.Retiring && !IsProviderGenerationUnloading(provider.Type)
            && (provider.Available || provider.Type.Assembly == value.GetType().Assembly)
            && provider.Registration.EnumImages.ContainsKey(value));
        if (owner == null) return null;
        owner.Leases++; var released = false;
        void Release() { Check(); if (released) return; owner.Leases--; released = true; }
        return new(owner.Registration.EnumImages[value], Release);
    }
}
