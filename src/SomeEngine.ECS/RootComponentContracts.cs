namespace SomeEngine.ECS;

/// <summary>Canonical marker for archetype-table component values.</summary>
public interface IComponent { }

/// <summary>Canonical marker for table components with a per-chunk enable mask.</summary>
public interface IEnableableComponent : IComponent { }

/// <summary>Canonical marker for table components retained during entity cleanup.</summary>
public interface ICleanupComponent : IComponent { }
