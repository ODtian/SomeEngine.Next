using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SomeEngine.Tools;

internal sealed record SampleLimit(string Pattern, double Target, bool HigherIsBetter);

internal sealed class SampleCheck(SampleLimit? limit, string? diagnosticScope)
{
    private readonly Regex? _pattern = limit is not null && diagnosticScope is null ? new(limit.Pattern) : null;
    private readonly StringBuilder[] _lines = [new(), new()];
    private readonly Lock _gate = new();
    public bool Failed { get; private set; }
    public double? Sample { get; private set; }
    public double? FirstSample { get; private set; }
    public double? Target => limit?.Target;

    public bool Observe(string output, bool error)
    {
        if (_pattern is null) return false;
        lock (_gate)
        {
            if (Failed) return false;
            StringBuilder line = _lines[error ? 1 : 0];
            foreach (char character in output)
            {
                if (character != '\n') { line.Append(character); continue; }
                MatchCollection matches = _pattern.Matches(line.ToString());
                line.Clear();
                foreach (Match match in matches)
                {
                    if (!double.TryParse(match.Groups["value"].Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)) continue;
                    FirstSample ??= value;
                    Sample = value;
                    Failed = limit!.HigherIsBetter ? value < limit.Target : value > limit.Target;
                    if (Failed) return true;
                }
            }
            return false;
        }
    }
}
