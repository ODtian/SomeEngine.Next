namespace SomeEngine.Serialization;

/// <summary>Explicit, source-generated contract catalog. It never scans assemblies.</summary>
public sealed class BinaryContractCatalog
{
    private readonly Dictionary<Guid, BinaryContractDescriptor> _byTypeId = [];
    private bool _frozen;

    public int Count => _byTypeId.Count;

    public IReadOnlyList<BinaryContractDescriptor> Descriptors => _byTypeId.Values
        .OrderBy(static descriptor => descriptor.TypeId)
        .ToArray();

    public BinaryContractCatalog Register<T>()
        where T : IBinaryContract<T>
    {
        if (_frozen)
            throw new InvalidOperationException("Binary contract catalog is frozen.");
        BinaryContractDescriptor descriptor = BinaryContract<T>.Descriptor;
        if (descriptor.TypeId == Guid.Empty)
            throw new InvalidOperationException($"Binary contract '{typeof(T).FullName}' has an empty type id.");
        if (_byTypeId.TryGetValue(descriptor.TypeId, out BinaryContractDescriptor existing))
        {
            if (existing.ContractType != descriptor.ContractType)
            {
                throw new InvalidOperationException(
                    $"Binary type id {descriptor.TypeId} is shared by '{existing.ContractType.FullName}' " +
                    $"and '{descriptor.ContractType.FullName}'.");
            }

            if (existing != descriptor)
                throw new InvalidOperationException($"Binary contract '{typeof(T).FullName}' was registered inconsistently.");
            return this;
        }

        _byTypeId.Add(descriptor.TypeId, descriptor);
        return this;
    }

    public bool TryGet(Guid typeId, out BinaryContractDescriptor descriptor)
        => _byTypeId.TryGetValue(typeId, out descriptor);

    public BinaryContractCatalog Freeze()
    {
        _frozen = true;
        return this;
    }
}
