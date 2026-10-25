using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Physics.Research;

public sealed unsafe partial class PhysicsDemo
{
    private readonly GCHandle _jobCallback;
    private Scheduler _stepJobs = null!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RunRanges(nint context, int count, nint execute, nint data)
    {
        var scene = (PhysicsDemo)GCHandle.FromIntPtr(context).Target!;
        scene._stepJobs.ExecuteParallel(new NativeRangesJob(execute, data), count);
    }

    private readonly struct NativeRangesJob(nint execute, nint data) : IJobParallelFor
    {
        public void Execute(int index) => ((delegate* unmanaged[Cdecl]<nint, int, void>)execute)(data, index);
    }
}
