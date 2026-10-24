using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using SomeEngine.Serialization;

internal static class ViewMeasurements
{
    internal static void Run()
    {
        foreach (int count in new[] { 0, 1024, 262144 })
        {
            var output = new ArrayBufferWriter<byte>();
            BinarySerializer.Write(output, new ViewMeasurementValue
            {
                Prefix = new int[count], Text = "field after collection", Value = 42,
            });
            using var memory = BinaryContractViewOwner.Borrow(output.WrittenMemory);
            for (int i = 0; i < 5; i++) Read(new ViewMeasurementValue.View(memory), 1000);
            double[] creation = new double[21];
            double[] access = new double[21];
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            int checksum = 0;
            for (int i = 0; i < creation.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                var view = new ViewMeasurementValue.View(memory);
                creation[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                start = Stopwatch.GetTimestamp();
                checksum += Read(view, 100000);
                access[i] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / 100000;
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Array.Sort(creation);
            Array.Sort(access);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"serialization-view: prefix={count} bytes={output.WrittenCount} build-p50={creation[10]:F2}us build-p95={creation[19]:F2}us read-p50={access[10]:F2}ns read-p95={access[19]:F2}ns allocated={allocated} checksum={checksum}"));
        }
    }

    private static int Read(ViewMeasurementValue.View view, int count)
    {
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            total += view.GetValue();
            view.TryGetTextUtf8(out var text);
            total += text.Length;
        }
        return total;
    }
}

[BinarySerializable]
public partial class ViewMeasurementValue
{
    public int[] Prefix { get; set; } = [];
    public string Text { get; set; } = "";
    public int Value { get; set; }
}
