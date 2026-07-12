namespace SomeEngine.Graphics;

internal static class Program
{
    public static int Main()
    {
        try
        {
            var scenarios = new Benchmarks();
            scenarios.CompilerCacheScenario();
            scenarios.RhiDescriptorResourceScenario();
            scenarios.LightweightTenThousandFrameSoak();
            Console.WriteLine("Graphics benchmark/soak artifact: harness/artifacts/graphics-benchmarks/graphics-rendergraph.v1.json");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
