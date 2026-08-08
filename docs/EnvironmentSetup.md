# .NET 10 environment setup

The repository pins SDK `10.0.301` in `global.json`.

## Normal machine with internet access

```bash
./scripts/bootstrap-dotnet.sh
source ./scripts/env.sh
dotnet --info
dotnet build SomeEngine.slnx -c Release
```

`bootstrap-dotnet.sh` downloads Microsoft's official `dotnet-install.sh`, installs the pinned SDK to `$HOME/.dotnet`, and configures repository-local CLI/NuGet caches. It is idempotent.

## OpenAI CAAS / restricted package-network container

The container blocks ordinary public networking but exposes authenticated Artifactory mirrors. Configure the Debian and Microsoft mirrors, then install through APT:

```bash
sudo tee /etc/apt/sources.list.d/caas-microsoft.sources >/dev/null <<APT
Types: deb
URIs: https://${CAAS_ARTIFACTORY_READER_USERNAME}:${CAAS_ARTIFACTORY_READER_PASSWORD}@${CAAS_ARTIFACTORY_BASE_URL}/artifactory/apt-microsoft-public/debian/13/prod
Suites: trixie
Components: main
Trusted: yes
APT
sudo apt-get update
sudo apt-get install -y --no-install-recommends --allow-unauthenticated dotnet-sdk-10.0
source ./scripts/env.sh
dotnet --info
```

`env.sh` also clears the CAAS Docker `PLATFORM=linux/amd64` variable before invoking MSBuild. Without that, `dotnet restore SomeEngine.slnx` can try an invalid solution configuration such as `Debug|linux/amd64`.

Do not commit Artifactory credentials. The variables are injected by the CAAS environment.

## NuGet in CAAS

For a one-off restore use the mirrored NuGet v3 source without writing credentials into the repository:

```bash
source ./scripts/env.sh
dotnet restore SomeEngine.slnx \
  --source "https://${CAAS_ARTIFACTORY_READER_USERNAME}:${CAAS_ARTIFACTORY_READER_PASSWORD}@${CAAS_ARTIFACTORY_BASE_URL}/artifactory/api/nuget/v3/nuget-public/index.json"
```

In restricted CAAS package-network mode, `env.sh` sets `NUGET_CERT_REVOCATION_MODE=offline`; otherwise signed NuGet package restore can block while certificate revocation endpoints are unreachable.

A normal developer machine uses `https://api.nuget.org/v3/index.json` from `NuGet.Config`.

## Verification

```bash
dotnet --version                  # 10.0.301
dotnet build SomeEngine.slnx -c Release
dotnet test tests/SomeEngine.RenderGraph.Tests/SomeEngine.RenderGraph.Tests.csproj -c Release
dotnet run -c Release --project samples/SomeEngine.RenderGraph.Sample/SomeEngine.RenderGraph.Sample.csproj
```

On Windows, validate the native D3D12/WARP surface separately:

```powershell
dotnet test tests\SomeEngine.Graphics.Direct3D12.Tests\SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release
```

### Windows D3D12 debug validation

The D3D12 integration suite uses the native debug layer when it is available. Windows
supplies that layer through the optional **Graphics Tools** capability; it is not
guaranteed to be present on a normal Windows installation. Install it from **Settings > System >
Optional features > View features > Graphics Tools**, or from an elevated PowerShell
prompt:

```powershell
Add-WindowsCapability -Online -Name Tools.Graphics.DirectX~~~~0.0.1.0
```

Start a new test process after installation, then run the D3D12 test project above.
When the component is absent, the D3D12 and RenderGraph suites still run their
debug-disabled functional WARP coverage; the two tests that specifically require the
native InfoQueue/DRED debug interfaces are reported as skipped. To make Graphics Tools
mandatory for a diagnostic CI lane, set the following variable before starting the
test process:

```powershell
$env:SOMEENGINE_REQUIRE_D3D12_DEBUG_LAYER = '1'
dotnet test tests\SomeEngine.Graphics.Direct3D12.Tests\SomeEngine.Graphics.Direct3D12.Tests.csproj -c Release
```

The fallback is test infrastructure only: product device creation still fails closed
when `EnableDebugLayer = true` is explicitly requested and the native layer is
unavailable. A machine used for the stronger diagnostic lane must therefore have
Graphics Tools installed and pass the native D3D12 suite with the variable above.

The canonical hello-triangle asset deliberately targets the WARP-compatible D3D12 SM 6.2 cook profile. Recreate it through the repository cook tool, never by editing bytecode in place:

```powershell
dotnet run --project tools\SomeEngine.AssetCook\SomeEngine.AssetCook.csproj -- shader assets/Shaders/hello_triangle.slang --profile d3d12-sm6.2
```


## No-root CAAS fallback

When `sudo apt-get install dotnet-sdk-10.0` is blocked, use the Microsoft APT mirror as a package source and extract the `.deb` payloads into a user-writable prefix:

```bash
# The final checkpoint includes global.json pinned to 10.0.301.
# Use the CAAS Artifactory APT mirror and download:
#   dotnet-host_10.0.9
#   dotnet-hostfxr-10.0_10.0.9
#   dotnet-runtime-deps-10.0_10.0.9
#   dotnet-runtime-10.0_10.0.9
#   aspnetcore-runtime-10.0_10.0.9
#   dotnet-targeting-pack-10.0_10.0.9
#   aspnetcore-targeting-pack-10.0_10.0.9
#   dotnet-apphost-pack-10.0_10.0.9
#   netstandard-targeting-pack-2.1
#   dotnet-sdk-10.0_10.0.301
#
# Extract each package with:
dpkg-deb -x package.deb /mnt/data/dotnet10
export DOTNET_ROOT=/mnt/data/dotnet10/usr/share/dotnet
export PATH="$DOTNET_ROOT:$PATH"
```

This is what the restricted container checkpoint used. It avoids writing to `/usr` and avoids the slow public `dotnet-install.sh` path when public DNS is unavailable.
