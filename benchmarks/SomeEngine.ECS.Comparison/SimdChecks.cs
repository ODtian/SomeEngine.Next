using System.Reflection;
using System.Runtime.Intrinsics;

namespace SomeEngine.ECS.Comparison;

// Correctness-only probes. The oracles below do not call Count, Contains,
// Stamp or Select to calculate expected answers, and expose no production API.
internal static class SimdChecks
{
    private static readonly int[] SharedLengths =
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 127, 128, 129];
    private static readonly ulong[] Thresholds =
        [0, 1, 2, 3, (1UL << 63) - 1, 1UL << 63, ulong.MaxValue - 1, ulong.MaxValue];
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type ChangesType = typeof(World).Assembly.GetType("SomeEngine.ECS.Storage.ComponentChanges", true)!;
    private static readonly Type SummaryType = typeof(World).Assembly.GetType("SomeEngine.ECS.Storage.ChangeSummary", true)!;
    private static readonly Type RowVersionsType = typeof(World).Assembly.GetType("SomeEngine.ECS.Storage.RowVersions", true)!;
    private static readonly ConstructorInfo ChangesConstructor =
        ChangesType.GetConstructor(Fields, null, [typeof(int), typeof(bool)], null)!;
    private static readonly MethodInfo SelectMethod = ChangesType.GetMethod("Select", Fields)!;
    private static readonly MethodInfo RangeMethod = ChangesType.GetMethod("StampRange", Fields)!;
    private static readonly MethodInfo MergeMethod = ChangesType.GetMethod("MergeWords", Fields)!;
    private static readonly FieldInfo RowsField = ChangesType.GetField("Rows", Fields)!;
    private static readonly FieldInfo WordsField = ChangesType.GetField("Words", Fields)!;
    private static readonly FieldInfo BlocksField = ChangesType.GetField("Blocks", Fields)!;
    private static readonly FieldInfo LastWriteField = ChangesType.GetField("LastWrite", Fields)!;
    private static readonly FieldInfo AddedField = RowVersionsType.GetField("Added", Fields)!;
    private static readonly FieldInfo WrittenField = RowVersionsType.GetField("Written", Fields)!;
    private static readonly FieldInfo LatestField = SummaryType.GetField("Latest", Fields)!;
    private static readonly FieldInfo OtherLatestField = SummaryType.GetField("OtherLatest", Fields)!;
    private static readonly FieldInfo MembersField = SummaryType.GetField("Members", Fields)!;

    internal static void Run(string[] args)
    {
        if (args.Length != 2 || (args[1] != "vector256" && args[1] != "scalar"))
            throw new ArgumentException("Use check-simd vector256, or check-simd scalar in a fresh process with hardware intrinsics disabled.");
        bool vector256 = Vector256.IsHardwareAccelerated;
        Require(vector256 == (args[1] == "vector256"),
            $"requested {args[1]} path, but Vector256.IsHardwareAccelerated={vector256}; no fallback pass is claimed");
        int sharedCases = CheckSharedMasks();
        (int selectCases, int fullWordScanCases) = CheckSelectMasks();
        int fillCases = CheckRangeFill();
        Require(fullWordScanCases != 0, "the fixture must exercise full-word non-Added Select scans, not only summary shortcuts");
        Console.WriteLine($"SIMD correctness only: mode={args[1]}, Vector256.IsHardwareAccelerated={vector256}, "
            + $"shared enumeration cases={sharedCases}, Select masks={selectCases}, full-word scan inputs={fullWordScanCases}, "
            + $"range-fill cases={fillCases}; independent scalar oracles passed. No timing or resource acceptance is claimed.");
    }

    private static int CheckSharedMasks()
    {
        int cases = 0;
        foreach (int count in SharedLengths)
        foreach (bool tailMatches in new[] { false, true })
        {
            using var world = new World();
            var primary = world.InternShared(new SharedPrimary { Value = 7 });
            var secondary = world.InternShared(new SharedSecondary { Value = 11 });
            var expectedSingle = new bool[count];
            var expectedCombined = new bool[count];
            for (int row = 0; row < count; row++)
            {
                bool last = row == count - 1;
                bool firstMatches = last ? tailMatches : row % 3 == 0;
                bool secondMatches = last || row % 5 != 0;
                bool enabled = last || row % 7 != 0;
                var entity = world.Create(new Ordinal { Value = row });
                world.SetShared(entity, new SharedPrimary { Value = firstMatches ? 7 : 8 });
                world.SetShared(entity, new SharedSecondary { Value = secondMatches ? 11 : 12 });
                if (!enabled) world.Disable<SharedPrimary>(entity);
                expectedSingle[row] = firstMatches && enabled;
                expectedCombined[row] = firstMatches && secondMatches && enabled;
            }
            // Open/foreach goes through QueryRowFilter.Next -> Mask for nonempty
            // shared ranges. Never use Query.Count's single-shared index shortcut.
            CheckEnumeration(world.Query(new QueryDescription().With<Ordinal>().WithShared(primary)),
                expectedSingle, $"single shared, count={count}, tail={tailMatches}");
            CheckEnumeration(world.Query(new QueryDescription().With<Ordinal>().WithShared(primary).WithShared(secondary)),
                expectedCombined, $"combined shared, count={count}, tail={tailMatches}");
            cases += 2;
        }
        return cases;
    }

    private static void CheckEnumeration(Query query, bool[] expected, string context)
    {
        var seen = new bool[expected.Length];
        using var scope = query.Open();
        foreach (var rows in scope)
        {
            ReadOnlySpan<Ordinal> values = rows.Read<Ordinal>();
            for (int i = 0; i < values.Length; i++)
            {
                int row = values[i].Value;
                Require((uint)row < (uint)expected.Length, $"{context}: returned ordinal is in bounds");
                Require(expected[row] && !seen[row], $"{context}: selected only expected rows, once");
                seen[row] = true;
            }
        }
        for (int row = 0; row < expected.Length; row++)
            Require(seen[row] == expected[row], $"{context}: membership differs at ordinal {row}");
    }

    private static (int Cases, int FullWordScanCases) CheckSelectMasks()
    {
        int cases = 0, fullWordScanCases = 0;
        // The extra length crosses a summary-block boundary; ordinary cases
        // cover every vector remainder and the 63/64/65 and 127/128/129 edges.
        int[] lengths = [.. SharedLengths, 4_097];
        ulong[] pattern = [0, 1, 3, (1UL << 63) - 1, 1UL << 63, ulong.MaxValue - 1, ulong.MaxValue];
        foreach (int count in lengths)
        foreach (bool tracksAdded in new[] { false, true })
        foreach (bool mixed in new[] { false, true })
        {
            var written = new ulong[count];
            var added = new ulong[count];
            for (int row = 0; row < count; row++)
            {
                written[row] = mixed ? pattern[row % pattern.Length] : 3;
                added[row] = (row & 1) == 0 ? written[row] : written[row] == 0 ? 0 : written[row] - 1;
            }
            object changes = CreateChanges(written, added, tracksAdded);
            cases += CheckAllMasks(changes, written, added, tracksAdded,
                $"raw Select, count={count}, Added={tracksAdded}, mixed={mixed}");
            if (!tracksAdded && mixed)
            {
                for (int start = 0; start + 64 <= count; start += 64)
                {
                    var summary = Summarize(written.AsSpan(start, 64));
                    foreach (ulong since in Thresholds)
                        if (summary.OtherLatest > since) fullWordScanCases++;
                }
            }
        }
        return (cases, fullWordScanCases);
    }

    private static int CheckRangeFill()
    {
        const int capacity = 129;
        (int Start, int Count)[] ranges =
            [(0, 0), (0, 1), (1, 2), (0, 3), (1, 4), (3, 5), (0, 8), (7, 9), (60, 7), (63, 2), (64, 64), (128, 1)];
        const ulong version = (1UL << 63) + 5;
        int cases = 0;
        foreach (bool tracksAdded in new[] { false, true })
        foreach (bool publish in new[] { false, true })
        foreach (var range in ranges)
        {
            var written = new ulong[capacity];
            var added = new ulong[capacity];
            for (int row = 0; row < capacity; row++) { written[row] = (ulong)(1 + row % 3); added[row] = 1; }
            object changes = CreateChanges(written, added, tracksAdded);
            RangeMethod.Invoke(changes, [range.Start, range.Count, version, tracksAdded, publish]);
            for (int row = range.Start; row < range.Start + range.Count; row++) written[row] = version;
            CheckRawRows(changes, written, added, tracksAdded);
            if (!publish && range.Count != 0)
            {
                for (int word = 0; word < (capacity + 63) / 64; word++)
                    Require(Select(changes, word, capacity, 3, false, tracksAdded) == 0,
                        "unjoined range writes must not publish through LastWrite/Blocks");
                MergeMethod.Invoke(changes,
                    [range.Start >> 6, (range.Start + range.Count + 63) >> 6, version]);
            }
            CheckAllMasks(changes, written, added, tracksAdded,
                $"range fill, start={range.Start}, count={range.Count}, Added={tracksAdded}, publish={publish}");
            cases++;
        }
        return cases;
    }

    private static int CheckAllMasks(object changes, ulong[] written, ulong[] added, bool tracksAdded, string context)
    {
        int cases = 0;
        for (int word = 0; word < (written.Length + 63) / 64; word++)
        foreach (ulong since in Thresholds)
        foreach (bool selectAdded in new[] { false, true })
        {
            ulong expected = 0;
            int start = word << 6, end = Math.Min(start + 64, written.Length);
            // Independent scalar oracle: every live row and every output bit.
            // Production summary/filter methods are not used here.
            for (int row = start; row < end; row++)
            {
                ulong value = tracksAdded && selectAdded ? added[row] : written[row];
                if (value > since) expected |= 1UL << (row - start);
            }
            ulong actual = Select(changes, word, written.Length, since, selectAdded, tracksAdded);
            Require(actual == expected,
                $"{context}: word={word}, since={since}, selectAdded={selectAdded}, expected={expected:X16}, actual={actual:X16}");
            cases++;
        }
        return cases;
    }

    private static ulong Select(object changes, int word, int count, ulong since, bool added, bool tracksAdded)
        => (ulong)SelectMethod.Invoke(changes, [word, count, since, added, tracksAdded])!;

    private static object CreateChanges(ulong[] written, ulong[] added, bool tracksAdded)
    {
        object changes = ChangesConstructor.Invoke([written.Length, tracksAdded]);
        var rows = (Array)RowsField.GetValue(changes)!;
        for (int row = 0; row < written.Length; row++)
        {
            if (!tracksAdded) { rows.SetValue(written[row], row); continue; }
            object value = Activator.CreateInstance(RowVersionsType)!;
            AddedField.SetValue(value, added[row]);
            WrittenField.SetValue(value, written[row]);
            rows.SetValue(value, row);
        }
        var words = (Array)WordsField.GetValue(changes)!;
        var wordLatest = new ulong[words.Length];
        for (int word = 0; word < words.Length; word++)
        {
            int start = word << 6;
            var summary = Summarize(written.AsSpan(start, Math.Min(64, written.Length - start)));
            SetSummary(words, word, summary);
            wordLatest[word] = summary.Latest;
        }
        var blocks = (Array)BlocksField.GetValue(changes)!;
        ulong lastWrite = 0;
        for (int block = 0; block < blocks.Length; block++)
        {
            int start = block << 6;
            var summary = Summarize(wordLatest.AsSpan(start, Math.Min(64, wordLatest.Length - start)));
            SetSummary(blocks, block, summary);
            if (summary.Latest > lastWrite) lastWrite = summary.Latest;
        }
        LastWriteField.SetValue(changes, lastWrite);
        return changes;
    }

    private static (ulong Latest, ulong OtherLatest, ulong Members) Summarize(ReadOnlySpan<ulong> values)
    {
        ulong latest = 0, otherLatest = 0, members = 0;
        for (int i = 0; i < values.Length; i++)
        {
            ulong value = values[i];
            if (value > latest) { otherLatest = latest; latest = value; members = 1UL << i; }
            else if (value == latest) members |= 1UL << i;
            else if (value > otherLatest) otherLatest = value;
        }
        return (latest, otherLatest, members);
    }

    private static void SetSummary(Array target, int index, (ulong Latest, ulong OtherLatest, ulong Members) summary)
    {
        object value = Activator.CreateInstance(SummaryType)!;
        LatestField.SetValue(value, summary.Latest);
        OtherLatestField.SetValue(value, summary.OtherLatest);
        MembersField.SetValue(value, summary.Members);
        target.SetValue(value, index);
    }

    private static void CheckRawRows(object changes, ulong[] written, ulong[] added, bool tracksAdded)
    {
        var rows = (Array)RowsField.GetValue(changes)!;
        for (int row = 0; row < written.Length; row++)
        {
            object value = rows.GetValue(row)!;
            ulong actualWritten = tracksAdded ? (ulong)WrittenField.GetValue(value)! : (ulong)value;
            Require(actualWritten == written[row], $"range fill changed the wrong Written value at row {row}");
            if (tracksAdded)
                Require((ulong)AddedField.GetValue(value)! == added[row], $"range fill overwrote Added at row {row}");
        }
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }

    private struct Ordinal { internal int Value; }
    private struct SharedPrimary : ISharedComponent, IEnableableComponent { internal int Value; }
    private struct SharedSecondary : ISharedComponent { internal int Value; }
}
