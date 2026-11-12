namespace SomeEngine.ECS.Serialization;

public interface IValueCodec<T>
{
    void Write(ref DataWriter writer, in T value);
    void Read(ref DataReader reader, out T value);
}

public interface IComponentCodec<T> : IValueCodec<T> where T : struct
{
    bool SupportsParallelWrite => false;
}
public interface IDurableComponentCodec<T> : IComponentCodec<T> where T : struct;
public enum SavePurpose : byte { RawCheckpoint, DurableSave }
