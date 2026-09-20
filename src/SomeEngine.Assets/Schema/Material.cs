using System.ComponentModel;
using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Schema;

/// <summary>A material has concrete properties and actual shader and texture references.</summary>
public abstract partial class Material : IDisposable
{
    private static readonly Dictionary<Guid, (Func<Material> Value, Func<AssetId<Material>, Asset> Asset)> Types = [];
    private static readonly object TypeGate = new();
    private ulong[]? _changes;
    private bool _changingProperties;
    private Texture[] _createdTextures = [];
    private Shader? _createdShader;
    private int _disposed;
    private Shader? _shader;

    public string? Name { get; set; }
    public Shader? Shader
    {
        get => _shader;
        set
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (ReferenceEquals(_shader, value)) return;
            if (_shader is not null) _shader.Changed -= OnShaderChanged;
            _shader = value;
            if (_shader is not null) _shader.Changed += OnShaderChanged;
            if (!_changingProperties) { Revision++; RaiseChanged(); }
        }
    }
    private void OnShaderChanged(Shader shader) { Revision++; RaiseChanged(); }
    public ulong Revision { get; private set; } = 1;
    public event Action<Material>? Changed;
    public abstract Guid MaterialTypeId { get; }
    public abstract IReadOnlyList<MaterialPropertyDefinition> Properties { get; }
    public int PropertyCount => Properties.Count;

    protected void SetProperty<T>(ref T field, T value, uint index)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _changes ??= new ulong[PropertyCount];
        _changes[index]++;
        if (!_changingProperties)
        {
            Revision++;
            RaiseChanged();
        }
    }

    public ulong GetPropertyRevision(uint index) => _changes?[index] ?? 0;
    public virtual Texture? GetTexture(uint index) => null;
    public virtual DeviceResource? GetDeviceResource(uint index) => null;
    public virtual SamplerDesc GetSampler(uint index) => default;
    public virtual Vector4 GetFloatComponents(uint index) => default;
    public virtual uint GetInteger(uint index) => 0;
    [EditorBrowsable(EditorBrowsableState.Never)]
    public abstract void ApplyParameter(MaterialParameter parameter, Texture? texture);
    protected abstract void CopyProperties(Material source);

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RegisterGeneratedType<T>(string name,
        Func<AssetId<Material>, Asset> createAsset) where T : Material, new()
    {
        lock (TypeGate)
            if (!Types.TryAdd(BinaryTypeId.FromLogicalName(name), (static () => new T(), createAsset)))
                throw new InvalidOperationException($"Material type '{name}' is already registered.");
    }

    public static async ValueTask<Material> CreateAsync(Asset asset, AssetLoader loader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(loader);
        MaterialData fields = await ReadFieldsAsync(asset, loader, [], cancellationToken).ConfigureAwait(false);
        Material result = CreateEmpty(fields.MaterialType);
        var textures = new Dictionary<Guid, Texture>();
        result._changingProperties = true;
        try
        {
            result.Name = fields.Name;
            if (!fields.Shader.IsEmpty)
            {
                await using Shader.Asset shader = await Shader.Asset.OpenAsync(loader, fields.Shader, cancellationToken).ConfigureAwait(false);
                result._createdShader = await Shader.CreateAsync(shader, cancellationToken).ConfigureAwait(false);
                result.Shader = result._createdShader;
            }
            foreach (MaterialParameter parameter in fields.Parameters)
            {
                Texture? texture = null;
                if (parameter.Kind == MaterialParameterKind.Texture && !parameter.Texture.IsEmpty)
                {
                    if (!textures.TryGetValue(parameter.Texture.Value, out texture))
                    {
                        await using Texture.Asset data = await Texture.Asset.OpenAsync(loader, parameter.Texture, cancellationToken).ConfigureAwait(false);
                        texture = await Texture.CreateAsync(data, cancellationToken).ConfigureAwait(false);
                        textures.Add(parameter.Texture.Value, texture);
                    }
                }
                result.ApplyParameter(parameter, texture);
            }
            cancellationToken.ThrowIfCancellationRequested();
            result._createdTextures = [.. textures.Values];
            return result;
        }
        catch (Exception failure)
        {
            result._createdTextures = [.. textures.Values];
            try { result.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            throw;
        }
        finally { result._changingProperties = false; }
    }

    private static async ValueTask<MaterialData> ReadFieldsAsync(Asset asset, AssetLoader loader,
        HashSet<Guid> ancestors, CancellationToken cancellationToken)
    {
        if (!ancestors.Add(asset.Id.Value))
            throw new InvalidDataException("Material parent references contain a cycle.");
        MaterialData fields = asset.CopyValues();
        if (fields.Parent.IsEmpty) return fields;
        await using Asset parent = await Asset.OpenAsync(loader, fields.Parent, cancellationToken).ConfigureAwait(false);
        MaterialData inherited = await ReadFieldsAsync(parent, loader, ancestors, cancellationToken).ConfigureAwait(false);
        if (fields.MaterialType != Guid.Empty && fields.MaterialType != inherited.MaterialType)
            throw new InvalidDataException("A material override cannot change the parent's material type.");
        MaterialData result = inherited.Copy(fields.Name ?? inherited.Name ?? "", asset.Id.Value);
        if (!fields.Shader.IsEmpty) result.Shader = fields.Shader;
        foreach (MaterialParameter parameter in fields.Parameters) result.Set(parameter);
        return result;
    }

    /// <summary>Applies new fields at the caller's update boundary. Observers run only after commit.</summary>
    public async ValueTask UpdateAsync(Asset asset, AssetLoader loader,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using Material replacement = await CreateAsync(asset, loader, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (MaterialTypeId != replacement.MaterialTypeId)
            throw new InvalidDataException("A material update cannot change its concrete type.");
        ulong revision = checked(Revision + 1);
        using Material previousFields = CreateEmpty(MaterialTypeId);
        previousFields._changingProperties = true;
        previousFields.Name = Name;
        previousFields.Shader = Shader;
        previousFields.CopyProperties(this);
        Texture[] previous = _createdTextures;
        Shader? previousShader = _createdShader;
        _changingProperties = true;
        try
        {
            Name = replacement.Name;
            Shader = replacement.Shader;
            CopyProperties(replacement);
        }
        catch
        {
            Name = previousFields.Name;
            Shader = previousFields.Shader;
            CopyProperties(previousFields);
            throw;
        }
        finally { _changingProperties = false; }

        // User notification must not allow replacement.Dispose to destroy the new fields.
        _createdTextures = replacement._createdTextures;
        replacement._createdTextures = [];
        _createdShader = replacement._createdShader;
        replacement._createdShader = null;
        Revision = revision;
        var errors = new List<Exception>();
        NotifyChanged(errors);
        CloseContent(previous, previousShader, errors);
        if (errors.Count != 0)
            throw new AggregateException("Material content was updated, but notification or old-content cleanup failed.", errors);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_shader is not null) _shader.Changed -= OnShaderChanged;
        var errors = new List<Exception>();
        CloseContent(Interlocked.Exchange(ref _createdTextures, []),
            Interlocked.Exchange(ref _createdShader, null), errors);
        if (errors.Count != 0)
            throw new AggregateException("Some material content could not be closed.", errors);
    }

    private static Material CreateEmpty(Guid type)
    {
        lock (TypeGate)
            return Types.TryGetValue(type, out var constructors) ? constructors.Value()
                : throw new InvalidDataException($"Unknown material type '{type}'.");
    }

    private void RaiseChanged()
    {
        if (Changed is null) return;
        var errors = new List<Exception>();
        NotifyChanged(errors);
        if (errors.Count != 0)
            throw new AggregateException("Material fields changed, but one or more observers failed.", errors);
    }

    private void NotifyChanged(List<Exception> errors)
    {
        if (Changed is not { } changed) return;
        foreach (Action<Material> notify in changed.GetInvocationList())
            try { notify(this); }
            catch (Exception error) { errors.Add(error); }
    }

    private static void CloseContent(Texture[] textures, Shader? shader, List<Exception> errors)
    {
        foreach (Texture texture in textures)
            try { texture.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        try { shader?.Dispose(); }
        catch (Exception error) { errors.Add(error); }
    }
}

public readonly record struct MaterialPropertyDefinition(uint Index, string Name, Type ValueType);
