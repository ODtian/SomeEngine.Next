using System.Globalization;

namespace SomeEngine.ECS.Benchmarks;

internal enum BenchmarkProfile
{
    Smoke,
    Standard,
    Certification,
}

internal sealed record BenchmarkOptions(
    BenchmarkProfile Profile,
    int[] EntityCounts,
    int WarmupSamples,
    int Samples,
    int QueryIterations,
    int StructuralIterations,
    string? OutputPath,
    string? BaselinePath,
    string? AbsoluteBudgetsPath,
    string? EvidenceManifestPath,
    double MaximumP50RegressionPercent,
    double MaximumP99RegressionPercent)
{
    internal const double DefaultMaximumP50RegressionPercent = 5.0;
    internal const double DefaultMaximumP99RegressionPercent = 10.0;
    internal const int CertificationWarmupSamples = 3;
    internal const int CertificationSamples = 100;
    internal const int CertificationQueryIterations = 128;
    internal const int CertificationStructuralIterations = 64;

    internal static readonly int[] CertificationEntityCounts =
    [
        100_000,
        500_000,
        1_000_000,
    ];

    internal static string HelpText =>
        """
        SomeEngine ECS benchmark and certification runner

        Usage:
          dotnet run --project benchmarks/SomeEngine.ECS.Benchmarks -c Release -- [options]

        Options:
          --profile <smoke|standard|certification>
              smoke (default): 10k entities, 1 warm-up, 3 fresh samples.
              standard: 100k and 500k entities, 2 warm-ups, 5 fresh samples.
              certification: fixed 100k, 500k, and 1m entities, 3 warm-ups,
              100 fresh samples, 128 query passes, and 64 structural
              publications. Certification requires --baseline,
              --absolute-budgets, and --evidence-manifest.
          --entity-counts <count[,count...]>
              Override smoke/standard profile counts. Suffixes k and m are
              accepted (for example 100k,500k,1m).
          --warmup <count>              Override smoke/standard warm-up count.
          --samples <count>             Override smoke/standard measured count.
          --query-iterations <count>    Override smoke/standard query passes.
          --structural-iterations <n>   Override smoke/standard publications.
          --output <path>               Also write the JSON report to this file.
          --baseline <report.json>      Compare p50/p99 with a prior runner report.
          --absolute-budgets <file>     Apply absolute per-scenario budgets.
          --evidence-manifest <file>    Bind certification to reviewed external evidence.
          --max-p50-regression-percent <percent>  Default: 5.
          --max-p99-regression-percent <percent>  Default: 10.
          --help                        Show this help.

        The default smoke invocation is intentionally short and does not need a
        baseline or budget file. See README.md next to the project for the absolute
        budget JSON schema and certification examples.
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
        string? baselinePath = null;
        string? absoluteBudgetsPath = null;
        string? evidenceManifestPath = null;
        double maximumP50RegressionPercent = DefaultMaximumP50RegressionPercent;
        double maximumP99RegressionPercent = DefaultMaximumP99RegressionPercent;

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
                case "--baseline":
                    baselinePath = ResolvePath(value);
                    break;
                case "--absolute-budgets":
                case "--budgets":
                    absoluteBudgetsPath = ResolvePath(value);
                    break;
                case "--evidence-manifest":
                    evidenceManifestPath = ResolvePath(value);
                    break;
                case "--max-p50-regression-percent":
                    if (!TryParseNonNegativeDouble(
                            value,
                            option,
                            out maximumP50RegressionPercent,
                            out error))
                    {
                        return false;
                    }
                    break;
                case "--max-p99-regression-percent":
                    if (!TryParseNonNegativeDouble(
                            value,
                            option,
                            out maximumP99RegressionPercent,
                            out error))
                    {
                        return false;
                    }
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

        if (profile == BenchmarkProfile.Certification)
        {
            if (baselinePath is null ||
                absoluteBudgetsPath is null ||
                evidenceManifestPath is null)
            {
                error =
                    "The certification profile requires --baseline, --absolute-budgets, " +
                    "and --evidence-manifest.";
                return false;
            }

#if DEBUG
            error = "The certification profile must be built and run in Release configuration.";
            return false;
#else
            if (!entityCounts.SequenceEqual(CertificationEntityCounts) ||
                warmupSamples != CertificationWarmupSamples ||
                samples != CertificationSamples ||
                queryIterations != CertificationQueryIterations ||
                structuralIterations != CertificationStructuralIterations)
            {
                error =
                    "The certification workload is fixed: --entity-counts 100k,500k,1m, " +
                    "--warmup 3, --samples 100, --query-iterations 128, and " +
                    "--structural-iterations 64. Use smoke or standard for custom workloads.";
                return false;
            }
            if (maximumP50RegressionPercent > DefaultMaximumP50RegressionPercent ||
                maximumP99RegressionPercent > DefaultMaximumP99RegressionPercent)
            {
                error =
                    "Certification regression limits may be tightened but not relaxed beyond " +
                    $"p50={DefaultMaximumP50RegressionPercent}% and " +
                    $"p99={DefaultMaximumP99RegressionPercent}%.";
                return false;
            }
            if (outputPath is not null &&
                (PathsEqual(outputPath, baselinePath) ||
                 PathsEqual(outputPath, absoluteBudgetsPath) ||
                 PathsEqual(outputPath, evidenceManifestPath)))
            {
                error =
                    "Certification --output must not overwrite the approved baseline, budget, " +
                    "or evidence manifest file.";
                return false;
            }
#endif
        }

        options = new BenchmarkOptions(
            profile,
            entityCounts,
            warmupSamples.Value,
            samples.Value,
            queryIterations.Value,
            structuralIterations.Value,
            outputPath,
            baselinePath,
            absoluteBudgetsPath,
            evidenceManifestPath,
            maximumP50RegressionPercent,
            maximumP99RegressionPercent);
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
        if (value.Equals("certification", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("cert", StringComparison.OrdinalIgnoreCase))
        {
            profile = BenchmarkProfile.Certification;
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

    private static bool TryParseNonNegativeDouble(
        string value,
        string option,
        out double parsed,
        out string? error)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) &&
            double.IsFinite(parsed) &&
            parsed >= 0)
        {
            error = null;
            return true;
        }
        error = $"Option '{option}' must be a finite non-negative number.";
        return false;
    }

    private static string ResolvePath(string value) => Path.GetFullPath(value);

    private static bool PathsEqual(string left, string? right) =>
        right is not null && string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

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
            BenchmarkProfile.Certification => new(
                CertificationEntityCounts,
                CertificationWarmupSamples,
                CertificationSamples,
                CertificationQueryIterations,
                CertificationStructuralIterations),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
    }
}
