namespace SomeEngine.Physics;

public interface IContactMaterialCombine
{
    ContactMaterial Combine(in ContactMaterial first, in ContactMaterial second);
}

public readonly struct AverageContactMaterials : IContactMaterialCombine
{
    public ContactMaterial Combine(in ContactMaterial first, in ContactMaterial second) => new()
    {
        NormalStiffness = (first.NormalStiffness + second.NormalStiffness) * 0.5f,
        NormalDamping = (first.NormalDamping + second.NormalDamping) * 0.5f,
        StaticFriction = (first.StaticFriction + second.StaticFriction) * 0.5f,
        DynamicFriction = (first.DynamicFriction + second.DynamicFriction) * 0.5f,
        Restitution = (first.Restitution + second.Restitution) * 0.5f,
        RestitutionThreshold = (first.RestitutionThreshold + second.RestitutionThreshold) * 0.5f,
    };
}
