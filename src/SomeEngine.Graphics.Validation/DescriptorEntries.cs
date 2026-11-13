namespace SomeEngine.Graphics.Validation;
internal static class DescriptorEntries
{
    internal static void Validate(ReadOnlySpan<DescriptorEntryDesc> entries)
    {
        if (entries.IsEmpty)
            throw new ArgumentException("A DescriptorTable requires at least one typed entry.", nameof(entries));
        bool samplers = entries[0].Type == ResourceBindingType.Sampler;
        foreach (var entry in entries)
        {
            if (!Enum.IsDefined(entry.Type) || entry.Type == ResourceBindingType.None)
                throw new ArgumentOutOfRangeException(nameof(entries));
            if ((entry.Type == ResourceBindingType.Sampler) != samplers)
                throw new ArgumentException("A DescriptorTable entry must match the table heap type.", nameof(entries));
            ValidateEntry(entry, nameof(entries));
        }
    }

    private const uint MaximumStructuredBufferStride = 2_048;
    private static void ValidateEntry(in DescriptorEntryDesc entry, string parameterName)
    {
        switch (entry.Type)
        {
            case ResourceBindingType.ConstantBuffer:
            case ResourceBindingType.Sampler:
            case ResourceBindingType.AccelerationStructure:
                ValidateEntryShape(entry, parameterName);
                break;
            case ResourceBindingType.BufferSrv:
            case ResourceBindingType.BufferUav:
                ValidateBufferEntry(entry, parameterName);
                break;
            case ResourceBindingType.TextureSrv:
            case ResourceBindingType.TextureUav:
                ValidateTextureEntry(entry, parameterName);
                break;
            default:
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateEntryShape(in DescriptorEntryDesc entry, string parameterName)
    {
        if (entry.Format.HasValue || entry.StructureStride != 0 || entry.TextureDimension.HasValue || entry.HasCounter)
        {
            throw new ArgumentException("The descriptor entry contains fields that do not apply to its type.", parameterName);
        }
    }

    private static void ValidateBufferEntry(in DescriptorEntryDesc entry, string parameterName)
    {
        if (entry.TextureDimension.HasValue || entry.Format.HasValue && entry.StructureStride != 0)
        {
            throw new ArgumentException("A Buffer entry cannot combine texture or typed/structured shapes.", parameterName);
        }

        uint stride = entry.StructureStride;
        if (stride != 0 && ((stride & 3) != 0 || stride > MaximumStructuredBufferStride))
        {
            throw new ArgumentOutOfRangeException(parameterName, $"A structured Buffer stride must be a four-byte multiple no greater than {MaximumStructuredBufferStride}.");
        }

        if (entry.HasCounter && (entry.Type != ResourceBindingType.BufferUav || stride == 0))
        {
            throw new ArgumentException("Only a structured Buffer UAV entry may carry a counter.", parameterName);
        }
    }

    private static void ValidateTextureEntry(in DescriptorEntryDesc entry, string parameterName)
    {
        if (!entry.Format.HasValue || !entry.TextureDimension.HasValue || entry.StructureStride != 0 || entry.HasCounter)
        {
            throw new ArgumentException("A Texture entry requires Format and TextureDimension only.", parameterName);
        }

        if (!Enum.IsDefined(entry.TextureDimension.Value))
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
