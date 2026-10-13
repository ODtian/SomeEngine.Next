using Texture = SomeEngine.Resources.Texture;
using System.ComponentModel;
using System.Numerics;
using SomeEngine.Graphics;
using SomeEngine.Serialization;

namespace SomeEngine.Resources;

/// <summary>A material has concrete properties and actual shader and texture references.</summary>
/// <remarks>
/// Shared by game and rendering worlds. Change its fields after CPU frame recording completes
/// and before the next render preparation. Extraction transfers references, not property copies.
/// </remarks>
[SomeEngine.Serialization.AssetUse]
public abstract partial class Material : IDisposable
{
    private ulong[]? _changes;
    private bool _changingProperties;
    private Texture[] _createdTextures = [];
    private int _disposed;
    private Shader? _shader;
    private static long _nextMaterialId;
    private readonly long _materialId = Interlocked.Increment(ref _nextMaterialId);
    public long MaterialId => _materialId;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public string? Name { get; set; }
    public Shader? Shader
    {
        get => _shader;
        set
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (ReferenceEquals(_shader, value)) return;
            _shader = value;
            if (!_changingProperties) { Revision++; RaiseChanged(); }
        }
    }
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
    public abstract void ApplyParameter(MaterialValue parameter, Texture? texture);
    protected abstract void CopyProperties(Material source);

    /// <summary>Applies new fields at the caller's update boundary. Observers run only after commit.</summary>
    public void Update(Material replacement)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(replacement);
        if (ReferenceEquals(this, replacement)) throw new ArgumentException("A material cannot replace itself.", nameof(replacement));
        ObjectDisposedException.ThrowIf(replacement.IsDisposed, replacement);
        if (MaterialTypeId != replacement.MaterialTypeId)
            throw new InvalidDataException("A material update cannot change its concrete type.");
        ulong revision = checked(Revision + 1);
        using Material previousFields = CreateEmpty();
        previousFields._changingProperties = true;
        previousFields.Name = Name;
        previousFields.Shader = Shader;
        previousFields.CopyProperties(this);
        Texture[] previous = _createdTextures;
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
        Revision = revision;
        var errors = new List<Exception>();
        NotifyChanged(errors);
        CloseContent(previous, errors);
        if (errors.Count != 0)
            throw new AggregateException("Material content was updated, but notification or old-content cleanup failed.", errors);
    }

    // Called on a newly created, unpublished material. The dependencies transfer before setters run,
    // so a failed initialization is cleaned up by disposing that material.
    internal void InitializeContent(
        string? name,
        Shader? shader,
        IEnumerable<(MaterialValue Value, Texture? Texture)> values,
        Texture[] createdTextures)
    {
        _createdTextures = [.. _createdTextures, .. createdTextures];
        _changingProperties = true;
        try
        {
            Name = name;
            Shader = shader;
            foreach (var value in values) ApplyParameter(value.Value, value.Texture);
        }
        finally { _changingProperties = false; }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var errors = new List<Exception>();
        CloseContent(Interlocked.Exchange(ref _createdTextures, []), errors);
        if (errors.Count != 0)
            throw new AggregateException("Some material content could not be closed.", errors);
    }

    protected abstract Material CreateEmpty();

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

    private static void CloseContent(Texture[] textures, List<Exception> errors)
    {
        foreach (Texture texture in textures)
            try { texture.Dispose(); }
            catch (Exception error) { errors.Add(error); }
    }
}

public readonly record struct MaterialPropertyDefinition(uint Index, string Name, Type ValueType);
