using Stride.Core;
using Stride.Engine;
using Stride.Engine.Design;
using Stride.Games;

namespace Stride.Editor.ImGui;

[DefaultEntityComponentProcessor(typeof(AuthorityParityProcessor), ExecutionMode = ExecutionMode.Editor)]
public sealed class AuthorityParityComponent : EntityComponent
{
    internal Game? ObservedGame;
    internal SceneSystem? ObservedScene;
    internal GameSystemCollection? ObservedSystems;
    internal int Updates;
}

// Runs through the actual Editor-mode SceneInstance processor lifecycle.
public sealed class AuthorityParityProcessor : EntityProcessor<AuthorityParityComponent>
{
    private void Observe(AuthorityParityComponent component)
    {
        var game = Services.GetService<IGame>() as Game
            ?? throw new InvalidOperationException("The native Editor processor has no real Game.");
        if (!ReferenceEquals(game.Services, Services)
            || !ReferenceEquals(game.SceneSystem, Services.GetService<SceneSystem>())
            || !ReferenceEquals(game.GameSystems, Services.GetService<IGameSystemCollection>()))
            throw new InvalidOperationException("A processor observes a borrowed root game instead of its local scene authority.");
        component.ObservedGame = game;
        component.ObservedScene = game.SceneSystem;
        component.ObservedSystems = game.GameSystems;
    }
    protected override void OnEntityComponentAdding(Entity entity, AuthorityParityComponent component, AuthorityParityComponent data)
        => Observe(component);
    public override void Update(GameTime time)
    {
        foreach (var entry in ComponentDatas)
        {
            Observe(entry.Key);
            entry.Key.Updates++;
        }
    }
}
