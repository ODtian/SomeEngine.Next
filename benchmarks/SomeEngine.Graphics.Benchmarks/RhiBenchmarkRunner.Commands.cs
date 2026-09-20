using SlangShaderSharp;

namespace SomeEngine.Graphics.Benchmarks;

internal static partial class RhiBenchmarkRunner
{
    private static readonly ResourceBinding[] EmptyResources = [];

    private static void DrawRepeated(IGraphicsBackend receiver, CommandContext context, in DrawArguments arguments, int count)
    {
        DrawArguments stableArguments = arguments;
        for (int index = 0; index < count; index++)
            receiver.Draw(context, stableArguments);
    }

    private static void DrawTransientPackets(IGraphicsBackend receiver, CommandContext context, ShaderVariableLayout layout, byte[] packets, in DrawArguments arguments, int count)
    {
        DrawArguments stableArguments = arguments;
        for (int index = 0; index < count; index++)
        {
            ParameterBlockBindings packet = new(layout, EmptyResources, packets.AsSpan(index * 16, 16));
            receiver.SetTransientParameterBindings(context, packet);
            receiver.Draw(context, stableArguments);
        }
    }

    private static void DrawWithRedundantState(IGraphicsBackend receiver, CommandContext context, Pipeline pipeline, PersistentParameterBindings bindings, Viewport[] viewports, ScissorRect[] scissors, in DrawArguments arguments, int count)
    {
        DrawArguments stableArguments = arguments;
        for (int index = 0; index < count; index++)
        {
            receiver.SetPipeline(context, pipeline);
            receiver.SetPersistentParameterBindings(context, bindings);
            receiver.SetViewports(context, viewports);
            receiver.SetScissors(context, scissors);
            receiver.Draw(context, stableArguments);
        }
    }

    private static void RecordMemoryBarriers(IGraphicsBackend receiver, CommandContext context, in MemoryBarrier barrier, int count)
    {
        for (int index = 0; index < count; index++)
            receiver.Barrier(context, barrier);
    }
}
