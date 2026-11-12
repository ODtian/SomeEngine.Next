using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements.Consumer.Tests;

internal static partial class FocusChecks
{
    internal static void RunCustomNavigation()
    {
        using var frames = new FocusFrames(new CornerNavigation());
        Entity root = Views.Views.Spawn(frames.World, frames.Canvas, new FocusGrid());
        frames.Frame();
        Gamepad.Navigate(frames.World, Direction.Right);
        frames.Frame();
        CheckDisplay(frames.World, root, 8, 0);
        Keyboard.Key(frames.World, Key.Left, true);
        frames.Frame();
        CheckDisplay(frames.World, root, 0, 0);
        Gamepad.Confirm(frames.World);
        frames.Frame();
        CheckDisplay(frames.World, root, 0, 1);
        Keyboard.Key(frames.World, Key.Space, true);
        frames.Frame();
        CheckDisplay(frames.World, root, 0, 2);
    }

    private sealed class CornerNavigation : ISystem<ImmediateSystemContext>
    {
        private Query _inputsQuery;
        private Query _nodesQuery;
        private readonly List<Entity> _inputs = new();
        private readonly List<FocusInput> _events = new();

        public void OnCreate(ref ImmediateSystemContext context)
        {
            World world = context.World;
            _inputsQuery = world.Query(new QueryDescription().With<InputCapture>());
            _nodesQuery = world.Query(new QueryDescription().With<Focusable>().With<LayoutRect>());
        }

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            World world = context.World;
            List<Entity> inputs = _inputs;
            inputs.Clear();
            using (QueryScope range = _inputsQuery.OpenRead())
                foreach (QueryChunk chunk in range)
                    foreach (Entity entity in chunk.Entities) inputs.Add(entity);
            (Entity First, Entity Last, float Minimum, float Maximum) corners =
                (Entity.Null, Entity.Null, float.PositiveInfinity, float.NegativeInfinity);
            using (QueryScope range = _nodesQuery.OpenRead())
                foreach (QueryChunk chunk in range)
                    foreach (Entity entity in chunk.Entities)
                    {
                        var rect = world.ReadComponentData<LayoutRect>(entity).Value;
                        float position = rect.X + rect.Width / 2 + rect.Y + rect.Height / 2;
                        if (position < corners.Minimum)
                        {
                            corners.First = entity;
                            corners.Minimum = position;
                        }
                        if (position > corners.Maximum)
                        {
                            corners.Last = entity;
                            corners.Maximum = position;
                        }
                    }
            Entity focused;
            using (SparseReadScope<Focused> focus = world.OpenSparseRead<Focused>())
                focused = focus.Count == 0 ? Entity.Null : focus.Entities[0];
            foreach (Entity inputEntity in inputs)
            {
                if (!world.Has<Buffer<FocusInput>>(inputEntity))
                    continue;
                List<FocusInput> events = _events;
                events.Clear();
                {
                    using DynamicBuffer<FocusInput> buffer = world.GetBuffer<FocusInput>(inputEntity);
                    foreach (FocusInput input in buffer.AsSpan()) events.Add(input);
                    buffer.Clear();
                }
                foreach (FocusInput input in events)
                {
                    bool navigation = input.Kind == FocusInputKind.Navigate ||
                        (input.Kind == FocusInputKind.Key && input.Pressed && input.Key <= Key.Down);
                    bool confirm = input.Kind == FocusInputKind.Confirm ||
                        (input.Kind == FocusInputKind.Key && input.Pressed && input.Key is Key.Enter or Key.Space);
                    if (navigation)
                    {
                        bool last = input.Kind == FocusInputKind.Navigate
                            ? input.Direction is Direction.Right or Direction.Down
                            : input.Key is Key.Right or Key.Down;
                        Entity next = last ? corners.Last : corners.First;
                        if (next != Entity.Null && next != focused)
                        {
                            if (focused != Entity.Null)
                                world.RemoveSparse<Focused>(focused);
                            world.SetSparse(next, new Focused());
                            focused = next;
                        }
                    }
                    if (confirm && focused != Entity.Null)
                    {
                        Clicks clicks = world.ReadComponentData<Clicks>(focused);
                        clicks.Count = unchecked(clicks.Count + 1);
                        world.Set(focused, clicks);
                    }
                }
                InputCapture capture = world.ReadComponentData<InputCapture>(inputEntity);
                capture.Keyboard = focused != Entity.Null;
                world.Set(inputEntity, capture);
            }
        }

        public void OnDestroy(ref ImmediateSystemContext context)
        {


        }
    }
}
