using SomeEngine.Serialization.Containers;
using SlangShaderSharp;
using SomeEngine.Graphics;

namespace SomeEngine.Assets.Schema;

public partial class Shader : IDisposable
{
    private BinaryDocument<ShaderData>? _document;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private int _disposed;
    public ulong Revision { get; private set; } = 1;
    public event Action<Shader>? Changed;

    public static Shader Create(string name, IEnumerable<ShaderProgram> code,
        IReadOnlyList<ShaderEntryPointReflection> reflections, ShaderMetadata? metadata = null)
    {
        var shader = new Shader
        {
            Name = name, Metadata = metadata, EntryPointReflections = reflections.ToList(),
            Programs = code.Select(static value => new CookedShaderProgram
            {
                Backend = value.Backend, Program = value,
            }).ToList(),
        };
        Validate(shader);
        return shader;
    }

    public static ValueTask<Shader> CreateAsync(Asset<Shader> asset,
        CancellationToken cancellationToken = default) => CreateCoreAsync(asset, null, cancellationToken);

    public static ValueTask<Shader> CreateAsync(Asset<Shader> asset, ShaderTarget target,
        CancellationToken cancellationToken = default)
        => CreateCoreAsync(asset, target == ShaderTarget.Spirv ? "spirv" : "dxil", cancellationToken);

    private static async ValueTask<Shader> CreateCoreAsync(Asset<Shader> asset, string? backend,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        cancellationToken.ThrowIfCancellationRequested();
        BinaryDocument<ShaderData>? document = asset.OpenDocumentGenerated<ShaderData>();
        try
        {
            ShaderData fields = document is null ? asset.GetFieldsGenerated<ShaderData>()
                : await ShaderData.MaterializeAsync(document, cancellationToken, backend).ConfigureAwait(false);
            if (document is null)
            {
                foreach (CookedShaderProgram code in fields.Programs ?? [])
                {
                    code.Program = null;
                    if (backend is not null && code.Backend != backend) continue;
                    Memory<byte> bytes = code.Data
                        ?? throw new InvalidDataException("Shader file content has no bytecode.");
                    code.Program = ShaderProgram.Read(bytes.Span);
                    if (code.Backend != code.Program.Backend)
                        throw new InvalidDataException("Shader bytecode backend does not match the file fields.");
                }
            }
            fields.AssetGuid = null;
            var shader = new Shader { _content = fields, _document = document };
            Validate(shader);
            return shader;
        }
        catch
        {
            if (document is not null) await document.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask LoadBackendAsync(ShaderTarget target, CancellationToken cancellationToken = default)
    {
        string backend = target == ShaderTarget.Spirv ? "spirv" : "dxil";
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (TryProgram(backend, out _)) return;
            await using BinaryDocument<ShaderData> document = _document?.Duplicate()
                ?? throw new InvalidOperationException("This shader has no file containing additional bytecode.");
            ShaderData fields = await ShaderData.MaterializeAsync(document, cancellationToken, backend).ConfigureAwait(false);
            var decoded = new Shader { _content = fields };
            Validate(decoded);
            ShaderData replacement = _content.CopyFields();
            for (int index = 0; index < fields.Programs!.Count; index++)
                if (fields.Programs[index].Backend == backend)
                    replacement.Programs![index] = fields.Programs[index];
            cancellationToken.ThrowIfCancellationRequested();
            _content = replacement;
        }
        finally { _readGate.Release(); }
    }

    public async ValueTask UpdateAsync(Asset<Shader> asset, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using Shader replacement = await CreateAsync(asset, cancellationToken).ConfigureAwait(false);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BinaryDocument<ShaderData>? previous;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            ulong revision = checked(Revision + 1);
            _content = replacement._content;
            previous = Interlocked.Exchange(ref _document, Interlocked.Exchange(ref replacement._document, null));
            Revision = revision;
        }
        finally { _readGate.Release(); }
        var errors = new List<Exception>();
        if (Changed is { } changed)
            foreach (Action<Shader> notify in changed.GetInvocationList())
                try { notify(this); }
                catch (Exception error) { errors.Add(error); }
        try { if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0)
            throw new AggregateException("Shader content was updated, but notification or old-content cleanup failed.", errors);
    }

    public void Dispose()
    {
        BinaryDocument<ShaderData>? document;
        _readGate.Wait();
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            document = Interlocked.Exchange(ref _document, null);
        }
        finally { _readGate.Release(); }
        document?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public bool TryProgram(string backend, out ShaderProgram program)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        IList<CookedShaderProgram>? programs = Programs;
        if (programs is not null)
        {
            for (int index = 0; index < programs.Count; index++)
            {
                CookedShaderProgram candidate = programs[index];
                if (string.Equals(candidate.Backend, backend, StringComparison.Ordinal) &&
                    candidate.Program is ShaderProgram value)
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
        IList<CookedShaderProgram>? programs = Programs;
        if (programs is null ||
            attribute.ProgramIndex < 0 ||
            attribute.ProgramIndex >= programs.Count ||
            programs[attribute.ProgramIndex].Program is not ShaderProgram program ||
            attribute.EntryPointIndex < 0 ||
            attribute.EntryPointIndex >= program.EntryPoints.Count)
        {
            return false;
        }

        entry = program.EntryPoints[attribute.EntryPointIndex];
        return string.IsNullOrWhiteSpace(preferred) ||
            string.Equals(entry.Name, preferred, StringComparison.Ordinal);
    }

    internal static void Validate(Shader asset)
    {
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
        foreach (CookedShaderProgram cooked in asset.Programs!)
        {
            if (string.IsNullOrWhiteSpace(cooked.Backend) || !backends.Add(cooked.Backend))
                throw new InvalidDataException($"Shader '{asset.Name}' has a missing or duplicate cooked backend.");
        }

        if (!asset.Programs!.Any(static code => code.Program is not null))
            throw new InvalidDataException($"Shader '{asset.Name}' has no bytecode for the requested backend.");

        if (asset.EntryPointAttributes is { Count: > 0 })
        {
            foreach (ShaderEntryPointAttribute attribute in asset.EntryPointAttributes)
            {
                if (attribute.ProgramIndex < 0 || attribute.ProgramIndex >= programCount ||
                    attribute.EntryPointIndex < 0 ||
                    (asset.Programs![attribute.ProgramIndex].Program is { } code && attribute.EntryPointIndex >= code.EntryPoints.Count))
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
