using System;
using System.Diagnostics;
using System.IO;

namespace SomeEngine.Harness.Core;

/// <summary>
/// Resolves the repository root and runs git commands, using HarnessConfig
/// anchor detection. No hardcoded paths.
/// </summary>
public static class Repo
{
    public static string Root => HarnessConfig.ResolveRepoRoot();

    public static string Git(string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        try
        {
            var p = Process.Start(psi);
            if (p is null) return "";
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return stdout.Trim();
        }
        catch
        {
            return "";
        }
    }
}
