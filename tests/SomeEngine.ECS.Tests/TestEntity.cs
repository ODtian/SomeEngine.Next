using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Tests;

internal static class TestEntity
{
    public static Entity Create(int index, int generation = 0)
    {
        if (index <= 0)
            throw new ArgumentOutOfRangeException(nameof(index), "Test entities must use index >= 1.");

        if (generation < 0)
            throw new ArgumentOutOfRangeException(nameof(generation), "Generation cannot be negative.");

        return new Entity(index, generation);
    }
}
