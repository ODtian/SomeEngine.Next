namespace SomeEngine.ECS.Serialization;

internal readonly record struct EntitySlotSnapshot(int Index, int Generation, bool IsAlive);
