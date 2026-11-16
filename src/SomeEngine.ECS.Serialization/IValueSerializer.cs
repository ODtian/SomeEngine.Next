namespace SomeEngine.ECS.Serialization;

/// <summary>Writes and reads one value. Registering it under a <see cref="SavedTypeKey"/> lets durable saves use it.</summary>
public interface IValueSerializer<T>
{
    void Write(ref SaveWriter writer, in T value);
    void Read(ref SaveReader reader, out T value);
    bool SupportsParallelWrite => false;
}

public enum SavePurpose : byte { RawCheckpoint, DurableSave }
