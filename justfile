# Product build and functional tests are the default path.

set windows-shell := ["pwsh.exe", "-NoProfile", "-Command"]

fuzz_seed_pattern := '^(0[xX][0-9A-Fa-f]{1,16}|[0-9]{1,20})$'
fuzz_steps_pattern := '^[0-9]{1,7}$'
single_quoted_argument_pattern := "^[^'\\r\\n]+$"

default: test

test *args:
    pwsh -NoProfile -File tools/Test.ps1 {{ args }}

build *args:
    pwsh -NoProfile -File tools/Build.ps1 {{ args }}

# Short deterministic model/reference check. Failure artifacts are emitted beside the test binary.
ecs-fuzz *args:
    dotnet test tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj -c Release {{ args }}

# Fail-closed long campaign: both inputs are required by the recipe signature.
ecs-fuzz-campaign seed steps:
    dotnet test tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj -c Release --filter FullyQualifiedName~EnvironmentCampaign -e SOMEENGINE_ECS_FUZZ_SEED={{ if seed =~ fuzz_seed_pattern { seed } else { error("seed must be decimal or 0x-prefixed hexadecimal") } }} -e SOMEENGINE_ECS_FUZZ_STEPS={{ if steps =~ fuzz_steps_pattern { steps } else { error("steps must contain decimal digits only") } }}

# Replay the exact minimized trace contained in a fuzz failure artifact.
ecs-fuzz-replay trace:
    dotnet test tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj -c Release --filter FullyQualifiedName~EnvironmentTrace -e SOMEENGINE_ECS_FUZZ_TRACE='{{ if trace =~ single_quoted_argument_pattern { trace } else { error("trace path may not contain a quote or line break") } }}'

# JSON performance runner. With no arguments this is the intentionally short smoke profile.
ecs-perf *args:
    dotnet run --project benchmarks/SomeEngine.ECS.Benchmarks/SomeEngine.ECS.Benchmarks.csproj -c Release -- {{ args }}

# Each destructive category runs in its own test process and writes a separate result file.
d3d12-device-loss:
    dotnet test tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj --filter 'Isolation=DeviceLossProcess' --logger 'trx;LogFileName=d3d12-device-loss.trx' --results-directory artifacts/test-results

d3d12-presentation:
    dotnet test tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj --filter 'Isolation=PresentationProcess' --logger 'trx;LogFileName=d3d12-presentation.trx' --results-directory artifacts/test-results

# Hardware adapters and installed Vulkan drivers are required by these tests.
test-hardware *args:
    pwsh -NoProfile -File tools/Test.ps1 -Category Hardware {{ args }}

# The seed and step count are required environment inputs for the long campaign.
test-long-fuzz *args:
    pwsh -NoProfile -File tools/Test.ps1 -Category LongFuzz {{ args }}

# Allocation and benchmark contracts use the independent performance category.
test-performance *args:
    pwsh -NoProfile -File tools/Test.ps1 -Category Performance {{ args }}

# DefaultGame supplies all three scenes; this runner enforces complete results within 60 seconds.
render-perf *args:
    pwsh -NoProfile -File tools/Measure-Scenes.ps1 {{ args }}
