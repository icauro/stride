using System;
using System.IO;
using System.Linq;
using Stride.Assets.Scripts;
using Stride.Core.Assets;
using Stride.Core.Assets.Quantum;
using Stride.Core.Assets.Serializers;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Reflection;

namespace Stride.Assets.Quantum;

public static class VisualScriptLegacyReferenceNormalizer
{
    public static int Normalize(VisualScriptAsset asset)
    {
        var requiresRepair = false;
        foreach (var method in asset.Methods)
        {
            var slots = method.Blocks.Values.SelectMany(block => block.Slots).ToArray();
            if (slots.Select(slot => slot.Id).Distinct().Count() != slots.Length)
                throw new InvalidDataException("Legacy visual-script method contains ambiguous slot identities.");
            foreach (var link in method.Links.Values)
            {
                if (link.Source == null || link.Target == null
                    || !slots.Any(slot => slot.Id == link.Source.Id) || !slots.Any(slot => slot.Id == link.Target.Id))
                    throw new InvalidDataException($"Legacy visual-script link {link.Id} in method {method.Id} has a missing or foreign endpoint: source={link.Source?.Id}, target={link.Target?.Id}, ownedSlots=[{string.Join(",", slots.Select(slot => slot.Id))}].");
                requiresRepair |= !slots.Any(slot => ReferenceEquals(slot, link.Source)) || !slots.Any(slot => ReferenceEquals(slot, link.Target));
            }
        }
        if (!requiresRepair) return 0;
        AssetQuantumRegistry.RegisterAssembly(typeof(AssetPropertyGraph).Assembly);
        AssetQuantumRegistry.RegisterAssembly(typeof(VisualScriptAsset).Assembly);
        var nodes = new AssetNodeContainer { NodeBuilder = { NodeFactory = new AssetNodeFactory() } };
        var container = new AssetPropertyGraphContainer(nodes);
        var log = new LoggerResult();
        using var graph = container.InitializeAsset(new AssetItem(new UFile("LegacyReferences.sdvs"), asset), log)
            ?? throw new InvalidOperationException("Native legacy visual-script graph is unavailable.");
        var references = graph.GenerateObjectReferencesForSerialization(graph.RootNode);
        var targets = FixupObjectReferences.CollectReferenceableObjects(asset, references, true, log);
        var paths = references.Select(reference => (Path: reference.Key.ToMemberPath(asset), Target: targets.TryGetValue(reference.Value, out var target) ? target : null)).ToArray();
        if (log.HasErrors || paths.Any(entry => entry.Target is not Slot))
            throw new InvalidDataException("Legacy visual-script native reference metadata has an unresolved endpoint.");
        var originals = paths.Select(entry => entry.Path.GetValue(asset)).ToArray();
        var changed = 0;
        try
        {
            FixupObjectReferences.FixupReferences(asset, references, targets, false, (path, root, target) =>
            {
                if (!path.Apply(root, MemberPathAction.ValueSet, target) || !ReferenceEquals(path.GetValue(root), target))
                    throw new InvalidDataException("Legacy visual-script native reference application failed.");
                changed++;
            }, log);
            if (log.HasErrors) throw new InvalidDataException("Legacy visual-script native reference normalization failed.");
            return changed;
        }
        catch
        {
            for (var index = 0; index < paths.Length; index++)
                if (!ReferenceEquals(paths[index].Path.GetValue(asset), originals[index])
                    && !paths[index].Path.Apply(asset, MemberPathAction.ValueSet, originals[index]))
                    throw new InvalidOperationException("Legacy visual-script endpoint rollback failed.");
            throw;
        }
    }
}
