using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
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
        private QueryHandle _inputsQuery;
        private QueryHandle _nodesQuery;
        private readonly List<Entity> _inputs = new();
        private readonly List<FocusInput> _events = new();

        public void OnCreate(ref ImmediateSystemContext context)
        {
            World world = context.World;
            _inputsQuery = world.Query(world.QueryDefinition().All<InputCapture>());
            _nodesQuery = world.Query(world.QueryDefinition().All<Focusable>().Read<LayoutRect>());
        }

        public void OnUpdate(ref ImmediateSystemContext context)
        {
            World world = context.World;
            List<Entity> inputs = _inputs;
            inputs.Clear();
            world.ExecuteQuery(_inputsQuery, ref inputs, static (rows, ref values) =>
            {
                foreach (var row in rows.Rows)
                    values.Add(row.Entity);
            });
            (Entity First, Entity Last, float Minimum, float Maximum) corners =
                (Entity.Null, Entity.Null, float.PositiveInfinity, float.NegativeInfinity);
            world.ExecuteQuery(_nodesQuery, ref corners, static (rows, ref result) =>
            {
                foreach (var row in rows.Rows)
                {
                    var rect = row.Read<LayoutRect>().Value;
                    float position = rect.X + rect.Width / 2 + rect.Y + rect.Height / 2;
                    if (position < result.Minimum)
                    {
                        result.First = row.Entity;
                        result.Minimum = position;
                    }
                    if (position > result.Maximum)
                    {
                        result.Last = row.Entity;
                        result.Maximum = position;
                    }
                }
            });
            Entity focused = Entity.Null;
            world.ExecuteSparseRead<Focused, Entity>(ref focused, static (entities, values, ref result) =>
            {
                if (entities.Length > 0)
                    result = entities[0];
            });
            foreach (Entity inputEntity in inputs)
            {
                if (!world.HasBuffer<FocusInput>(inputEntity))
                    continue;
                List<FocusInput> events = _events;
                events.Clear();
                world.ExecuteBufferWrite<FocusInput, List<FocusInput>>(inputEntity, ref events, static (buffer, ref values) =>
                {
                    foreach (FocusInput input in buffer.ReadSpan())
                        values.Add(input);
                    buffer.Clear();
                });
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
                            world.AddSparse(next, new Focused());
                            focused = next;
                        }
                    }
                    if (confirm && focused != Entity.Null)
                    {
                        Clicks clicks = world.Read<Clicks>(focused);
                        clicks.Count = unchecked(clicks.Count + 1);
                        world.Replace(focused, clicks);
                    }
                }
                InputCapture capture = world.Read<InputCapture>(inputEntity);
                capture.Keyboard = focused != Entity.Null;
                world.Replace(inputEntity, capture);
            }
        }

        public void OnDestroy(ref ImmediateSystemContext context)
        {
            context.World.ReleaseQuery(_inputsQuery);
            context.World.ReleaseQuery(_nodesQuery);
        }
    }
}
