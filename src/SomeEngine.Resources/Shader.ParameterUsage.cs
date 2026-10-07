using System.Collections.Immutable;
using SlangShaderSharp;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

public partial class Shader
{
    private readonly ImmutableArray<ImmutableHashSet<SlangDescriptorLocation>> _slangUsedParameters;
    private readonly Dictionary<(int Target, string Name), (int Index, EntryPointReflection Entry)> _slangNamedEntries = [];
    private readonly Dictionary<(int Target, string Name, SlangStage Stage), EntryPointReflection> _slangStageEntries = [];

    public IReadOnlySet<SlangDescriptorLocation> GetSlangUsedParameters(int targetIndex, int entryIndex) =>
        _slangUsedParameters[GetCodeIndex(targetIndex, entryIndex)];

    public bool IsSlangParameterLocationUsed(int targetIndex, string entryName,
        SlangParameterCategory category, nuint space, nuint register)
    {
        var entry = _slangNamedEntries[(targetIndex, entryName)];
        return _slangUsedParameters[GetCodeIndex(targetIndex, entry.Index)].Contains(
            new(category, checked((ulong)space), checked((ulong)register)));
    }

    private void IndexSlangEntries()
    {
        for (int target = 0; target < _slangReflections.Length; target++)
        for (int index = 0; index < _slangEntryCount; index++)
        {
            EntryPointReflection entry = _slangReflections[target].GetEntryPointByIndex(checked((nuint)index));
            _slangNamedEntries.TryAdd((target, entry.Name), (index, entry));
            _slangStageEntries.TryAdd((target, entry.Name, entry.Stage), entry);
            if (!string.IsNullOrEmpty(entry.NameOverride))
                _slangNamedEntries.TryAdd((target, entry.NameOverride), (index, entry));
        }
    }

    private static ImmutableArray<ImmutableHashSet<SlangDescriptorLocation>> ReadSlangUsedParameters(
        IComponentType code, int targetCount, int entryCount)
    {
        var result = ImmutableArray.CreateBuilder<ImmutableHashSet<SlangDescriptorLocation>>();
        for (int target = 0; target < targetCount; target++)
        {
            for (int entry = 0; entry < entryCount; entry++)
            {
                SlangResult status = code.GetEntryPointMetadata(entry, target, out IMetadata metadata,
                    out ISlangBlob? diagnostics);
                if (status.Failed)
                    throw new InvalidDataException("Slang resource usage failed: " + DiagnosticText(diagnostics));
                ShaderReflection targetLayout = code.GetLayout(target, out _);
                SlangDescriptorLocation[] locations = CollectSlangParameterLocations(
                    targetLayout.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null);
                var used = ImmutableHashSet.CreateBuilder<SlangDescriptorLocation>();
                foreach (SlangDescriptorLocation location in locations)
                {
                    status = metadata.IsParameterLocationUsed(location.Category, checked((nuint)location.Space),
                        checked((nuint)location.Register), out bool referenced);
                    if (status.Failed)
                        throw new InvalidDataException("Slang parameter usage failed: " + status);
                    if (referenced) used.Add(location);
                }
                result.Add(used.ToImmutable());
            }
        }
        return result.ToImmutable();
    }

    private static SlangDescriptorLocation[] CollectSlangParameterLocations(VariableLayoutReflection layout)
    {
        if (layout == VariableLayoutReflection.Null) return [];
        TypeLayoutReflection contents = layout.TypeLayout.UnwrapArray();
        if (contents.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            contents = contents.ElementTypeLayout.UnwrapArray();
        var result = new HashSet<SlangDescriptorLocation>();
        for (nint range = 0; range < contents.BindingRangeCount; range++)
        {
            nint count = contents.GetBindingRangeDescriptorRangeCount(range);
            if (count == 0) continue;
            nint set = contents.GetBindingRangeDescriptorSetIndex(range);
            nint first = contents.GetBindingRangeFirstDescriptorRangeIndex(range);
            for (nint relative = 0; relative < count; relative++)
            {
                nint descriptor = first + relative;
                nint elements = contents.GetDescriptorSetDescriptorRangeDescriptorCount(set, descriptor);
                for (nint element = 0; element < elements; element++)
                    result.Add(SlangDescriptorLocation.FromLayout(layout, contents, set, descriptor, element));
            }
        }
        return [.. result];
    }
}
