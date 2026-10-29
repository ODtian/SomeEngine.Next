using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SomeEngine.Tools;

internal sealed class CpuCores
{
    private readonly ulong[] processors = ReadCores();

    public int Count => processors.Length;
    public ulong ProcessorMask => processors.Aggregate(0UL, (mask, core) => mask | core);

    public ulong Select(ulong available, int count)
    {
        ulong selected = 0;
        foreach (ulong core in processors)
        {
            if ((available & core) != core) continue;
            selected |= core;
            if (--count == 0) break;
        }
        return selected;
    }

    public int CountCores(ulong mask) => processors.Count(core => (mask & core) != 0);

    private static ulong[] ReadCores()
    {
        if (!OperatingSystem.IsWindows())
            return Enumerable.Range(0, Environment.ProcessorCount).Select(index => 1UL << index).ToArray();

        using var process = Process.GetCurrentProcess();
        ulong allowed = (ulong)process.ProcessorAffinity;
        uint size = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref size);
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref size)) throw new Win32Exception();
            var cores = new List<ulong>();
            for (int offset = 0; offset < size; offset += Marshal.ReadInt32(buffer, offset + 4))
            {
                ulong mask = (ulong)Marshal.ReadInt64(buffer, offset + 32) & allowed;
                if (mask != 0) cores.Add(mask);
            }
            return cores.Take(Environment.ProcessorCount).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint size);
}
