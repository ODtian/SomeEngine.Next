using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Maintainability;

/// <summary>
/// Wiki link density gate. Every wiki note (excluding Home.md which is the
/// root MOC) must have at least one inbound or outbound link to another wiki
/// note. Orphan notes fail.
///
/// Rationale: a note nobody links to has no reusable value. Link density is
/// the structural signal of "沉淀价值", not format templates.
/// </summary>
public sealed class WikiLinkGateTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();
    private static readonly Regex WikilinkPattern = new(@"\[\[([^\]]+)\]", RegexOptions.Compiled);

    [Fact]
    public void NoWikiNoteIsAnOrphan()
    {
        if (!Directory.Exists(Config.WikiRoot)) return;

        var notes = Directory.GetFiles(Config.WikiRoot, "*.md", SearchOption.AllDirectories);
        var noteNames = notes
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToHashSet();

        var outLinks = new Dictionary<string, HashSet<string>>();
        var inLinks = new Dictionary<string, HashSet<string>>();
        foreach (var name in noteNames)
        {
            outLinks[name] = [];
            inLinks[name] = [];
        }

        foreach (var path in notes)
        {
            var source = Path.GetFileNameWithoutExtension(path);
            var content = File.ReadAllText(path);
            foreach (Match m in WikilinkPattern.Matches(content))
            {
                var target = m.Groups[1].Value.Trim();
                var pipe = target.IndexOf('|');
                if (pipe >= 0) target = target[..pipe].Trim();
                var hash = target.IndexOf('#');
                if (hash >= 0) target = target[..hash].Trim();

                if (noteNames.Contains(target))
                {
                    outLinks[source].Add(target);
                    inLinks[target].Add(source);
                }
            }
        }

        var orphans = noteNames
            .Where(n => n != "Home")
            .Where(n => outLinks[n].Count == 0 && inLinks[n].Count == 0)
            .ToList();

        Assert.True(orphans.Count == 0,
            $"Orphan wiki notes with no inbound or outbound links: {string.Join(", ", orphans)}. " +
            "Every note must connect to the knowledge graph.");
    }

    [Fact]
    public void HomeLinksToAllDomains()
    {
        var homePath = Path.Combine(Config.WikiRoot, "Home.md");
        Assert.True(File.Exists(homePath), "Home.md root MOC must exist at wiki/Home.md");

        var content = File.ReadAllText(homePath);
        var links = WikilinkPattern.Matches(content)
            .Select(m => m.Groups[1].Value.Trim())
            .ToList();

        Assert.True(links.Count >= 4,
            $"Home.md must link to all domain MOC notes. Found {links.Count} links, need >= 4.");
    }
}
