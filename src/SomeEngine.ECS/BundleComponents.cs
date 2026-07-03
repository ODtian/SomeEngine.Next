namespace SomeEngine.ECS;

internal static class BundleComponents
{
    internal static void SortAndValidate(Span<int> componentIds)
    {
        if (componentIds.Length == 0)
            return;

        Sort(componentIds);
        for (int i = 1; i < componentIds.Length; i++)
        {
            if (componentIds[i - 1] == componentIds[i])
                throw new InvalidOperationException("Duplicate component types are not allowed in bundle operations.");
        }
    }

    internal static void Sort(Span<int> componentIds)
    {
        switch (componentIds.Length)
        {
            case 0:
            case 1:
                return;
            case 2:
                if (componentIds[0] > componentIds[1])
                    (componentIds[0], componentIds[1]) = (componentIds[1], componentIds[0]);
                return;
            case 3:
                if (componentIds[0] > componentIds[1])
                    (componentIds[0], componentIds[1]) = (componentIds[1], componentIds[0]);
                if (componentIds[1] > componentIds[2])
                    (componentIds[1], componentIds[2]) = (componentIds[2], componentIds[1]);
                if (componentIds[0] > componentIds[1])
                    (componentIds[0], componentIds[1]) = (componentIds[1], componentIds[0]);
                return;
            default:
                componentIds.Sort();
                return;
        }
    }
}

