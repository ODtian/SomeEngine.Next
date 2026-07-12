using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class GraphicsCapabilityContinuityTests
{
    private const string ManifestRelativePath = "harness/capabilities/graphics-rendergraph-capabilities.v1.json";
    private const string SchemaRelativePath = "harness/capabilities/graphics-rendergraph-capabilities.schema.json";
    private const string InventoryRelativePath = "harness/capabilities/graphics-rendergraph-public-api-inventory.v1.json";
    private const string InventorySchemaRelativePath = "harness/capabilities/graphics-rendergraph-public-api-inventory.schema.json";

    private static readonly string[] ExpectedLevelOrder =
    [
        "absent",
        "metadata",
        "public-contract",
        "compiler-lowering",
        "null-execution",
        "native-call",
        "native-execution",
        "renderer-consumer",
    ];

    private static readonly HashSet<string> ValidAreas =
        new(["public", "null", "d3d12", "renderGraph", "tests"], StringComparer.Ordinal);

    private static readonly string[] RequiredAdvancedCapabilityIds =
    [
        "advanced.dxr",
        "advanced.mesh-shader",
        "advanced.variable-rate-shading",
        "advanced.sparse-tiled-resources",
        "advanced.sampler-feedback",
        "advanced.work-graphs",
    ];

    private static readonly HashSet<string> ValidInventoryDispositions =
        new(
        [
            "restored",
            "renamed",
            "accepted-replacement",
            "recorded-gap",
            "advanced-record-only",
        ],
        StringComparer.Ordinal);

    [Fact]
    public void Manifest_identity_levels_and_rows_are_structurally_complete()
    {
        CapabilityManifest manifest = LoadManifest();
        string root = HarnessConfig.ResolveRepoRoot();
        string schemaPath = Path.Combine(root, SchemaRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string inventorySchemaPath = Path.Combine(root, InventorySchemaRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var failures = new List<string>();

        Check(File.Exists(schemaPath), failures, $"Capability schema must exist at {SchemaRelativePath}");
        if (File.Exists(schemaPath))
        {
            using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
            Check(
                schema.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 1,
                failures,
                "Capability schema must pin schemaVersion 1");
        }
        Check(File.Exists(inventorySchemaPath), failures, $"Public API inventory schema must exist at {InventorySchemaRelativePath}");
        if (File.Exists(inventorySchemaPath))
        {
            using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(inventorySchemaPath));
            Check(
                schema.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32() == 1,
                failures,
                "Public API inventory schema must pin schemaVersion 1");
        }

        Check(manifest.Schema == "graphics-rendergraph-capabilities.schema.json", failures, "Manifest must use the checked-in local schema");
        Check(manifest.SchemaVersion == 1, failures, "Manifest schemaVersion must be 1");
        Check(manifest.ManifestId == "someengine.graphics-rendergraph.capability-continuity", failures, "Manifest id changed unexpectedly");
        Check(manifest.AcceptedRun == "0004", failures, "Capability continuity must remain attached to accepted run 0004");
        Check(manifest.TargetRepository == "F:/SomeEngine.Next", failures, "Capability continuity target must be SomeEngine.Next, not the legacy repository");
        Check(manifest.CheckpointCommit == "c0ac382e", failures, "Manifest must retain the committed pre-migration checkpoint identity");
        Check(manifest.LevelOrder.SequenceEqual(ExpectedLevelOrder), failures, "Capability level order changed; this requires an explicit schema revision and review");
        Check(Regex.IsMatch(manifest.BaselineArtifact.Sha256, "^[0-9A-F]{64}$"), failures, "Baseline artifact SHA-256 must be a 64-digit uppercase hex value");
        Check(!manifest.BaselineArtifact.RequiredInCleanCheckout, failures, "The untracked forensic ZIP must not become a clean-checkout dependency");
        Check(manifest.BaselinePublicApiInventory.Path == InventoryRelativePath, failures, "Manifest must pin the checked-in original public API inventory");
        Check(manifest.BaselinePublicApiInventory.SchemaPath == InventorySchemaRelativePath, failures, "Manifest must pin the checked-in public API inventory schema");
        Check(manifest.BaselinePublicApiInventory.SchemaVersion == 1, failures, "Public API inventory descriptor must pin schemaVersion 1");
        Check(
            manifest.BaselinePublicApiInventory.Groups.Any(static group => group.Id == "graphics-device-methods" && group.ExpectedCount == 130),
            failures,
            "Manifest must pin all 130 original IDevice method declarations");
        Check(
            manifest.BaselinePublicApiInventory.Groups.Any(static group => group.Id == "rendergraph-public-type-declarations" && group.ExpectedCount == 100),
            failures,
            "Manifest must pin all 100 original RenderGraph.Core public type declarations");

        foreach (IGrouping<string, CapabilityLane> duplicate in manifest.Lanes.GroupBy(static lane => lane.Id, StringComparer.Ordinal).Where(static group => group.Count() > 1))
            failures.Add($"Duplicate capability lane id {duplicate.Key}");

        foreach (IGrouping<string, CapabilityRow> duplicate in manifest.Capabilities.GroupBy(static capability => capability.Id, StringComparer.Ordinal).Where(static group => group.Count() > 1))
            failures.Add($"Duplicate capability id {duplicate.Key}");

        var laneIds = manifest.Lanes.Select(static lane => lane.Id).ToHashSet(StringComparer.Ordinal);
        foreach (CapabilityLane lane in manifest.Lanes)
        {
            Check(Regex.IsMatch(lane.Id, "^[a-z0-9]+(?:-[a-z0-9]+)*$"), failures, $"Lane id {lane.Id} is not stable kebab-case");
            Check(!string.IsNullOrWhiteSpace(lane.TestProject), failures, $"Lane {lane.Id} must name its test project");
            if (lane.Required)
                Check(!lane.AllowSilentSkip, failures, $"Required lane {lane.Id} cannot allow silent skip");
        }

        foreach (CapabilityRow capability in manifest.Capabilities)
        {
            string label = capability.Id;
            Check(Regex.IsMatch(label, "^[a-z0-9]+(?:[.-][a-z0-9]+)*$"), failures, $"Capability id {label} is not stable lowercase dotted/kebab form");
            Check(capability.Aliases.Length > 0, failures, $"{label} must retain at least one checkpoint/current terminology alias");
            Check(capability.Aliases.All(static alias => !string.IsNullOrWhiteSpace(alias)), failures, $"{label} contains an empty alias");
            Check(capability.Aliases.Distinct(StringComparer.Ordinal).Count() == capability.Aliases.Length, failures, $"{label} repeats an alias");
            Check(!string.IsNullOrWhiteSpace(capability.Family), failures, $"{label} must describe its family");
            Check(LevelIndex(manifest, capability.BaselineLevel) >= 0, failures, $"{label} has unknown baseline level {capability.BaselineLevel}");
            Check(LevelIndex(manifest, capability.CurrentLevel) >= 0, failures, $"{label} has unknown current level {capability.CurrentLevel}");
            Check(LevelIndex(manifest, capability.RequiredLevel) >= 0, failures, $"{label} has unknown required level {capability.RequiredLevel}");
            Check(capability.BaselineEvidence.Length > 0, failures, $"{label} must retain at least one checkpoint evidence item");

            foreach (BaselineEvidence evidence in capability.BaselineEvidence)
            {
                Check(evidence.Kind is "source-symbol" or "test-symbol" or "negative-audit", failures, $"{label} has unknown baseline evidence kind {evidence.Kind}");
                Check(!string.IsNullOrWhiteSpace(evidence.Path), failures, $"{label} baseline evidence must name a ZIP entry");
                Check(!string.IsNullOrWhiteSpace(evidence.Symbol), failures, $"{label} baseline evidence {evidence.Path} must name the audited symbol/token");
                Check(!string.IsNullOrWhiteSpace(evidence.Note), failures, $"{label} baseline evidence {evidence.Path} must explain what the symbol proves");
            }

            foreach (string area in capability.RequiredAreas)
                Check(ValidAreas.Contains(area), failures, $"{label} names unknown required mapping area {area}");

            foreach (string lane in capability.RequiredLanes)
                Check(laneIds.Contains(lane), failures, $"{label} requires undeclared lane {lane}");

            Check(capability.RequiredAreas.Distinct(StringComparer.Ordinal).Count() == capability.RequiredAreas.Length, failures, $"{label} repeats a required area");
            Check(capability.RequiredLanes.Distinct(StringComparer.Ordinal).Count() == capability.RequiredLanes.Length, failures, $"{label} repeats a required lane");
            Check(capability.RequiredTestIds.Distinct(StringComparer.Ordinal).Count() == capability.RequiredTestIds.Length, failures, $"{label} repeats a required test id");

            if (capability.Scope is "mandatory-core" or "retained-regression" or "accepted-replacement")
            {
                Check(capability.RequiredLevel != "absent", failures, $"{label} is in production scope but requires only absent");
                Check(capability.RequiredAreas.Length > 0, failures, $"{label} is in production scope but has no required mapping areas");
                Check(capability.RequiredLanes.Length > 0, failures, $"{label} is in production scope but has no required executable lanes");
                Check(capability.RequiredTestIds.Length > 0, failures, $"{label} is in production scope but has no required test ids");
            }
        }

        foreach (string id in RequiredAdvancedCapabilityIds)
        {
            CapabilityRow? capability = manifest.Capabilities.SingleOrDefault(row => row.Id == id);
            Check(capability is not null, failures, $"Advanced truth ledger is missing {id}");
            if (capability is null) continue;
            Check(capability.Scope == "advanced-record-only", failures, $"{id} must remain advanced-record-only unless the requirement is re-grilled");
            Check(capability.MissingSemantics.Length > 0, failures, $"{id} must enumerate missing API/execution semantics");
        }

        AssertNoFailures(failures, "Capability continuity manifest is structurally incomplete");
    }

    [Fact]
    public void Original_public_api_inventory_is_complete_unique_and_capability_mapped()
    {
        CapabilityManifest manifest = LoadManifest();
        PublicApiInventory inventory = LoadInventory();
        var failures = new List<string>();
        var capabilityIds = manifest.Capabilities.Select(static capability => capability.Id).ToHashSet(StringComparer.Ordinal);
        var declaredGroups = inventory.Groups.ToDictionary(static group => group.Id, StringComparer.Ordinal);
        var descriptorGroups = manifest.BaselinePublicApiInventory.Groups.ToDictionary(static group => group.Id, StringComparer.Ordinal);

        Check(inventory.Schema == "graphics-rendergraph-public-api-inventory.schema.json", failures, "Public API inventory must use the checked-in local schema");
        Check(inventory.SchemaVersion == 1, failures, "Public API inventory schemaVersion must be 1");
        Check(inventory.InventoryId == "someengine.graphics-rendergraph.original-public-api", failures, "Public API inventory identity changed unexpectedly");
        Check(inventory.AcceptedRun == "0004", failures, "Public API inventory must remain attached to accepted run 0004");
        Check(inventory.BaselineArtifactSha256 == manifest.BaselineArtifact.Sha256, failures, "Public API inventory and capability ledger must pin the same ZIP bytes");
        Check(inventory.Symbols.Length == 230, failures, $"Public API inventory must contain exactly 230 audited declarations, found {inventory.Symbols.Length}");

        foreach (InventoryGroupDescriptor expected in manifest.BaselinePublicApiInventory.Groups)
        {
            Check(declaredGroups.TryGetValue(expected.Id, out InventoryGroup? group), failures, $"Public API inventory is missing group {expected.Id}");
            if (group is null) continue;
            Check(group.ExpectedCount == expected.ExpectedCount, failures, $"Inventory group {expected.Id} count disagrees with the capability manifest");
            int actual = inventory.Symbols.Count(symbol => symbol.Group == expected.Id);
            Check(actual == expected.ExpectedCount, failures, $"Inventory group {expected.Id} expected {expected.ExpectedCount} symbols but found {actual}");
        }

        foreach (IGrouping<string, InventorySymbol> duplicate in inventory.Symbols.GroupBy(static symbol => symbol.Id, StringComparer.Ordinal).Where(static group => group.Count() > 1))
            failures.Add($"Duplicate public API inventory id {duplicate.Key}");
        foreach (IGrouping<string, InventorySymbol> duplicate in inventory.Symbols.GroupBy(static symbol => InventorySourceKey(symbol.Path, symbol.Symbol), StringComparer.Ordinal).Where(static group => group.Count() > 1))
            failures.Add($"Duplicate public API inventory source declaration {duplicate.Key.Replace('\n', ' ')}");

        foreach (InventorySymbol symbol in inventory.Symbols)
        {
            Check(Regex.IsMatch(symbol.Id, "^[a-z0-9]+(?:[.-][a-z0-9]+)*$"), failures, $"Inventory id {symbol.Id} is not stable lowercase dotted form");
            Check(declaredGroups.TryGetValue(symbol.Group, out InventoryGroup? group), failures, $"{symbol.Id} names undeclared group {symbol.Group}");
            Check(capabilityIds.Contains(symbol.CapabilityId), failures, $"{symbol.Id} maps to missing capability {symbol.CapabilityId}");
            Check(ValidInventoryDispositions.Contains(symbol.Disposition), failures, $"{symbol.Id} has unknown disposition {symbol.Disposition}");
            Check(!string.IsNullOrWhiteSpace(symbol.Path), failures, $"{symbol.Id} must name its ZIP entry");
            Check(!string.IsNullOrWhiteSpace(symbol.Name), failures, $"{symbol.Id} must name its declaration");
            Check(!string.IsNullOrWhiteSpace(symbol.Symbol), failures, $"{symbol.Id} must retain an exact ZIP source token");
            Check(!string.IsNullOrWhiteSpace(symbol.Note), failures, $"{symbol.Id} must explain its mapping");
            if (group is not null)
            {
                bool pathMatches = group.PathIsPrefix
                    ? symbol.Path.StartsWith(group.Path, StringComparison.Ordinal)
                    : symbol.Path == group.Path;
                Check(pathMatches, failures, $"{symbol.Id} path {symbol.Path} is outside inventory group {group.Id}");
            }
        }

        Check(descriptorGroups.Count == declaredGroups.Count, failures, "Manifest and inventory must declare the same public API groups");
        AssertNoFailures(failures, "Original public API inventory is incomplete or ambiguously mapped");
    }

    [Fact]
    public void Original_checkpoint_hash_and_evidence_match_when_the_forensic_zip_is_present()
    {
        CapabilityManifest manifest = LoadManifest();
        string root = HarnessConfig.ResolveRepoRoot();
        string artifactPath = Path.Combine(root, manifest.BaselineArtifact.Path.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(artifactPath))
        {
            Assert.False(manifest.BaselineArtifact.RequiredInCleanCheckout);
            return;
        }

        string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactPath)));
        Assert.Equal(manifest.BaselineArtifact.Sha256, actualHash);

        using ZipArchive archive = ZipFile.OpenRead(artifactPath);
        var failures = new List<string>();

        foreach (CapabilityRow capability in manifest.Capabilities)
        {
            foreach (BaselineEvidence evidence in capability.BaselineEvidence)
            {
                ZipArchiveEntry? entry = archive.GetEntry(evidence.Path);
                if (entry is null)
                {
                    failures.Add($"{capability.Id} checkpoint evidence entry does not exist: {evidence.Path}");
                    continue;
                }

                using var reader = new StreamReader(entry.Open());
                string source = reader.ReadToEnd();
                bool contains = source.Contains(evidence.Symbol, StringComparison.Ordinal);

                if (evidence.Kind == "negative-audit")
                {
                    if (contains)
                        failures.Add($"{capability.Id} negative checkpoint audit is no longer true: {evidence.Path} contains {evidence.Symbol}");
                }
                else if (!contains)
                {
                    failures.Add($"{capability.Id} checkpoint evidence is stale: {evidence.Path} does not contain {evidence.Symbol}");
                }
            }
        }

        ValidatePublicApiInventoryAgainstArchive(archive, LoadInventory(), failures);

        AssertNoFailures(failures, "Original checkpoint evidence no longer matches the accepted ledger");
    }

    [Fact]
    public void Current_mappings_reference_existing_files_symbols_lanes_and_test_ids()
    {
        CapabilityManifest manifest = LoadManifest();
        string root = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();
        var laneIds = manifest.Lanes.Select(static lane => lane.Id).ToHashSet(StringComparer.Ordinal);

        foreach (CapabilityRow capability in manifest.Capabilities)
        {
            foreach ((string area, SourceMapping mapping) in SourceMappings(capability))
                ValidateSourceMapping(root, capability.Id, area, mapping, failures);

            foreach (TestMapping test in capability.Mappings.Tests)
            {
                ValidateSourceMapping(root, capability.Id, "tests", test, failures);
                Check(laneIds.Contains(test.Lane), failures, $"{capability.Id} test {test.TestId} uses undeclared lane {test.Lane}");

                string path = Absolute(root, test.Path);
                if (!File.Exists(path)) continue;
                IReadOnlySet<string> discovered = DiscoverTestIds(File.ReadAllText(path));
                Check(discovered.Contains(test.TestId), failures, $"{capability.Id} mapped test id {test.TestId} is not discoverable in {test.Path}");
            }
        }

        AssertNoFailures(failures, "Capability mappings contain nonexistent or stale evidence");
    }

    [Fact]
    public void Required_capabilities_cannot_be_downgraded_or_left_partially_mapped()
    {
        CapabilityManifest manifest = LoadManifest();
        var failures = new List<string>();

        foreach (CapabilityRow capability in manifest.Capabilities)
        {
            int baseline = LevelIndex(manifest, capability.BaselineLevel);
            int current = LevelIndex(manifest, capability.CurrentLevel);
            int required = LevelIndex(manifest, capability.RequiredLevel);

            if (current < required)
                failures.Add($"{capability.Id} is {capability.CurrentLevel}, below accepted required level {capability.RequiredLevel}");

            if (current < baseline && capability.AcceptedDecision is null)
                failures.Add($"{capability.Id} dropped from checkpoint {capability.BaselineLevel} to {capability.CurrentLevel} without an accepted run-0004 decision");

            if (capability.BaselineLevel == "native-execution" && current < baseline)
                failures.Add($"{capability.Id} had real native execution in the checkpoint and cannot be downgraded by a ledger decision");

            if (capability.AcceptedDecision is { } decision)
            {
                Check(decision.Run == "0004", failures, $"{capability.Id} decision is not attached to accepted run 0004");
                Check(decision.Kind is "accepted-replacement" or "truth-correction" or "record-only-this-run", failures, $"{capability.Id} has unknown decision kind {decision.Kind}");
                Check(!string.IsNullOrWhiteSpace(decision.Reason), failures, $"{capability.Id} decision must explain why");
                Check(!string.IsNullOrWhiteSpace(decision.ObservableSemantics), failures, $"{capability.Id} decision must pin observable semantics");
                if (decision.Kind == "record-only-this-run")
                    Check(capability.Scope is "advanced-record-only" or "recorded-gap", failures, $"{capability.Id} uses record-only outside the accepted advanced/gap scope");
            }

            foreach (string area in capability.RequiredAreas)
            {
                int count = area switch
                {
                    "public" => capability.Mappings.Public.Length,
                    "null" => capability.Mappings.Null.Length,
                    "d3d12" => capability.Mappings.D3D12.Length,
                    "renderGraph" => capability.Mappings.RenderGraph.Length,
                    "tests" => capability.Mappings.Tests.Length,
                    _ => 0,
                };

                if (count == 0)
                    failures.Add($"{capability.Id} requires {area} closure but has no current mapping");
            }
        }

        AssertNoFailures(failures, "Required Graphics/RenderGraph capability continuity is not closed");
    }

    [Fact]
    public void Required_lanes_have_discoverable_non_skipping_tests()
    {
        CapabilityManifest manifest = LoadManifest();
        string root = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();
        var lanes = manifest.Lanes.ToDictionary(static lane => lane.Id, StringComparer.Ordinal);

        foreach (CapabilityLane lane in manifest.Lanes.Where(static lane => lane.Required))
        {
            string projectPath = Absolute(root, lane.TestProject);
            Check(File.Exists(projectPath), failures, $"Required lane {lane.Id} test project does not exist: {lane.TestProject}");
        }

        foreach (CapabilityRow capability in manifest.Capabilities)
        {
            var mappedById = capability.Mappings.Tests.ToDictionary(static test => test.TestId, StringComparer.Ordinal);

            foreach (string testId in capability.RequiredTestIds)
                Check(mappedById.ContainsKey(testId), failures, $"{capability.Id} required test id is not mapped/discoverable: {testId}");

            foreach (string requiredLane in capability.RequiredLanes)
            {
                TestMapping[] laneTests = capability.Mappings.Tests.Where(test => test.Lane == requiredLane).ToArray();
                if (laneTests.Length == 0)
                {
                    failures.Add($"{capability.Id} requires lane {requiredLane} but maps zero tests to it");
                    continue;
                }

                if (!lanes.TryGetValue(requiredLane, out CapabilityLane? lane))
                    continue;

                foreach (TestMapping test in laneTests)
                {
                    string path = Absolute(root, test.Path);
                    if (!File.Exists(path)) continue;
                    string source = File.ReadAllText(path);
                    IReadOnlySet<string> discovered = DiscoverTestIds(source);
                    Check(discovered.Contains(test.TestId), failures, $"{capability.Id} lane {requiredLane} discovers zero instances of {test.TestId}");

                    if (!lane.AllowSilentSkip && ContainsSilentPlatformSkip(source))
                        failures.Add($"{capability.Id} required lane {requiredLane} maps {test.TestId} to {test.Path}, which contains an early-return or dynamic skip path");
                }
            }
        }

        AssertNoFailures(failures, "Required capability test lanes are absent, undiscoverable, or silently skipped");
    }

    [Fact]
    public void Indexed_documentation_claims_cannot_exceed_current_evidence_level()
    {
        CapabilityManifest manifest = LoadManifest();
        string root = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();

        foreach (CapabilityRow capability in manifest.Capabilities)
        {
            int current = LevelIndex(manifest, capability.CurrentLevel);
            foreach (DocumentationClaim claim in capability.DocumentationClaims)
            {
                string path = Absolute(root, claim.Path);
                if (!File.Exists(path))
                {
                    failures.Add($"{capability.Id} indexed documentation does not exist: {claim.Path}");
                    continue;
                }

                string text = File.ReadAllText(path);
                Check(text.Contains(claim.Marker, StringComparison.Ordinal), failures, $"{capability.Id} indexed documentation marker changed or disappeared in {claim.Path}; update the document and ledger together");
                int claimed = LevelIndex(manifest, claim.ClaimedLevel);
                if (claimed > current)
                    failures.Add($"{capability.Id} documentation {claim.Path} claims {claim.ClaimedLevel}, above current evidence {capability.CurrentLevel}: {claim.Marker}");
            }
        }

        AssertNoFailures(failures, "Documentation overstates Graphics/RenderGraph capability evidence");
    }

    [Fact]
    public void Portable_graphics_and_render_graph_sources_do_not_reference_native_backend_types()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        string[] portableRoots =
        [
            Absolute(root, "src/SomeEngine.Graphics"),
            Absolute(root, "src/SomeEngine.Graphics.Null"),
            Absolute(root, "src/SomeEngine.RenderGraph"),
        ];
        string[] forbidden = ["Vortice.", "ID3D12", "IDXGI", "DXGI_FORMAT", "D3D12_"];
        var failures = new List<string>();

        foreach (string portableRoot in portableRoots)
        {
            if (!Directory.Exists(portableRoot))
            {
                failures.Add($"Portable capability root does not exist: {Path.GetRelativePath(root, portableRoot)}");
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(portableRoot, "*.cs", SearchOption.AllDirectories)
                         .Where(static path => !IsGeneratedOutput(path)))
            {
                string source = File.ReadAllText(path);
                foreach (string token in forbidden)
                {
                    if (source.Contains(token, StringComparison.Ordinal))
                        failures.Add($"{Path.GetRelativePath(root, path)} leaks native token {token} into the portable boundary");
                }
            }
        }

        AssertNoFailures(failures, "Portable Graphics/RenderGraph boundary leaks native D3D12/DXGI types");
    }

    [Fact]
    public void Asset_cook_clean_checkout_uses_only_tracked_dxc_runtime_inputs()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        string projectPath = Absolute(root, "tools/SomeEngine.AssetCook/SomeEngine.AssetCook.csproj");
        string project = File.ReadAllText(projectPath);
        var failures = new List<string>();

        Check(!project.Contains("dxc.exe", StringComparison.OrdinalIgnoreCase), failures,
            "AssetCook must not copy the ignored local dxc.exe into its output.");
        foreach (string input in new[]
        {
            "external/dxc/bin/x64/dxcompiler.dll",
            "external/dxc/bin/x64/dxil.dll",
        })
        {
            Check(File.Exists(Absolute(root, input)), failures, $"Tracked DXC runtime input is missing: {input}");
            string tracked = Repo.Git($"ls-files -- {input}").Trim();
            Check(string.Equals(tracked.Replace('\\', '/'), input, StringComparison.OrdinalIgnoreCase), failures,
                $"DXC runtime input must be tracked for clean checkout: {input}");
        }

        AssertNoFailures(failures, "AssetCook has an untracked clean-checkout dependency");
    }

    [Fact]
    public void Benchmark_and_soak_host_is_a_versioned_hard_executable()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        string projectPath = Absolute(root, "benchmarks/SomeEngine.Graphics.Benchmarks/SomeEngine.Graphics.Benchmarks.csproj");
        string sourcePath = Absolute(root, "benchmarks/SomeEngine.Graphics.Benchmarks/Benchmarks.cs");
        string runnerPath = Absolute(root, "harness/RunHarness.ps1");
        var failures = new List<string>();

        Check(File.Exists(projectPath), failures, "Graphics benchmark executable project is missing.");
        Check(File.Exists(sourcePath), failures, "Graphics benchmark scenarios are missing.");
        if (File.Exists(projectPath))
        {
            string project = File.ReadAllText(projectPath);
            Check(project.Contains("<OutputType>Exe</OutputType>", StringComparison.Ordinal), failures,
                "Graphics benchmark host must be directly executable.");
            Check(!project.Contains("BenchmarkDotNet", StringComparison.OrdinalIgnoreCase), failures,
                "The hard benchmark infrastructure must not require a new restore-only benchmark package.");
        }
        if (File.Exists(sourcePath))
        {
            string source = File.ReadAllText(sourcePath);
            foreach (string token in new[]
            {
                "SchemaVersion = 1",
                "CompilerCacheScenario",
                "RhiDescriptorResourceScenario",
                "LightweightTenThousandFrameSoak",
                "10_000",
                "OperatingSystem",
                "Cpu",
                "Adapter",
                "Driver",
                "Build",
            })
            {
                Check(source.Contains(token, StringComparison.Ordinal), failures,
                    $"Graphics benchmark host omits required artifact/scenario token {token}.");
            }
        }

        string runner = File.ReadAllText(runnerPath);
        Check(runner.Contains("graphics-benchmark-soak", StringComparison.Ordinal), failures,
            "The single harness entry does not run the graphics benchmark/soak hard step.");
        Check(runner.Contains("benchmarks/SomeEngine.Graphics.Benchmarks/SomeEngine.Graphics.Benchmarks.csproj", StringComparison.Ordinal), failures,
            "The harness hard step is not wired to the accepted graphics benchmark executable.");

        AssertNoFailures(failures, "Graphics benchmark/soak infrastructure is not a hard executable");
    }

    private static CapabilityManifest LoadManifest()
    {
        string path = Absolute(HarnessConfig.ResolveRepoRoot(), ManifestRelativePath);
        Assert.True(File.Exists(path), $"Capability continuity manifest must exist at {ManifestRelativePath}");
        CapabilityManifest? manifest = JsonSerializer.Deserialize<CapabilityManifest>(
            File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            });
        return Assert.IsType<CapabilityManifest>(manifest);
    }

    private static PublicApiInventory LoadInventory()
    {
        string path = Absolute(HarnessConfig.ResolveRepoRoot(), InventoryRelativePath);
        Assert.True(File.Exists(path), $"Original public API inventory must exist at {InventoryRelativePath}");
        PublicApiInventory? inventory = JsonSerializer.Deserialize<PublicApiInventory>(
            File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            });
        return Assert.IsType<PublicApiInventory>(inventory);
    }

    private static void ValidatePublicApiInventoryAgainstArchive(
        ZipArchive archive,
        PublicApiInventory inventory,
        List<string> failures)
    {
        var sourceCache = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (InventorySymbol symbol in inventory.Symbols)
        {
            if (!sourceCache.TryGetValue(symbol.Path, out string? source))
            {
                ZipArchiveEntry? entry = archive.GetEntry(symbol.Path);
                if (entry is null)
                {
                    failures.Add($"{symbol.Id} public API inventory ZIP entry does not exist: {symbol.Path}");
                    continue;
                }

                using var reader = new StreamReader(entry.Open());
                source = reader.ReadToEnd();
                sourceCache.Add(symbol.Path, source);
            }

            if (!source.Contains(symbol.Symbol, StringComparison.Ordinal))
                failures.Add($"{symbol.Id} public API inventory token is stale: {symbol.Path} does not contain {symbol.Symbol}");
        }

        string devicePath = "src/Graphics/IDevice.cs";
        if (sourceCache.TryGetValue(devicePath, out string? deviceSource))
        {
            HashSet<string> extracted = ExtractDeviceMethodDeclarations(deviceSource)
                .Select(symbol => InventorySourceKey(devicePath, symbol))
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> inventoried = inventory.Symbols
                .Where(static symbol => symbol.Group == "graphics-device-methods")
                .Select(static symbol => InventorySourceKey(symbol.Path, symbol.Symbol))
                .ToHashSet(StringComparer.Ordinal);
            AddSetDifferences("IDevice method", extracted, inventoried, failures);
        }

        HashSet<string> extractedTypes = [];
        foreach (ZipArchiveEntry entry in archive.Entries.Where(static entry =>
                     entry.FullName.StartsWith("src/RenderGraph.Core/", StringComparison.Ordinal) &&
                     entry.FullName.EndsWith(".cs", StringComparison.Ordinal)))
        {
            string source;
            if (!sourceCache.TryGetValue(entry.FullName, out string? cachedSource))
            {
                using var reader = new StreamReader(entry.Open());
                source = reader.ReadToEnd();
                sourceCache.Add(entry.FullName, source);
            }
            else
            {
                source = cachedSource;
            }

            foreach (string symbol in ExtractPublicTypeDeclarations(source))
                extractedTypes.Add(InventorySourceKey(entry.FullName, symbol));
        }

        HashSet<string> inventoriedTypes = inventory.Symbols
            .Where(static symbol => symbol.Group == "rendergraph-public-type-declarations")
            .Select(static symbol => InventorySourceKey(symbol.Path, symbol.Symbol))
            .ToHashSet(StringComparer.Ordinal);
        AddSetDifferences("RenderGraph public type declaration", extractedTypes, inventoriedTypes, failures);
    }

    private static IEnumerable<string> ExtractDeviceMethodDeclarations(string source)
    {
        const string pattern = @"(?m)^\s{4}(?<symbol>[^\r\n]*\);)\s*$";
        return Regex.Matches(source, pattern, RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Groups["symbol"].Value.Trim());
    }

    private static IEnumerable<string> ExtractPublicTypeDeclarations(string source)
    {
        const string pattern = @"(?m)^\s*public\s+(?:(?:readonly|ref|sealed|static|abstract|partial)\s+)*(?:(?:record(?:\s+(?:class|struct))?|class|struct|interface|enum)\s+)(?<name>[A-Za-z_][A-Za-z0-9_]*)";
        return Regex.Matches(source, pattern, RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(static match => match.Value.Trim());
    }

    private static void AddSetDifferences(
        string label,
        HashSet<string> extracted,
        HashSet<string> inventoried,
        List<string> failures)
    {
        foreach (string missing in extracted.Except(inventoried, StringComparer.Ordinal))
            failures.Add($"{label} is absent from the checked-in inventory: {missing.Replace('\n', ' ')}");
        foreach (string stale in inventoried.Except(extracted, StringComparer.Ordinal))
            failures.Add($"{label} inventory entry is not extracted from the ZIP: {stale.Replace('\n', ' ')}");
    }

    private static string InventorySourceKey(string path, string symbol) => path + "\n" + symbol;

    private static int LevelIndex(CapabilityManifest manifest, string level) =>
        Array.IndexOf(manifest.LevelOrder, level);

    private static IEnumerable<(string Area, SourceMapping Mapping)> SourceMappings(CapabilityRow capability)
    {
        foreach (SourceMapping mapping in capability.Mappings.Public) yield return ("public", mapping);
        foreach (SourceMapping mapping in capability.Mappings.Null) yield return ("null", mapping);
        foreach (SourceMapping mapping in capability.Mappings.D3D12) yield return ("d3d12", mapping);
        foreach (SourceMapping mapping in capability.Mappings.RenderGraph) yield return ("renderGraph", mapping);
    }

    private static void ValidateSourceMapping(
        string root,
        string capabilityId,
        string area,
        SourceMapping mapping,
        List<string> failures)
    {
        string path = Absolute(root, mapping.Path);
        if (!File.Exists(path))
        {
            failures.Add($"{capabilityId} {area} mapping file does not exist: {mapping.Path}");
            return;
        }

        string source = File.ReadAllText(path);
        if (!source.Contains(mapping.Symbol, StringComparison.Ordinal))
            failures.Add($"{capabilityId} {area} mapping is stale: {mapping.Path} does not contain {mapping.Symbol}");
    }

    private static IReadOnlySet<string> DiscoverTestIds(string source)
    {
        Match namespaceMatch = Regex.Match(source, @"\bnamespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*[;{]");
        Match classMatch = Regex.Match(source, @"\bpublic\s+(?:sealed\s+)?class\s+([A-Za-z_][A-Za-z0-9_]*)");
        if (!namespaceMatch.Success || !classMatch.Success)
            return new HashSet<string>(StringComparer.Ordinal);

        string prefix = namespaceMatch.Groups[1].Value + "." + classMatch.Groups[1].Value + ".";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        const string pattern = @"\[(?:Fact|Theory|BenchmarkScenario)(?:\([^\]]*\))?\]\s*(?:\[[^\]]+\]\s*)*public\s+(?:async\s+)?(?:void|Task|ValueTask)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(";
        foreach (Match method in Regex.Matches(source, pattern, RegexOptions.CultureInvariant))
            ids.Add(prefix + method.Groups[1].Value);
        return ids;
    }

    private static bool ContainsSilentPlatformSkip(string source) =>
        Regex.IsMatch(
            source,
            @"if\s*\(\s*!\s*OperatingSystem\.IsWindows\s*\(\s*\)\s*\)\s*(?:\{\s*)?return\s*;",
            RegexOptions.CultureInvariant)
        || Regex.IsMatch(source, @"\bSkip\s*=", RegexOptions.CultureInvariant)
        || source.Contains("SkipException", StringComparison.Ordinal);

    private static string Absolute(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static bool IsGeneratedOutput(string path) =>
        path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void Check(bool condition, List<string> failures, string failure)
    {
        if (!condition) failures.Add(failure);
    }

    private static void AssertNoFailures(List<string> failures, string title) =>
        Assert.True(failures.Count == 0, title + ":\n" + string.Join("\n", failures));

    private sealed class CapabilityManifest
    {
        [JsonPropertyName("$schema")]
        public string Schema { get; init; } = "";
        public int SchemaVersion { get; init; }
        public string ManifestId { get; init; } = "";
        public string AcceptedRun { get; init; } = "";
        public string TargetRepository { get; init; } = "";
        public string CheckpointCommit { get; init; } = "";
        public BaselineArtifact BaselineArtifact { get; init; } = new();
        public BaselinePublicApiInventory BaselinePublicApiInventory { get; init; } = new();
        public string[] LevelOrder { get; init; } = [];
        public CapabilityLane[] Lanes { get; init; } = [];
        public CapabilityRow[] Capabilities { get; init; } = [];
    }

    private sealed class BaselineArtifact
    {
        public string Path { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public bool RequiredInCleanCheckout { get; init; }
        public string Note { get; init; } = "";
    }

    private sealed class CapabilityLane
    {
        public string Id { get; init; } = "";
        public string TestProject { get; init; } = "";
        public bool Required { get; init; }
        public bool AllowSilentSkip { get; init; }
        public string? Platform { get; init; }
        public string Note { get; init; } = "";
    }

    private sealed class CapabilityRow
    {
        public string Id { get; init; } = "";
        public string[] Aliases { get; init; } = [];
        public string Family { get; init; } = "";
        public string Scope { get; init; } = "";
        public string BaselineLevel { get; init; } = "";
        public BaselineEvidence[] BaselineEvidence { get; init; } = [];
        public string CurrentLevel { get; init; } = "";
        public string RequiredLevel { get; init; } = "";
        public string[] RequiredAreas { get; init; } = [];
        public string[] RequiredLanes { get; init; } = [];
        public string[] RequiredTestIds { get; init; } = [];
        public CapabilityMappings Mappings { get; init; } = new();
        public AcceptedDecision? AcceptedDecision { get; init; }
        public string[] Gaps { get; init; } = [];
        public string[] MissingSemantics { get; init; } = [];
        public DocumentationClaim[] DocumentationClaims { get; init; } = [];
    }

    private sealed class BaselineEvidence
    {
        public string Kind { get; init; } = "";
        public string Path { get; init; } = "";
        public string Symbol { get; init; } = "";
        public string Note { get; init; } = "";
    }

    private sealed class CapabilityMappings
    {
        [JsonPropertyName("public")]
        public SourceMapping[] Public { get; init; } = [];
        [JsonPropertyName("null")]
        public SourceMapping[] Null { get; init; } = [];
        [JsonPropertyName("d3d12")]
        public SourceMapping[] D3D12 { get; init; } = [];
        [JsonPropertyName("renderGraph")]
        public SourceMapping[] RenderGraph { get; init; } = [];
        [JsonPropertyName("tests")]
        public TestMapping[] Tests { get; init; } = [];
    }

    private class SourceMapping
    {
        public string Path { get; init; } = "";
        public string Symbol { get; init; } = "";
    }

    private sealed class TestMapping : SourceMapping
    {
        public string TestId { get; init; } = "";
        public string Lane { get; init; } = "";
    }

    private sealed class AcceptedDecision
    {
        public string Kind { get; init; } = "";
        public string Run { get; init; } = "";
        public string Reason { get; init; } = "";
        public string ObservableSemantics { get; init; } = "";
    }

    private sealed class DocumentationClaim
    {
        public string Path { get; init; } = "";
        public string Marker { get; init; } = "";
        public string ClaimedLevel { get; init; } = "";
    }

    private sealed class BaselinePublicApiInventory
    {
        public string Path { get; init; } = "";
        public string SchemaPath { get; init; } = "";
        public int SchemaVersion { get; init; }
        public InventoryGroupDescriptor[] Groups { get; init; } = [];
    }

    private sealed class InventoryGroupDescriptor
    {
        public string Id { get; init; } = "";
        public int ExpectedCount { get; init; }
    }

    private sealed class PublicApiInventory
    {
        [JsonPropertyName("$schema")]
        public string Schema { get; init; } = "";
        public int SchemaVersion { get; init; }
        public string InventoryId { get; init; } = "";
        public string AcceptedRun { get; init; } = "";
        public string BaselineArtifactSha256 { get; init; } = "";
        public InventoryGroup[] Groups { get; init; } = [];
        public InventorySymbol[] Symbols { get; init; } = [];
    }

    private sealed class InventoryGroup
    {
        public string Id { get; init; } = "";
        public string SymbolKind { get; init; } = "";
        public string Path { get; init; } = "";
        public bool PathIsPrefix { get; init; }
        public int ExpectedCount { get; init; }
    }

    private sealed class InventorySymbol
    {
        public string Id { get; init; } = "";
        public string Group { get; init; } = "";
        public string Path { get; init; } = "";
        public string Name { get; init; } = "";
        public string Symbol { get; init; } = "";
        public string CapabilityId { get; init; } = "";
        public string Disposition { get; init; } = "";
        public string Note { get; init; } = "";
    }
}
