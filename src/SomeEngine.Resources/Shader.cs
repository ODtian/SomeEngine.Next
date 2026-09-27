using System.Collections.Immutable;
using SlangShaderSharp;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

/// <summary>Ready shader bytecode and reflection. Additional asset reads happen outside this object.</summary>
[SomeEngine.Serialization.AssetUse]
public partial class Shader : IDisposable
{
    private readonly object _updateGate = new();
    private int _disposed;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public ulong Revision { get; private set; } = 1;
    public event Action<Shader>? Changed;

    public static Shader Create(string name, IEnumerable<ShaderProgram> code,
        IReadOnlyList<ShaderEntryPointReflection> reflections, ShaderMetadata? metadata = null)
    {
        var shader = new Shader
        {
            Name = name, Metadata = metadata, EntryPointReflections = reflections.ToImmutableList(),
            Programs = code.Cast<ShaderProgram?>().ToImmutableList(),
        };
        Validate(shader);
        return shader;
    }

    /// <summary>Replaces ready content while preserving this shader's object identity.</summary>
    public void Update(Shader replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref replacement._disposed) != 0, replacement);
        Validate(replacement);
        lock (_updateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            ulong revision = checked(Revision + 1);
            Name = replacement.Name;
            Programs = replacement.Programs;
            EntryPointAttributes = replacement.EntryPointAttributes;
            Reflections = replacement.Reflections;
            EntryPointReflections = replacement.EntryPointReflections;
            Metadata = replacement.Metadata;
            EntryPointMetadata = replacement.EntryPointMetadata;
            SchemaVersion = replacement.SchemaVersion;
            ReplaceSlangData(replacement);
            Revision = revision;
        }
        List<Exception>? errors = null;
        if (Changed is { } changed)
            foreach (Action<Shader> notify in changed.GetInvocationList())
                try { notify(this); }
                catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is not null)
            throw new AggregateException("Shader content was updated, but change notification failed.", errors);
    }

    public void Dispose()
    {
        lock (_updateGate)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            DisposeSlangData();
        }
    }

    public bool TryProgram(string backend, out ShaderProgram program)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        IList<ShaderProgram?>? programs = Programs;
        if (programs is not null)
        {
            for (int index = 0; index < programs.Count; index++)
            {
                ShaderProgram? candidate = programs[index];
                if (candidate is not null && string.Equals(candidate.Backend, backend, StringComparison.Ordinal) &&
                    candidate is ShaderProgram value)
                {
                    program = value;
                    return true;
                }
            }
        }

        program = null!;
        return false;
    }

    public bool TryEntry(
        string backend,
        string entryPoint,
        ShaderStage stage,
        out ShaderProgram program,
        out ShaderEntryPoint entry)
    {
        if (TryProgram(backend, out program))
        {
            SlangStage expectedStage = ToSlangStage(stage);
            for (int index = 0; index < program.EntryPoints.Count; index++)
            {
                ShaderEntryPoint candidate = program.EntryPoints[index];
                if (candidate.Stage == expectedStage &&
                    string.Equals(candidate.Name, entryPoint, StringComparison.Ordinal))
                {
                    entry = candidate;
                    return true;
                }
            }
        }

        entry = null!;
        return false;
    }

    public CookedShaderEntry RequireEntry(
        ShaderTarget target,
        string entryPoint,
        ShaderStage stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
        string backend = target == ShaderTarget.Spirv ? "spirv" : "dxil";
        if (TryEntry(backend, entryPoint, stage, out ShaderProgram program, out ShaderEntryPoint entry))
            return new CookedShaderEntry(program, entry);
        throw new InvalidDataException(
            $"Shader '{Name}' has no cooked {backend} {stage} entry '{entryPoint}'.");
    }

    public bool TryReflection(
        string backend,
        string entryPoint,
        out ShaderEntryPointReflection reflection)
    {
        IList<ShaderEntryPointReflection>? reflections = EntryPointReflections;
        if (reflections is not null)
        {
            for (int index = 0; index < reflections.Count; index++)
            {
                ShaderEntryPointReflection candidate = reflections[index];
                if (string.Equals(candidate.Backend, backend, StringComparison.Ordinal)
                    && string.Equals(candidate.EntryPoint, entryPoint, StringComparison.Ordinal))
                {
                    reflection = candidate;
                    return true;
                }
            }
        }

        reflection = null!;
        return false;
    }

    public bool TryEntry(
        ShaderEntryPointAttribute attribute,
        string? preferred,
        out ShaderEntryPoint entry)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        entry = null!;
        IList<ShaderProgram?>? programs = Programs;
        if (programs is null ||
            attribute.ProgramIndex < 0 ||
            attribute.ProgramIndex >= programs.Count ||
            programs[attribute.ProgramIndex] is not ShaderProgram program ||
            attribute.EntryPointIndex < 0 ||
            attribute.EntryPointIndex >= program.EntryPoints.Count)
        {
            return false;
        }

        entry = program.EntryPoints[attribute.EntryPointIndex];
        return string.IsNullOrWhiteSpace(preferred) ||
            string.Equals(entry.Name, preferred, StringComparison.Ordinal);
    }

    public static void Validate(Shader asset)
    {
        if (asset._slangCode is not null)
            return;
        if (asset.EntryPointReflections is not { Count: > 0 })
        {
            throw new InvalidDataException(
                $"Shader '{asset.Name}' has no serialized entry-point reflection. " +
                "Runtime loading never compiles or reflects source files; recook the shader.");
        }

        int programCount = asset.Programs?.Count ?? 0;
        if (programCount == 0)
            throw new InvalidDataException($"Shader '{asset.Name}' has no cooked program.");
        var backends = new HashSet<string>(StringComparer.Ordinal);
        foreach (ShaderProgram? cooked in asset.Programs!)
        {
            if (cooked is not null && (string.IsNullOrWhiteSpace(cooked.Backend) || !backends.Add(cooked.Backend)))
                throw new InvalidDataException($"Shader '{asset.Name}' has a missing or duplicate cooked backend.");
        }

        if (!asset.Programs!.Any(static code => code is not null))
            throw new InvalidDataException($"Shader '{asset.Name}' has no bytecode for the requested backend.");

        if (asset.EntryPointAttributes is { Count: > 0 })
        {
            foreach (ShaderEntryPointAttribute attribute in asset.EntryPointAttributes)
            {
                if (attribute.ProgramIndex < 0 || attribute.ProgramIndex >= programCount ||
                    attribute.EntryPointIndex < 0 ||
                    (asset.Programs![attribute.ProgramIndex] is { } code && attribute.EntryPointIndex >= code.EntryPoints.Count))
                {
                    throw new InvalidDataException(
                        "Shader entry-point attribute references an invalid cooked entry point.");
                }
            }
        }

        ValidateScalarLayouts(asset.Metadata?.MaterialScalarLayouts);
        ValidateInstanceProperties(asset.Metadata?.InstanceProperties);
    }

    private static void ValidateScalarLayouts(IList<ShaderMaterialScalarLayout>? layouts)
    {
        if (layouts is null)
            return;

        foreach (ShaderMaterialScalarLayout layout in layouts)
        {
            uint previousEnd = 0;
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (layout.Fields is null)
                continue;
            foreach (ShaderMaterialScalarField field in layout.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Name) || !names.Add(field.Name))
                    throw new InvalidDataException($"Shader scalar layout '{layout.Name}' has a missing or duplicate field name.");
                if (field.Size == 0 || field.Offset < previousEnd || field.Offset + field.Size > layout.Size)
                    throw new InvalidDataException($"Shader scalar layout '{layout.Name}' is not canonical and ordered.");
                previousEnd = field.Offset + field.Size;
            }
        }
    }

    private static void ValidateInstanceProperties(
        IList<ShaderInstanceProperty>? properties)
    {
        if (properties is null)
            return;

        for (int index = 0; index < properties.Count; index++)
        {
            ShaderInstanceProperty property = properties[index];
            if (string.IsNullOrWhiteSpace(property.CanonicalId)
                || string.IsNullOrWhiteSpace(property.MaterialScalarLayoutName)
                || string.IsNullOrWhiteSpace(property.MaterialScalarName)
                || string.IsNullOrWhiteSpace(property.Accessor)
                || property.Size == 0
                || property.Alignment == 0
                || property.ScalarType == 0
                || property.DefaultValue is not { } value
                || value.Length != property.Size)
            {
                throw new InvalidDataException("Shader material-instance property metadata is not canonical.");
            }

            for (int previousIndex = 0; previousIndex < index; previousIndex++)
            {
                ShaderInstanceProperty previous = properties[previousIndex];
                if (string.Equals(previous.CanonicalId, property.CanonicalId, StringComparison.Ordinal)
                    && (string.Equals(
                            previous.MaterialScalarLayoutName,
                            property.MaterialScalarLayoutName,
                            StringComparison.Ordinal)
                        || !HasSameEncodingContract(previous, property)))
                {
                    throw new InvalidDataException(
                        $"Shader material-instance property '{property.CanonicalId}' is duplicated or changes its byte encoding.");
                }
            }
        }
    }

    private static bool HasSameEncodingContract(
        ShaderInstanceProperty left,
        ShaderInstanceProperty right)
        => left.Size == right.Size
            && left.Alignment == right.Alignment
            && left.RowCount == right.RowCount
            && left.ColumnCount == right.ColumnCount
            && left.ScalarType == right.ScalarType;

    private static SlangStage ToSlangStage(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => SlangStage.Vertex,
        ShaderStage.Pixel => SlangStage.Fragment,
        ShaderStage.Compute => SlangStage.Compute,
        ShaderStage.Hull => SlangStage.Hull,
        ShaderStage.Domain => SlangStage.Domain,
        ShaderStage.Geometry => SlangStage.Geometry,
        ShaderStage.Amplification => SlangStage.Amplification,
        ShaderStage.Mesh => SlangStage.Mesh,
        ShaderStage.RayGen => SlangStage.RayGeneration,
        ShaderStage.RayMiss => SlangStage.Miss,
        ShaderStage.RayClosestHit => SlangStage.ClosestHit,
        ShaderStage.RayAnyHit => SlangStage.AnyHit,
        ShaderStage.RayIntersection => SlangStage.Intersection,
        ShaderStage.Callable => SlangStage.Callable,
        ShaderStage.Node => SlangStage.Node,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };
}

public readonly record struct CookedShaderEntry(
    ShaderProgram Program,
    ShaderEntryPoint EntryPoint);
