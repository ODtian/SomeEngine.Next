using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation;

public enum TrackEndpoints : byte { Values, Displacement, DisplacementValues }

/// <summary>The values, curve and stage of one animated component field.</summary>
[BufferCapacity(4)]
public struct Track<T> : IBufferElement where T : unmanaged
{
    public Entity Target;
    public Channel<T> Channel;
    public T From, To, Current, Velocity;
    public float Start, Duration;
    public float SampleTime;
    public Curve Curve;
    public ushort Stage;
    public byte Layer;
    public TrackEndpoints Endpoints;
    public uint StartId;
    public bool Finished;
    public bool CaptureStart;
    public int GraphMotion, GraphGroup;
    public ushort MotionStage;
    public uint MotionStartId;
    public T Displacement;
    public bool FromGraph;
    public bool Typed;
    public bool SourceActive;
    public int MotionType;
    public int MotionIndex;
    public bool MotionInitialized;
}
