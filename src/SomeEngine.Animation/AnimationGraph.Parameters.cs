using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public static partial class AnimationGraph
{
    /// <summary>Changes formula parameters in one instance while retaining its compiled intervals and algorithm history.</summary>
    public static void UpdateParameters<T, TOps, TParameters, TState, TEvaluator>(World world, Entity animation,
        Channel<T> channel, in Spec<T, TOps, TParameters, TState, TEvaluator> spec)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        int type = MotionStorage<T, TOps, TParameters, TState, TEvaluator>.Type;
        MotionStorage<T, TOps, TParameters, TState, TEvaluator>.UseInstanceParameters(world, animation);
        bool found = false;
        int count = PropertyAnimation.BufferCount<Track<T>>(world, animation);
        for (int index = 0; index < count; index++)
        {
            Track<T> track = PropertyAnimation.ReadTrack<T>(world, animation, index);
            if (!track.FromGraph || track.Channel != channel || track.MotionType != type)
                continue;
            DevelopmentCheck.Require(spec.Boundary.End == track.End && spec.Boundary.Duration == track.Duration,
                "Changing graph timing requires compiling and playing the updated definition.");
            var update = (Index: track.MotionIndex, Parameters: spec.Parameters, Initialized: track.MotionInitialized);
            world.ExecuteBufferWrite<MotionInstance<T, TOps, TParameters, TState, TEvaluator>,
                (int Index, TParameters Parameters, bool Initialized)>(animation, ref update,
                static (instances, ref update) =>
                {
                    ref MotionData<T, TParameters, TState> data = ref instances[update.Index].Data;
                    if (TEvaluator.ParametersEqual(in data.Parameters, in update.Parameters))
                        return;
                    if (update.Initialized)
                        TEvaluator.UpdateParameters(in data.Parameters, in update.Parameters, ref data.State);
                    data.Parameters = update.Parameters;
                });
            found = true;
        }
        DevelopmentCheck.Require(found, "Updating graph parameters requires a matching concrete property leaf.");
    }
}
