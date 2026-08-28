namespace SomeEngine.Render.Instances;

/// <summary>
/// Composes opaque property requirements. Equal keys deduplicate only when their complete
/// encodings agree; contributors, values, shader-local names, and storage addresses are not
/// part of compatibility.
/// </summary>
public sealed class RenderInstancePropertyLayoutBuilder
{
    private readonly Dictionary<RenderInstancePropertyKey, RenderInstancePropertyRegistration> _properties = [];
    private RenderInstancePropertyLayout? _layout;

    public int Count => _properties.Count;

    public bool IsFrozen => _layout is not null;

    public RenderInstanceProperty<T> Register<T>(
        string contributor,
        RenderInstancePropertyKey key,
        RenderInstancePropertyEncoding encoding)
        where T : unmanaged
    {
        EnsureMutable();
        ArgumentException.ThrowIfNullOrWhiteSpace(contributor);
        if (!key.IsValid)
            throw new ArgumentException("The property key is uninitialized.", nameof(key));
        encoding.ValidateManagedType<T>(nameof(encoding));

        var declaration = new RenderInstancePropertyDeclaration(key, encoding);
        RenderInstancePropertyRegistration registration = RegisterDeclaration(declaration, contributor);
        return new RenderInstanceProperty<T>(registration.Declaration);
    }

    public void Include(RenderInstancePropertyLayout layout)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(layout);
        foreach (RenderInstancePropertyDescriptor property in layout.Properties)
        {
            RenderInstancePropertyRegistration registration = RegisterDeclaration(
                property.CloneDeclaration(),
                property.Contributors[0]);
            for (int index = 1; index < property.Contributors.Count; index++)
                registration.Contributors.Add(property.Contributors[index]);
        }
    }

    internal void Include(RenderInstancePropertyDescriptor property)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(property);
        RenderInstancePropertyRegistration registration = RegisterDeclaration(
            property.CloneDeclaration(),
            property.Contributors[0]);
        for (int index = 1; index < property.Contributors.Count; index++)
            registration.Contributors.Add(property.Contributors[index]);
    }

    public RenderInstancePropertyLayout Freeze()
    {
        _layout ??= RenderInstancePropertyLayout.Create(_properties.Values);
        return _layout;
    }

    private RenderInstancePropertyRegistration RegisterDeclaration(
        RenderInstancePropertyDeclaration declaration,
        string contributor)
    {
        if (_properties.TryGetValue(declaration.Key, out RenderInstancePropertyRegistration? existing))
        {
            if (!existing.Declaration.HasSameContract(declaration))
            {
                throw new InvalidOperationException(
                    $"Render-instance property '{declaration.Key}' from '{contributor}' uses encoding " +
                    $"'{declaration.Encoding.Codec}', while '{string.Join("', '", existing.Contributors)}' " +
                    $"uses '{existing.Declaration.Encoding.Codec}'.");
            }
            existing.Contributors.Add(contributor);
            return existing;
        }

        var registration = new RenderInstancePropertyRegistration(declaration, contributor);
        _properties.Add(declaration.Key, registration);
        return registration;
    }

    private void EnsureMutable()
    {
        if (_layout is not null)
            throw new InvalidOperationException("The render-instance property layout is frozen.");
    }
}
