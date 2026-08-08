namespace SomeEngine.ECS.Serialization;

public sealed partial class SerializationRegistry
{
    /// <summary>
    /// Whole-World capture pins an ECS-owned copy-on-write root, but a struct containing managed
    /// references may still point at an object mutated through an external alias that World cannot
    /// admit or freeze. Until registration exposes an explicit ownership or deep snapshot contract,
    /// reject every such runtime that is present in the admitted image before caller output.
    /// </summary>
    internal void ValidateWorldSnapshotCapture(
        ReadOnlySpan<SerializationTypeRuntime> capturedRuntimes)
    {
        for (int i = 0; i < capturedRuntimes.Length; i++)
            ValidateWorldSnapshotCapture(capturedRuntimes[i]);
    }

    internal void ValidateWorldSnapshotCapture(SerializationTypeRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!runtime.Entry.ContainsReferences)
            return;

        throw new InvalidOperationException(
            $"Whole-World serialization cannot capture registered type " +
            $"'{runtime.Entry.TypeKey.StableName}' because {runtime.ValueType.FullName} " +
            "contains managed references and no deep snapshot-clone contract is available. " +
            "Serialize a reference-free value, or use an entity/component API while the " +
            "application owns the referenced object lifetime.");
    }
}
