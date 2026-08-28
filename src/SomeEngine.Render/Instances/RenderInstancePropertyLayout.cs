using System.Collections.ObjectModel;

namespace SomeEngine.Render.Instances;

/// <summary>
/// Immutable result of linking opaque property requirements into dense batch metadata words.
/// Property values and physical addresses are deliberately outside this contract.
/// </summary>
public sealed class RenderInstancePropertyLayout : IEquatable<RenderInstancePropertyLayout>
{
    private readonly ReadOnlyCollection<RenderInstancePropertyDescriptor> _properties;
    private readonly Dictionary<RenderInstancePropertyKey, RenderInstancePropertyDescriptor> _propertiesByKey;
    private readonly int _hashCode;

    private RenderInstancePropertyLayout(
        RenderInstancePropertyDescriptor[] properties,
        int metadataWordCount)
    {
        _properties = Array.AsReadOnly(properties);
        _propertiesByKey = new Dictionary<RenderInstancePropertyKey, RenderInstancePropertyDescriptor>(
            properties.Length);
        foreach (RenderInstancePropertyDescriptor property in properties)
            _propertiesByKey.Add(property.Key, property);
        MetadataWordCount = metadataWordCount;
        _hashCode = ComputeHashCode(properties, metadataWordCount);
    }

    public IReadOnlyList<RenderInstancePropertyDescriptor> Properties => _properties;

    public int MetadataWordCount { get; }

    internal static RenderInstancePropertyLayout Create(
        IEnumerable<RenderInstancePropertyRegistration> registrations)
    {
        var sorted = new List<RenderInstancePropertyRegistration>(registrations);
        sorted.Sort(RenderInstancePropertyRegistrationComparer.Instance);
        var properties = new RenderInstancePropertyDescriptor[sorted.Count];
        int metadataWordOffset = 0;
        for (int index = 0; index < sorted.Count; index++)
        {
            properties[index] = new RenderInstancePropertyDescriptor(
                sorted[index],
                index,
                metadataWordOffset);
            metadataWordOffset = checked(
                metadataWordOffset + sorted[index].Declaration.Encoding.MetadataWordCount);
        }

        return new RenderInstancePropertyLayout(properties, metadataWordOffset);
    }

    public static RenderInstancePropertyLayout Compose(
        params RenderInstancePropertyLayout[] contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        var builder = new RenderInstancePropertyLayoutBuilder();
        foreach (RenderInstancePropertyLayout contributor in contributors)
        {
            ArgumentNullException.ThrowIfNull(contributor);
            builder.Include(contributor);
        }
        return builder.Freeze();
    }

    public ResolvedRenderInstanceProperty<T> Resolve<T>(RenderInstanceProperty<T> property)
        where T : unmanaged
    {
        if (!property.IsValid)
            throw new ArgumentException("The render-instance property token is uninitialized.", nameof(property));
        RenderInstancePropertyDeclaration declaration = property.Declaration;
        if (!_propertiesByKey.TryGetValue(declaration.Key, out RenderInstancePropertyDescriptor? descriptor)
            || !descriptor.HasSameContract(declaration))
        {
            throw new ArgumentException(
                $"Property '{declaration.Key}' is not part of this composed metadata contract with the same encoding.",
                nameof(property));
        }
        return ResolveDescriptor<T>(descriptor, nameof(property));
    }

    public ResolvedRenderInstanceProperty<T> Resolve<T>(RenderInstancePropertyKey key)
        where T : unmanaged
    {
        if (!key.IsValid)
            throw new ArgumentException("The property key is uninitialized.", nameof(key));
        if (!_propertiesByKey.TryGetValue(key, out RenderInstancePropertyDescriptor? descriptor))
            throw new KeyNotFoundException($"Property '{key}' is not part of this metadata contract.");
        return ResolveDescriptor<T>(descriptor, nameof(key));
    }

    public bool Contains(RenderInstancePropertyKey key) =>
        key.IsValid && _propertiesByKey.ContainsKey(key);

    internal RenderInstancePropertyDescriptor Require(
        RenderInstancePropertyKey key,
        string parameterName)
    {
        if (!key.IsValid)
            throw new ArgumentException("The property key is uninitialized.", parameterName);
        if (!_propertiesByKey.TryGetValue(key, out RenderInstancePropertyDescriptor? descriptor))
            throw new ArgumentException($"Property '{key}' is not part of this metadata contract.", parameterName);
        return descriptor;
    }

    internal RenderInstancePropertyDescriptor RequireCompatible(
        RenderInstancePropertyDescriptor property,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (!_propertiesByKey.TryGetValue(property.Key, out RenderInstancePropertyDescriptor? storageProperty)
            || storageProperty.Encoding != property.Encoding)
        {
            throw new ArgumentException(
                $"Property '{property.Key}' is not available with encoding " +
                $"'{property.Encoding.Codec}'.",
                parameterName);
        }
        return storageProperty;
    }

    public bool Equals(RenderInstancePropertyLayout? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null
            || _hashCode != other._hashCode
            || MetadataWordCount != other.MetadataWordCount
            || _properties.Count != other._properties.Count)
        {
            return false;
        }

        for (int ordinal = 0; ordinal < _properties.Count; ordinal++)
        {
            RenderInstancePropertyDescriptor left = _properties[ordinal];
            RenderInstancePropertyDescriptor right = other._properties[ordinal];
            if (left.Key != right.Key
                || left.Encoding != right.Encoding
                || left.MetadataWordOffset != right.MetadataWordOffset)
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) =>
        obj is RenderInstancePropertyLayout other && Equals(other);

    public override int GetHashCode() => _hashCode;

    internal bool HasSameContract(RenderInstancePropertyLayout other) => Equals(other);

    internal void Validate<T>(ResolvedRenderInstanceProperty<T> property, string parameterName)
        where T : unmanaged
    {
        if (!property.IsValid || !property.BelongsTo(this))
            throw new ArgumentException("The resolved property belongs to a different metadata layout.", parameterName);
        property.Encoding.ValidateManagedType<T>(parameterName);
    }

    private ResolvedRenderInstanceProperty<T> ResolveDescriptor<T>(
        RenderInstancePropertyDescriptor descriptor,
        string parameterName)
        where T : unmanaged
    {
        descriptor.Encoding.ValidateManagedType<T>(parameterName);
        return new ResolvedRenderInstanceProperty<T>(this, descriptor);
    }

    private static int ComputeHashCode(
        IReadOnlyList<RenderInstancePropertyDescriptor> properties,
        int metadataWordCount)
    {
        var hash = new HashCode();
        hash.Add(metadataWordCount);
        hash.Add(properties.Count);
        foreach (RenderInstancePropertyDescriptor property in properties)
        {
            hash.Add(property.Key);
            hash.Add(property.Encoding);
            hash.Add(property.MetadataWordOffset);
            hash.Add(property.Encoding.MetadataWordCount);
        }
        return hash.ToHashCode();
    }

    private sealed class RenderInstancePropertyRegistrationComparer :
        IComparer<RenderInstancePropertyRegistration>
    {
        internal static readonly RenderInstancePropertyRegistrationComparer Instance = new();

        public int Compare(
            RenderInstancePropertyRegistration? left,
            RenderInstancePropertyRegistration? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;
            return StringComparer.Ordinal.Compare(
                left.Declaration.Key.Value,
                right.Declaration.Key.Value);
        }
    }

}
