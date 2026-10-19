namespace SomeEngine.ECS.Relations.Benchmarks;

public sealed record MeasurementCase(
    string Class, string Size, string Variant, int EntityCount, int CandidateCount,
    string Density, int DynamicPercent, int StaticPercent, int MaskPopulation,
    bool Mutual, int HairPerCharacter, string CharacterLayout, int WorkerCount, bool Parallel)
{
    public string Id => $"{Class}-{Size}-{Variant}";
    public int Seed => 231243;
    public int WarmupFrames => 16;
    public int Rounds => 31;
    public int FramesPerRound => 8;
    public int ColdPairs => 7;
    public int IterationsPerFrame => 1;
    public int FramesPerSample => 1;
    public int SideSwitchIntervalFrames => 1;
    public int PartitionCapacity => 16;
    public int CategoryCount => 8;
    public int HairPercent => 75;
    public int CharacterCount => (EntityCount * HairPercent / 100 + HairPerCharacter - 1) / HairPerCharacter;
    public bool HasSpace => Class is "bitmap-space" or "character-space" or "complete" or "updates" or "threads" or "consume";
    public bool ChangesEveryFrame => Class == "updates";
    public string PrimaryBoundary => Class switch
    {
        "updates" => "MaintenanceAndQuery",
        "threads" => "ScheduledPairOutput",
        "consume" => "PairsAndConsume",
        _ => "Query"
    };
}

public static class MeasurementCases
{
    public static MeasurementCase[] All { get; } = Build();

    private static MeasurementCase[] Build()
    {
        var cases = new List<MeasurementCase>();
        string[] sizes = ["small", "middle", "large"];
        int[] sets = [128, 512, 2048];
        int[] spaces = [512, 2048, 8192];
        int[] candidates = [4096, 32768, 262144];
        string[] densities = ["sparse", "uniform", "dense"];
        for (int size = 0; size < sizes.Length; size++)
        {
            MeasurementCase Input(string category, string variant, bool space = false) =>
                new(category, sizes[size], variant, space ? spaces[size] : sets[size], candidates[size],
                    "dense", 50, 30, 4, true, 16, "clustered", 0, false);

            foreach (int mask in new[] { 1, 4, 8 })
                foreach (bool mutual in new[] { false, true })
                    cases.Add(Input("finite", $"mask{mask}-{(mutual ? "mutual" : "oneway")}")
                        with { MaskPopulation = mask, Mutual = mutual });
            foreach ((int dynamic, int stationary) in new[] { (10, 60), (50, 30), (90, 5) })
                cases.Add(Input("default", $"dynamic{dynamic}") with { DynamicPercent = dynamic, StaticPercent = stationary });
            foreach (int hairs in new[] { 16, 64 })
                foreach (string layout in new[] { "clustered", "interleaved" })
                    cases.Add(Input("character", $"hair{hairs}-{layout}") with { HairPerCharacter = hairs, CharacterLayout = layout });
            foreach (string density in densities)
            {
                foreach (bool mutual in new[] { false, true })
                    cases.Add(Input("bitmap-space", $"{density}-{(mutual ? "mutual" : "oneway")}", true)
                        with { Density = density, Mutual = mutual, MaskPopulation = density == "uniform" ? 4 : 1 });
                cases.Add(Input("complete", density, true) with { Density = density });
                cases.Add(Input("updates", density, true) with { Density = density });
                cases.Add(Input("consume", density, true) with { Density = density });
                foreach (bool parallel in new[] { false, true })
                    cases.Add(Input("threads", $"{density}-{(parallel ? "parallel" : "inline")}", true)
                        with { Density = density, Parallel = parallel, WorkerCount = parallel ? 4 : 0 });
            }
            foreach (string layout in new[] { "clustered", "interleaved" })
                cases.Add(Input("character-space", layout, true) with { Density = "dense", CharacterLayout = layout });
        }
        return cases.ToArray();
    }
}
