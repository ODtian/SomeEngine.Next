namespace SomeEngine.ArchitectureRules;

public static class ArchitectureCommand
{
    public static int Main(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            Console.Error.WriteLine("Usage: SomeEngine.ArchitectureRules <repository> [--rules ARCH-28,ARCH-29] [--paths path,...]");
            return 2;
        }

        string repository = Path.GetFullPath(arguments[0]);
        string[]? rules = null;
        string[]? paths = null;
        for (int index = 1; index < arguments.Length; index += 2)
        {
            if (index + 1 == arguments.Length)
                return 2;
            switch (arguments[index])
            {
                case "--rules": rules = arguments[index + 1].Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                case "--paths": paths = arguments[index + 1].Split(',', StringSplitOptions.RemoveEmptyEntries); break;
                default: return 2;
            }
        }

        IReadOnlyList<ArchitectureViolation> featureViolations = rules is null ||
            rules.Any(rule => int.TryParse(rule.AsSpan(5), out int number) && number >= 23)
            ? VsmTileRules.Check(repository, rules, paths) : [];
        IReadOnlyList<ArchitectureViolation> renderingViolations = rules is null ||
            rules.Any(rule => int.TryParse(rule.AsSpan(5), out int number) && number <= 22)
            ? RenderArchitectureRules.Check(repository, rules, paths) : [];
        var violations = featureViolations.Concat(renderingViolations).ToArray();
        foreach (ArchitectureViolation violation in violations)
            Console.Error.WriteLine($"{violation.Path}({violation.Line},{violation.Column}): error {violation.Rule}: {violation.Description}");
        Console.WriteLine($"Architecture checks: {violations.Length} violation(s).");
        return violations.Length == 0 ? 0 : 1;
    }
}
