using System.Collections.Immutable;
using System.Text.Json;
using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Schema;

public sealed partial class ShaderSourceBlock
{
    private ShaderSpecialization[] ReadSpecializations(Shader shader)
    {
        var result = new List<ShaderSpecialization>();
        foreach (ShaderSpecialization specialization in Specializations) Add(specialization);
        for (int entryIndex = 0; entryIndex < shader.SlangEntryCount; entryIndex++)
        {
            FunctionReflection function = shader.GetSlangEntryPoint(entryIndex).GetFunctionReflection();
            for (uint index = 0; index < function.AttributeCount; index++)
            {
                AttributeReflection attribute = function.GetAttribute(index);
                if (attribute.Name != "ShaderSpecialization") continue;
                if (attribute.ArgumentCount != 1)
                    throw new InvalidDataException("ShaderSpecialization requires one JSON string array.");
                using JsonDocument arguments = JsonDocument.Parse(attribute.GetArgumentValueString(0));
                Add(new ShaderSpecialization
                {
                    EntryPoint = function.Name,
                    Arguments = arguments.RootElement.EnumerateArray()
                        .Select(static value => value.GetString()
                            ?? throw new InvalidDataException("A shader specialization expression cannot be null."))
                        .ToImmutableList(),
                });
            }
        }
        return result.ToArray();

        void Add(ShaderSpecialization specialization)
        {
            if (!result.Any(existing => existing.EntryPoint == specialization.EntryPoint &&
                existing.Arguments.SequenceEqual(specialization.Arguments)))
                result.Add(specialization);
        }
    }
}
