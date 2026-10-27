using SlangShaderSharp;
using SomeEngine.Diagnostics;
using SomeEngine.Job;

namespace SomeEngine.Resources;

public partial class Shader
{
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
            read = ReadEntryMetadataAsync(JobSystem.Current, targetIndex, entryIndex);
            _entryMetadata.Add(codeIndex, read);
            return read;
        }
    }

    private async Task<IMetadata> ReadEntryMetadataAsync(Scheduler scheduler, int targetIndex, int entryIndex)
    {
        var values = new IMetadata[1];
        JobHandle completed = await scheduler.ScheduleAsync(new ReadEntryMetadata(this, targetIndex, entryIndex, values),
            new() { Category = ShaderCodeJobs.GetCategory(scheduler) }).ConfigureAwait(false);
        await completed.CompleteAsync().ConfigureAwait(false);
        return values[0];
    }

    private readonly record struct ReadEntryMetadata(Shader Shader, int TargetIndex, int EntryIndex,
        IMetadata[] Values) : IJob
    {
        public void Execute()
        {
            using var trace = EngineTrace.Begin(EntryMetadataTrace);
            SlangResult result = Shader._slangCode.GetEntryPointMetadata(EntryIndex, TargetIndex,
                out IMetadata metadata, out ISlangBlob? diagnostics);
            if (result.Failed)
                throw new InvalidDataException($"Slang entry metadata failed for shader '{Shader.Name}': {diagnostics?.AsString ?? result.ToString()}");
            Values[0] = metadata;
        }
    }
}
