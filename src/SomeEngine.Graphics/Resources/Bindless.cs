namespace SomeEngine.Graphics;

public readonly record struct BindlessTableDesc(BindingKind Kind, uint Capacity, string? Name = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
        if (Capacity == 0) throw new ArgumentOutOfRangeException(nameof(Capacity));
    }
}

public readonly record struct BindlessSlot(BindlessTableHandle Table, uint Index, uint Generation)
{
    public bool IsValid => Table.IsValid && Generation != 0;
}
