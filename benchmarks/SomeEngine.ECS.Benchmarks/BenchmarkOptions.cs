using System.Globalization;

namespace SomeEngine.ECS.Benchmarks;

internal enum BenchmarkProfile
{
    Smoke,
    Standard,
}

internal sealed record BenchmarkOptions(
    BenchmarkProfile Profile,
    int[] EntityCounts,
    int WarmupSamples,
    int Samples,
    int QueryIterations,
    int StructuralIterations,
    string? OutputPath)
{
    internal static string HelpText => """
        SomeEngine ECS measurements
        --profile <smoke|standard>
        --entity-counts <count[,count...]> (k/m suffixes accepted)
        --warmup <count> --samples <count>
        --query-iterations <count> --structural-iterations <count>
        --output <path> --help
        """;

    internal string ProfileName => Profile.ToString().ToLowerInvariant();

    internal static bool TryParse(
        string[] args,
        out BenchmarkOptions? options,
        out string? error)
    {
        options = null;
        error = null;
        BenchmarkProfile profile = BenchmarkProfile.Smoke;
        int[]? entityCounts = null;
        int? warmupSamples = null;
        int? samples = null;
        int? queryIterations = null;
        int? structuralIterations = null;
        string? outputPath = null;
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument is "--help" or "-h")
                return false;

            string option = argument;
            string? inlineValue = null;
            int equalsIndex = argument.IndexOf('=');
            if (equalsIndex >= 0)
            {
                option = argument[..equalsIndex];
                inlineValue = argument[(equalsIndex + 1)..];
            }

            if (!TryReadValue(args, ref index, option, inlineValue, out string? value, out error))
                return false;

            switch (option)
            {
                case "--profile":
                    if (!TryParseProfile(value, out profile))
                    {
                        error = $"Unknown benchmark profile '{value}'.";
                        return false;
                    }
                    break;
                case "--entity-counts":
                    if (!TryParseEntityCounts(value, out entityCounts, out error))
                        return false;
                    break;
                case "--warmup":
                case "--warmup-samples":
                    if (!TryParseNonNegativeInt(value, option, out int parsedWarmup, out error))
                        return false;
                    warmupSamples = parsedWarmup;
                    break;
                case "--samples":
                    if (!TryParsePositiveInt(value, option, out int parsedSamples, out error))
                        return false;
                    samples = parsedSamples;
                    break;
                case "--query-iterations":
                    if (!TryParsePositiveInt(value, option, out int parsedQueryIterations, out error))
                        return false;
                    queryIterations = parsedQueryIterations;
                    break;
                case "--structural-iterations":
                    if (!TryParsePositiveInt(value, option, out int parsedStructuralIterations, out error))
                        return false;
                    structuralIterations = parsedStructuralIterations;
                    break;
                case "--output":
                    outputPath = ResolvePath(value);
                    break;
                default:
                    error = $"Unknown option '{option}'.";
                    return false;
            }
        }

        ProfileDefaults defaults = ProfileDefaults.For(profile);
        entityCounts ??= defaults.EntityCounts;
        warmupSamples ??= defaults.WarmupSamples;
        samples ??= defaults.Samples;
        queryIterations ??= defaults.QueryIterations;
        structuralIterations ??= defaults.StructuralIterations;

        options = new BenchmarkOptions(
            profile,
            entityCounts,
            warmupSamples.Value,
            samples.Value,
            queryIterations.Value,
            structuralIterations.Value,
            outputPath);
        return true;
    }

    private static bool TryReadValue(
        string[] args,
        ref int index,
        string option,
        string? inlineValue,
        out string value,
        out string? error)
    {
        error = null;
        if (inlineValue is not null)
        {
            value = inlineValue;
            if (value.Length != 0)
                return true;
            error = $"Option '{option}' requires a value.";
            return false;
        }

        if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = args[++index];
            return true;
        }

        value = string.Empty;
        error = $"Option '{option}' requires a value.";
        return false;
    }

    private static bool TryParseProfile(string value, out BenchmarkProfile profile)
    {
        if (value.Equals("smoke", StringComparison.OrdinalIgnoreCase))
        {
            profile = BenchmarkProfile.Smoke;
            return true;
        }
        if (value.Equals("standard", StringComparison.OrdinalIgnoreCase))
        {
            profile = BenchmarkProfile.Standard;
            return true;
        }

        profile = default;
        return false;
    }

    private static bool TryParseEntityCounts(
        string value,
        out int[]? entityCounts,
        out string? error)
    {
        var parsed = new List<int>();
        foreach (string token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseEntityCount(token, out int count))
            {
                entityCounts = null;
                error = $"Entity count '{token}' is not a positive whole number (k/m suffixes are allowed).";
                return false;
            }
            if (!parsed.Contains(count))
                parsed.Add(count);
        }

        if (parsed.Count == 0)
        {
            entityCounts = null;
            error = "--entity-counts must contain at least one positive count.";
            return false;
        }

        entityCounts = parsed.ToArray();
        error = null;
        return true;
    }

    private static bool TryParseEntityCount(string text, out int count)
    {
        string normalized = text.Replace("_", string.Empty, StringComparison.Ordinal);
        decimal multiplier = 1;
        if (normalized.EndsWith('k') || normalized.EndsWith('K'))
        {
            multiplier = 1_000;
            normalized = normalized[..^1];
        }
        else if (normalized.EndsWith('m') || normalized.EndsWith('M'))
        {
            multiplier = 1_000_000;
            normalized = normalized[..^1];
        }

        if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number) ||
            number <= 0)
        {
            count = 0;
            return false;
        }

        if (number > int.MaxValue / multiplier)
        {
            count = 0;
            return false;
        }

        decimal scaled = number * multiplier;
        if (scaled != decimal.Truncate(scaled))
        {
            count = 0;
            return false;
        }

        count = (int)scaled;
        return true;
    }

    private static bool TryParsePositiveInt(
        string value,
        string option,
        out int parsed,
        out string? error)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
        {
            error = null;
            return true;
        }
        error = $"Option '{option}' must be a positive integer.";
        return false;
    }

    private static bool TryParseNonNegativeInt(
        string value,
        string option,
        out int parsed,
        out string? error)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 0)
        {
            error = null;
            return true;
        }
        error = $"Option '{option}' must be a non-negative integer.";
        return false;
    }

    private static string ResolvePath(string value) => Path.GetFullPath(value);

    private sealed record ProfileDefaults(
        int[] EntityCounts,
        int WarmupSamples,
        int Samples,
        int QueryIterations,
        int StructuralIterations)
    {
        internal static ProfileDefaults For(BenchmarkProfile profile) => profile switch
        {
            BenchmarkProfile.Smoke => new([10_000], 1, 3, 8, 8),
            BenchmarkProfile.Standard => new([100_000, 500_000], 2, 5, 64, 32),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
    }
}
