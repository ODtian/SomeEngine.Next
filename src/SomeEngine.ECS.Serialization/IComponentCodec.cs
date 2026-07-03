namespace SomeEngine.ECS.Serialization;

public interface IComponentCodec<T>
    where T : struct
{
    void Write(ref DataWriter writer, in T value);
    void Read(ref DataReader reader, out T value);
}

