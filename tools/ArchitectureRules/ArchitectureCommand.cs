namespace SomeEngine.ArchitectureRules;

public static class ArchitectureCommand
{
    public static int Main(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            Console.Error.WriteLine("Usage: SomeEngine.ArchitectureRules <repository> --base <revision> [--rules ARCH-30] [--paths path,...]");
            return 2;
        }
        string? revision = null;
        string[]? paths = null;
        for (int index = 1; index < arguments.Length; index += 2)
        {
            if (index + 1 == arguments.Length) return 2;
            switch (arguments[index])
            {
                case "--base": revision = arguments[index + 1]; break;
                case "--paths": paths = arguments[index + 1].Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                case "--rules" when arguments[index + 1] == "ARCH-30": break;
                default: return 2;
            }
        }
        if (revision is null) return 2;
        IReadOnlyList<TypeNameViolation> violations = TypeNameRules.Check(Path.GetFullPath(arguments[0]), revision, paths);
        foreach (TypeNameViolation violation in violations)
            Console.Error.WriteLine($"{violation.Path}({violation.Line},{violation.Column}): error ARCH-30: New type '{violation.Name}' uses '{violation.Word}'; name its data or behavior directly.");
        Console.WriteLine($"Architecture checks: {violations.Count} violation(s).");
        return violations.Count == 0 ? 0 : 1;
    }
}
