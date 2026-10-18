using Stride.Core;
using Stride.Core.Serialization;
using Stride.Core.Storage;
using Stride.Rendering;
using Stride.Shaders;
using Stride.Shaders.Compiler;

// Records exact resolved requests for offline preparation. This never compiles
// shaders in the browser or substitutes a shader when the cache is incomplete.
internal sealed class EffectRequestRecorder(EffectCompilerBase compiler, Action<string, string> report)
    : EffectCompilerChain(compiler)
{
    private readonly HashSet<ObjectId> recorded = [];
    public override TaskOrResult<EffectBytecodeCompilerResult> Compile(ShaderMixinSource mixinTree,
        EffectCompilerParameters effectParameters, CompilerParameters compilerParameters, ObjectId effectInputHash)
    {
        if (recorded.Add(effectInputHash))
        {
            using var bytes = new MemoryStream();
            var writer = new BinarySerializationWriter(bytes);
            writer.Write(1);
            writer.Write(mixinTree);
            writer.Write(effectParameters);
            writer.Write(compilerParameters);
            writer.Write(effectInputHash);
            report(effectInputHash.ToString(), Convert.ToBase64String(bytes.ToArray()));
        }
        return base.Compile(mixinTree, effectParameters, compilerParameters, effectInputHash);
    }
}
