using SlangShaderSharp;
using SomeEngine.Diagnostics;

namespace SomeEngine.Resources;

public partial class Shader
{
    private static readonly SemaphoreSlim EntryMetadataConcurrency = new(Math.Clamp(Environment.ProcessorCount, 1, 4));
    private static readonly TraceSite EntryMetadataTrace = new("shader", "Slang.EntryMetadata");
    private readonly Dictionary<int, Task<IMetadata>> _entryMetadata = [];

    public Task<IMetadata> GetSlangEntryMetadataAsync(int targetIndex, EntryPointReflection entry) =>
        GetSlangEntryMetadataAsync(targetIndex, GetEntryIndex(targetIndex, entry));

    /// <summary>Gets the resident Slang entry metadata, sharing its asynchronous backend work.</summary>
    public Task<IMetadata> GetSlangEntryMetadataAsync(int targetIndex, int entryIndex)
    {
        int codeIndex = GetCodeIndex(targetIndex, entryIndex);
        lock (_entryMetadata)
        {
            if (_entryMetadata.TryGetValue(codeIndex, out Task<IMetadata>? read)) return read;
            read = ReadEntryMetadataAsync(targetIndex, entryIndex);
            _entryMetadata.Add(codeIndex, read);
            return read;
        }
    }

    private async Task<IMetadata> ReadEntryMetadataAsync(int targetIndex, int entryIndex)
    {
        await Task.Yield();
        await EntryMetadataConcurrency.WaitAsync().ConfigureAwait(false);
        try
        {
            using var trace = EngineTrace.Begin(EntryMetadataTrace);
            SlangResult result = _slangCode.GetEntryPointMetadata(entryIndex, targetIndex,
                out IMetadata metadata, out ISlangBlob? diagnostics);
            if (result.Failed)
                throw new InvalidDataException($"Slang entry metadata failed for shader '{Name}': {diagnostics?.AsString ?? result.ToString()}");
            return metadata;
        }
        finally
        {
            EntryMetadataConcurrency.Release();
        }
    }
}
