# Product build and tests are the default path. Harness checks are opt-in.

set windows-shell := ["pwsh.exe", "-NoProfile", "-Command"]

aot_executable_extension := if os() == "windows" { ".exe" } else { "" }
fuzz_seed_pattern := '^(0[xX][0-9A-Fa-f]{1,16}|[0-9]{1,20})$'
fuzz_steps_pattern := '^[0-9]{1,7}$'
single_quoted_argument_pattern := "^[^'\\r\\n]+$"
aot_rid_pattern := '^(win-(x64|arm64)|linux-(x64|arm64)|linux-musl-(x64|arm64)|osx-(x64|arm64))$'
aot_rid_list_pattern := '^(win-(x64|arm64)|linux-(x64|arm64)|linux-musl-(x64|arm64)|osx-(x64|arm64))(,(win-(x64|arm64)|linux-(x64|arm64)|linux-musl-(x64|arm64)|osx-(x64|arm64)))*$'

default: test

test *args:
    dotnet test SomeEngine.slnx {{ args }}

build *args:
    dotnet build SomeEngine.slnx {{ args }}

harness-test *args:
    dotnet test SomeEngine.Harness.slnx {{ args }}

# Short deterministic model/reference check. Failure artifacts are emitted beside the test binary.
ecs-fuzz *args:
    dotnet test tests/SomeEngine.ECS.Fuzz.Tests/SomeEngine.ECS.Fuzz.Tests.csproj -c Release {{ args }}

# Fail-closed long campaign: both inputs are required by the recipe signature.
ecs-fuzz-campaign seed steps:
    dotnet test tests/SomeEngine.ECS.Fuzz.Tests/SomeEngine.ECS.Fuzz.Tests.csproj -c Release --filter FullyQualifiedName~EnvironmentCampaign -e SOMEENGINE_ECS_FUZZ_SEED={{ if seed =~ fuzz_seed_pattern { seed } else { error("seed must be decimal or 0x-prefixed hexadecimal") } }} -e SOMEENGINE_ECS_FUZZ_STEPS={{ if steps =~ fuzz_steps_pattern { steps } else { error("steps must contain decimal digits only") } }}

# Replay the exact minimized trace contained in a fuzz failure artifact.
ecs-fuzz-replay trace:
    dotnet test tests/SomeEngine.ECS.Fuzz.Tests/SomeEngine.ECS.Fuzz.Tests.csproj -c Release --filter FullyQualifiedName~EnvironmentTrace -e SOMEENGINE_ECS_FUZZ_TRACE='{{ if trace =~ single_quoted_argument_pattern { trace } else { error("trace path may not contain a quote or line break") } }}'

# JSON performance runner. With no arguments this is the intentionally short smoke profile.
ecs-perf *args:
    dotnet run --project benchmarks/SomeEngine.ECS.Benchmarks/SomeEngine.ECS.Benchmarks.csproj -c Release -- {{ args }}

# Restore and force-rebuild a NativeAOT image without claiming runtime certification.
ecs-aot-build rid:
    dotnet restore tools/SomeEngine.ECS.AotSmoke/SomeEngine.ECS.AotSmoke.csproj -r {{ if rid =~ aot_rid_pattern { rid } else { error("unsupported NativeAOT RID") } }} -p:PublishAot=true -p:_IsPublishing=true
    dotnet publish tools/SomeEngine.ECS.AotSmoke/SomeEngine.ECS.AotSmoke.csproj -c Release -r {{ if rid =~ aot_rid_pattern { rid } else { error("unsupported NativeAOT RID") } }} --self-contained true --no-restore -t:Rebuild -o tools/SomeEngine.ECS.AotSmoke/bin/Release/net10.0/{{ if rid =~ aot_rid_pattern { rid } else { error("unsupported NativeAOT RID") } }}/cert-publish -p:PublishAot=true -p:SelfContained=true -p:TreatWarningsAsErrors=true -p:ILLinkTreatWarningsAsErrors=true -p:TrimmerSingleWarn=false

# Host-compatible certification: forced native build followed by native semantic execution.
ecs-aot rid:
    dotnet restore tools/SomeEngine.ECS.AotSmoke/SomeEngine.ECS.AotSmoke.csproj -r {{ if rid =~ aot_rid_pattern { rid } else { error("unsupported NativeAOT RID") } }} -p:PublishAot=true -p:_IsPublishing=true
    dotnet publish tools/SomeEngine.ECS.AotSmoke/SomeEngine.ECS.AotSmoke.csproj -c Release -r {{ if rid =~ aot_rid_pattern { rid } else { error("unsupported NativeAOT RID") } }} --self-contained true --no-restore -t:Rebuild -o tools/SomeEngine.ECS.AotSmoke/bin/Release/net10.0/{{ if rid =~ aot_rid_pattern { rid } else { error("unsupported NativeAOT RID") } }}/cert-publish -p:PublishAot=true -p:SelfContained=true -p:TreatWarningsAsErrors=true -p:ILLinkTreatWarningsAsErrors=true -p:TrimmerSingleWarn=false
    ./tools/SomeEngine.ECS.AotSmoke/bin/Release/net10.0/{{ if rid =~ aot_rid_pattern { rid } else { error("unsupported NativeAOT RID") } }}/cert-publish/SomeEngine.ECS.AotSmoke{{ aot_executable_extension }}

# Host-executed NativeAOT matrix. Every comma-separated RID must be runnable on this host.
# Success writes schema-2 evidence containing clean source identity, the exact RID set, and hashes.
ecs-aot-matrix rids evidence='artifacts/ecs-aot-evidence.json':
    pwsh -NoProfile -File tools/SomeEngine.ECS.AotSmoke/Invoke-AotMatrix.ps1 -Rids '{{ if rids =~ aot_rid_list_pattern { rids } else { error("RIDs must be a non-empty comma-separated supported list") } }}' -Evidence '{{ if evidence =~ single_quoted_argument_pattern { evidence } else { error("evidence path may not contain a quote or line break") } }}'

ecs-aot-matrix-script-test:
    pwsh -NoProfile -File tools/SomeEngine.ECS.AotSmoke/Test-AotEvidenceDestination.ps1

# Fast local evidence loop; the full certification sequence and native execution are documented.
ecs-cert-smoke:
    dotnet test tests/SomeEngine.ECS.Fuzz.Tests/SomeEngine.ECS.Fuzz.Tests.csproj -c Release
    dotnet test tests/SomeEngine.ECS.Benchmarks.Tests/SomeEngine.ECS.Benchmarks.Tests.csproj -c Release
    dotnet run --project benchmarks/SomeEngine.ECS.Benchmarks/SomeEngine.ECS.Benchmarks.csproj -c Release -- --profile smoke
    dotnet run --project tools/SomeEngine.ECS.AotSmoke/SomeEngine.ECS.AotSmoke.csproj -c Release -p:PublishAot=false
