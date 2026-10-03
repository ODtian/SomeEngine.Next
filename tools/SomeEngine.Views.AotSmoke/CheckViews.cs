global using static SomeEngine.Views.AotSmoke.CardFunctions;

using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;
using SomeEngine.Views.AotSmoke;
using SomeEngine.Views.Tests;

if (args is ["write-checkpoint", string writePath])
{
    CheckViewCheckpoint.Write(writePath);
    return;
}
if (args is ["read-checkpoint", string readPath])
{
    CheckViewCheckpoint.Read(readPath);
    return;
}

using var frames = new HeadlessFrameDriver();
Entity root = Views.Spawn(frames.World, Entity.Null, new Cards(false));
frames.Frame();
Entity[] first = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
Require(first.Length == 3, "The first frame declares three cards.");
Require(frames.World.ReadSparse<Card>(first[0]).Title == "same", "The named function stores the card parameters.");
Require(frames.World.Read<ElementKey>(first[1]).Key != frames.World.Read<ElementKey>(first[2]).Key,
    "Columns distinguish the two calls on one line.");
frames.World.Replace(first[0], new Selection(9));
frames.World.Replace(first[2], new Selection(7));
frames.World.ReplaceSparse(root, new Cards(true));
frames.Frame();
Entity[] second = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
Require(first[0] != second[0], "Switching branches gives each call its own entity.");
Require(frames.World.Read<Selection>(second[0]).Value == 0, "The second branch starts with its own state.");
Require(first[2] == second[2] && frames.World.Read<Selection>(second[2]).Value == 7,
    "The call on the same column preserves its local state.");
ComposeChecks.Run(frames);
RememberChecks.Run(frames);
Console.WriteLine("View functions, Compose views, Remember, state lifting, parameter comparisons, node identities, and sparse storage passed under NativeAOT.");

static void Require(bool value, string message)
{
    if (!value)
        throw new InvalidOperationException(message);
}

namespace SomeEngine.Views.AotSmoke
{
    public readonly partial record struct Card(string Title) : IView
    {
        public async BuildTask Build()
        {
            if (!Scope.World.Has<Selection>(Scope.Entity))
            {
                Scope.World.Add(Scope.Entity, new Selection(0));
                Scope.World.AddTag<LocalState>(Scope.Entity);
            }
        }
    }

    public readonly record struct Cards(bool Alternate) : IView, ISparseComponent
    {
        public BuildTask Build()
        {
            if (Alternate)
                Scope.View(new Card("same"));
            else
                Card("same");
            Card("left"); Card("right");
            return default;
        }
    }

    public readonly record struct Selection(int Value) : IComponent;
}
