using System.Reflection;
using System.Reflection.Emit;
using Stride.Core.Reflection;
using Stride.Core.Yaml;
using Xunit;

namespace Stride.Core.Design.Tests;

public class TestYamlAssemblyRegistration
{
    [Fact]
    public void LateAssetRegistrationInvalidatesYamlFactorySnapshotWithoutDuplicates()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("YamlRegistration_" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        assembly.DefineDynamicModule("Main");
        using var serializer = new RegistrationProbe();
        try
        {
            var initial = serializer.ResetCount;
            AssemblyRegistry.Register(assembly, AssemblyCommonCategories.Assets);
            Assert.Equal(1, serializer.Count(assembly));
            Assert.True(serializer.ResetCount > initial);

            var assetRegistration = serializer.ResetCount;
            AssemblyRegistry.Register(assembly, AssemblyCommonCategories.Engine);
            Assert.Equal(1, serializer.Count(assembly));
            Assert.True(serializer.ResetCount > assetRegistration);

            var beforeRemoval = serializer.ResetCount;
            AssemblyRegistry.Unregister(assembly);
            Assert.Equal(0, serializer.Count(assembly));
            Assert.True(serializer.ResetCount > beforeRemoval);
        }
        finally { AssemblyRegistry.Unregister(assembly); }
    }

    private sealed class RegistrationProbe : YamlSerializerBase
    {
        public int ResetCount { get; private set; }
        public int Count(Assembly assembly) { lock (Lock) return RegisteredAssemblies.Count(candidate => ReferenceEquals(candidate, assembly)); }
        public override void ResetCache() => ResetCount++;
    }
}
