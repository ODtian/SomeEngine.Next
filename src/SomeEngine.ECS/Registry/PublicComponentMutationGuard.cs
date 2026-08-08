namespace SomeEngine.ECS.Registry;

internal static class PublicComponentMutationGuard
{
    internal static void RelationshipRole<T>(string operation)
        where T : struct
    {
        if (!ComponentMetadata<T>.IsRelationshipSource &&
            !ComponentMetadata<T>.IsRelationshipTarget)
        {
            throw new InvalidOperationException(
                $"{operation} is reserved for relationship components, but {typeof(T).Name} " +
                $"implements neither IRelationshipSource nor IRelationshipTarget.");
        }
    }

    internal static void Structural<T>(string operation)
        where T : struct
    {
        if (!ComponentMetadata<T>.AllowsPublicStructuralMutation)
            Throw<T>(operation, structural: true);
    }

    internal static void Value<T>(string operation)
        where T : struct
    {
        if (!ComponentMetadata<T>.AllowsPublicValueMutation)
            Throw<T>(operation, structural: false);
    }

    internal static void Structural(ReadOnlySpan<int> componentIds, string operation)
    {
        for (int i = 0; i < componentIds.Length; i++)
        {
            ref readonly var info = ref ComponentRegistry.Get(componentIds[i]);
            if (!info.AllowsPublicStructuralMutation)
                Throw(in info, operation, structural: true);
        }
    }

    internal static void Value(ReadOnlySpan<int> componentIds, string operation)
    {
        for (int i = 0; i < componentIds.Length; i++)
        {
            ref readonly var info = ref ComponentRegistry.Get(componentIds[i]);
            if (!info.AllowsPublicValueMutation)
                Throw(in info, operation, structural: false);
        }
    }

    internal static void CopySurface(
        ReadOnlySpan<int> componentIds,
        bool includeTableComponents,
        bool includeCleanupComponents,
        string operation)
    {
        if (!includeTableComponents && !includeCleanupComponents)
            return;

        for (int i = 0; i < componentIds.Length; i++)
        {
            ref readonly var info = ref ComponentRegistry.Get(componentIds[i]);
            if (info.Storage != StoragePath.Table ||
                (info.IsCleanup ? !includeCleanupComponents : !includeTableComponents))
            {
                continue;
            }

            if (!info.AllowsPublicStructuralMutation || !info.AllowsPublicValueMutation)
                Throw(in info, operation, structural: true);
        }
    }

    private static void Throw<T>(string operation, bool structural)
        where T : struct
    {
        ref readonly var info = ref ComponentRegistry.Get(ComponentMetadata<T>.Id);
        Throw(in info, operation, structural);
    }

    private static void Throw(in ComponentInfo info, string operation, bool structural)
    {
        string role = info.IsRelationshipSource
            ? "relationship source"
            : "relationship target";
        string guidance = info.IsRelationshipSource
            ? structural
                ? "Use the typed relationship API."
                : "Use the typed relationship API or an owner-bound query writer."
            : "Use the typed relationship API; relationship targets are maintained by the ECS.";

        throw new InvalidOperationException(
            $"{operation} cannot mutate {role} component {info.Type.Name}. {guidance}");
    }
}
