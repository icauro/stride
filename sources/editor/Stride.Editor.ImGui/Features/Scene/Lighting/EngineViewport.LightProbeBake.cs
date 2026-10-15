using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Rendering.LightProbes;

namespace Stride.Editor.ImGui;

internal sealed partial class EngineViewport
{
    private sealed record ProbeBinding(EntityHierarchyDocument Owner, LightProbeComponent Authored, LightProbeComponent Runtime, List<Color3>? Before);
    private sealed class ProbeBake
    {
        internal required EditorSessionHistory History;
        internal required SceneInstance Scene;
        internal required ProbeBinding[] Bindings;
        internal required Dictionary<EditorAuthoringState, long> Owners;
        internal required int Bounces;
        internal readonly List<IDisposable> Leases = [];
        internal readonly Dictionary<LightProbeComponent, List<Color3>> Preview = [];
        internal LightProbeCaptureSession? Capture;
        internal int Bounce;
        internal bool Published, Restored;
    }
    private ProbeBake? probeBake;
    internal bool HasLightProbeBake => probeBake != null;
    internal void BeginLightProbeBake(int bounces)
    {
        if (bounces < 0) throw new ArgumentOutOfRangeException(nameof(bounces));
        if (probeBake != null) throw new InvalidOperationException("Light probe capture is already running.");
        if (ownedSceneInstance == null || sceneAuthority == null || !ReferenceEquals(sceneAuthority.SceneSystem.SceneInstance, ownedSceneInstance)) throw new InvalidOperationException("The scene is not active.");
        var history = PresentedDocument?.SessionHistory ?? throw new InvalidOperationException("The scene has no canonical history.");
        var bindings = new List<ProbeBinding>();
        var owners = new Dictionary<EditorAuthoringState, long>();
        foreach (var entity in ownedSceneInstance)
        {
            var owner = RuntimeOwner(entity); if (owner == null) continue;
            foreach (var runtime in entity.Components.OfType<LightProbeComponent>())
            {
                if (!owner.Asset.Hierarchy.Parts.TryGetValue(entity.Id, out var design)) throw new InvalidOperationException("The light probe owner changed.");
                var authored = design.Entity.Components.OfType<LightProbeComponent>().SingleOrDefault(component => component.Id == runtime.Id)
                    ?? throw new InvalidOperationException("The light probe component changed.");
                var node = owner.HierarchyGraph.Container.NodeContainer.GetOrCreateNode(authored)[nameof(LightProbeComponent.Coefficients)];
                var state = history.Registry.Find(owner.Asset.Id);
                if (!ReferenceEquals(owner.SessionHistory, history) || !owner.CanEditHierarchyNode(node) || state == null || !ReferenceEquals(state.Document, owner))
                    throw new InvalidOperationException("A light probe is not editable.");
                owners.TryAdd(state, owner.EditRevision);
                bindings.Add(new(owner, authored, runtime, runtime.Coefficients?.ToList()));
            }
        }
        if (bindings.Count == 0) return;
        var job = new ProbeBake { History = history, Scene = ownedSceneInstance, Bindings = bindings.ToArray(), Owners = owners, Bounces = bounces };
        probeBake = job;
        try
        {
            foreach (var state in owners.Keys.ToArray())
            {
                job.Leases.Add(state.AcquireView());
                var owner = (EntityHierarchyDocument)state.Document; owner.EndEdits(); owners[state] = owner.EditRevision;
            }
            foreach (var binding in job.Bindings) job.Preview.Add(binding.Runtime, Enumerable.Repeat(default(Color3), LightProbeGenerator.LambertHamonicOrder * LightProbeGenerator.LambertHamonicOrder).ToList());
            ApplyProbePreview(job);
            if (bounces > 0) job.Capture = new(sceneAuthority, job.Bindings.Select(binding => binding.Runtime).ToArray());
        }
        catch (Exception failure)
        {
            try { CancelLightProbeBake(); }
            catch (Exception recovery) { throw new AggregateException("Light probe capture and cleanup failed.", failure, recovery); }
            throw;
        }
    }
    internal bool StepLightProbeBake()
    {
        if (probeBake is not { } job) return true;
        try
        {
            ValidateProbeBake(job); ApplyProbePreview(job);
            sceneAuthority!.PrepareCapture(game.DrawTime);
            if (job.Capture != null)
            {
                SuppressNavigationForBake(true);
                bool complete;
                try { complete = job.Capture.Step(); }
                finally { SuppressNavigationForBake(false); }
                if (!complete) return false;
                foreach (var result in job.Capture.Results) job.Preview[result.Key] = result.Value.ToList();
                job.Capture.Dispose(); job.Capture = null; job.Bounce++;
                ApplyProbePreview(job);
                if (job.Bounce < job.Bounces)
                { job.Capture = new(sceneAuthority!, job.Bindings.Select(binding => binding.Runtime).ToArray()); return false; }
            }
            ValidateProbeBake(job);
            using var capture = job.History.CaptureOperationsAtomic(() =>
            {
                foreach (var state in job.Owners.Keys)
                {
                    var owner = (EntityHierarchyDocument)state.Document;
                    owner.ApplyInheritedGraphMutation(() =>
                    {
                        foreach (var binding in job.Bindings)
                        {
                            if (!ReferenceEquals(binding.Owner, owner)) continue;
                            var node = owner.HierarchyGraph.Container.NodeContainer.GetOrCreateNode(binding.Authored)[nameof(LightProbeComponent.Coefficients)];
                            if (!owner.CanEditHierarchyNode(node)) throw new InvalidOperationException("A light probe is no longer editable.");
                            node.Update(job.Preview[binding.Runtime].ToList());
                        }
                    }, "Capture LightProbes");
                }
            });
            capture.Publish(job.History, "Capture LightProbes"); job.Published = true;
            FinishProbeBake(job); return true;
        }
        catch (Exception failure)
        {
            try { CancelLightProbeBake(); }
            catch (Exception recovery) { throw new AggregateException("Light probe capture and cleanup failed.", failure, recovery); }
            throw;
        }
    }
    private void ValidateProbeBake(ProbeBake job)
    {
        if (!ReferenceEquals(job.Scene, ownedSceneInstance) || sceneAuthority == null || !ReferenceEquals(sceneAuthority.SceneSystem.SceneInstance, job.Scene)) throw new InvalidOperationException("The capture scene changed.");
        foreach (var pair in job.Owners)
            if (!pair.Key.IsPublished || pair.Key.IsDisposed || !ReferenceEquals(job.History.Registry.Find(pair.Key.AssetId!.Value), pair.Key)
                || ((EntityHierarchyDocument)pair.Key.Document).EditRevision != pair.Value) throw new InvalidOperationException("A capture owner changed.");
        foreach (var binding in job.Bindings)
            if (!job.Scene.Contains(binding.Runtime.Entity) || !ReferenceEquals(RuntimeOwner(binding.Runtime.Entity), binding.Owner)
                || !binding.Runtime.Entity.Components.Contains(binding.Runtime)) throw new InvalidOperationException("A capture component changed.");
    }
    private static void ApplyProbePreview(ProbeBake job)
    {
        foreach (var binding in job.Bindings) binding.Runtime.Coefficients = job.Preview[binding.Runtime];
        job.Scene.GetProcessor<LightProbeProcessor>()?.UpdateLightProbeCoefficients();
    }
    internal void CancelLightProbeBake(SceneViewportResources? retirement = null)
    {
        if (probeBake is not { } job) return;
        if (!job.Published && !job.Restored)
        {
            foreach (var binding in job.Bindings) binding.Runtime.Coefficients = binding.Before;
            job.Scene.GetProcessor<LightProbeProcessor>()?.UpdateLightProbeCoefficients(); job.Restored = true;
        }
        if (job.Capture != null)
        { if (retirement != null) retirement.Retain(job.Capture); else job.Capture.Dispose(); job.Capture = null; }
        FinishProbeBake(job, retirement);
    }
    private void FinishProbeBake(ProbeBake job, SceneViewportResources? retirement = null)
    {
        while (job.Leases.Count > 0)
        { if (retirement != null) retirement.RetainLease(job.Leases[^1]); else job.Leases[^1].Dispose(); job.Leases.RemoveAt(job.Leases.Count - 1); }
        probeBake = null;
    }
}
