using System.Reflection;
using System.Runtime.CompilerServices;

namespace SomeEngine.Elements.Consumer.Tests;

public sealed class AssemblyBoundaryTests
{
    [Fact]
    public void ElementLayerUsesPublicAssemblyAccess()
    {
        Assembly[] assemblies = ReadEngineAssemblies();
        var publicAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SomeEngine.Elements",
            "SomeEngine.Views.Compose",
            "SomeEngine.Views.SwiftUI",
            "SomeEngine.Views.Solid",
            typeof(AssemblyBoundaryTests).Assembly.GetName().Name!,
        };
        bool changed;
        do
        {
            changed = false;
            foreach (Assembly assembly in assemblies)
            {
                string name = assembly.GetName().Name!;
                if (IsElementAssembly(name) || assembly.GetReferencedAssemblies()
                    .Any(reference => publicAssemblies.Contains(reference.Name!)))
                    changed |= publicAssemblies.Add(name);
            }
        } while (changed);

        var violations = new List<string>();
        foreach (Assembly assembly in assemblies)
        {
            foreach (var access in assembly.GetCustomAttributes<InternalsVisibleToAttribute>())
            {
                string target = new AssemblyName(access.AssemblyName).Name!;
                if (IsElementAssembly(target) || publicAssemblies.Contains(target))
                    violations.Add($"{assembly.GetName().Name} grants internal access to {target}.");
            }
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static bool IsElementAssembly(string name) =>
        name.Equals("SomeEngine.Elements", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("SomeEngine.Elements.", StringComparison.OrdinalIgnoreCase);

    private static Assembly[] ReadEngineAssemblies()
    {
        var assemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        var remaining = new Queue<Assembly>();
        remaining.Enqueue(typeof(AssemblyBoundaryTests).Assembly);
        remaining.Enqueue(Assembly.Load("SomeEngine.Animation"));
        remaining.Enqueue(Assembly.Load("SomeEngine.Typography"));
        remaining.Enqueue(Assembly.Load("SomeEngine.Views.Compose"));
        remaining.Enqueue(Assembly.Load("SomeEngine.Views.SwiftUI"));
        while (remaining.TryDequeue(out Assembly? assembly))
        {
            if (!assemblies.TryAdd(assembly.GetName().Name!, assembly))
                continue;
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
                if (reference.Name!.StartsWith("SomeEngine.", StringComparison.Ordinal))
                    remaining.Enqueue(Assembly.Load(reference));
        }
        return assemblies.Values.ToArray();
    }
}
