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
/// <summary>Marks a codec whose encoding is meant to be read by later builds.</summary>
public interface IDurableCodec;

public interface IDurableComponentCodec<T> : IComponentCodec<T>, IDurableCodec where T : struct;
public enum SavePurpose : byte { RawCheckpoint, DurableSave }
