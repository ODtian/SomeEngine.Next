param(
    [string]$HostName = '4090',
    [ValidatePattern('^[a-zA-Z0-9._/-]+$')]
    [string]$RemoteDirectory = '.cache/someengine-benchmark-queue'
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'BenchmarkQueue/BenchmarkQueue.csproj'
$output = Join-Path $PSScriptRoot 'BenchmarkQueue/bin/remote'
& dotnet publish $project -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:BaseOutputPath=bin/ResourceQueue/ -o $output -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$setup = @'
set -eu
root="$HOME/__DIRECTORY__"
mkdir -p "$root/install"
cd "$root/install"
if [ ! -x "$root/fuse2fs" ]; then
    apt download fuse2fs
    dpkg-deb -x ./*.deb extracted
    cp extracted/usr/bin/fuse2fs "$root/fuse2fs"
fi
cd "$root"
rm -rf "$root/install"
mkdir -p "$root/tools"
if [ ! -f "$root/tools.img" ]; then
    truncate -s 2G "$root/tools.img"
    mke2fs -q -t ext4 -O ^has_journal -F -m 0 "$root/tools.img"
fi
if ! mountpoint -q "$root/tools"; then
    "$root/fuse2fs" -o fakeroot "$root/tools.img" "$root/tools"
    for attempt in $(seq 1 50); do
        if mountpoint -q "$root/tools"; then break; fi
        sleep 0.1
    done
    mountpoint -q "$root/tools"
fi
if [ ! -x "$root/tools/dotnet/dotnet" ]; then
    mkdir -p "$root/tools/temporary"
    export TMPDIR="$root/tools/temporary"
    curl --connect-timeout 15 --max-time 60 -fsSL https://dot.net/v1/dotnet-install.sh -o "$root/tools/install.sh"
    bash "$root/tools/install.sh" --version 10.0.112 --install-dir "$root/tools/dotnet" --no-path
    rm -rf "$root/tools/temporary" "$root/tools/install.sh"
fi
'@.Replace('__DIRECTORY__', $RemoteDirectory)
$setup | & ssh -o BatchMode=yes -o ConnectTimeout=15 $HostName "tr -d '\r' | bash"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& scp -o BatchMode=yes -o ConnectTimeout=15 (Join-Path $output 'SomeEngine.CommandQueue') "${HostName}:${RemoteDirectory}/queue.next"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& ssh -o BatchMode=yes -o ConnectTimeout=15 $HostName "chmod +x '$RemoteDirectory/queue.next' && mv '$RemoteDirectory/queue.next' '$RemoteDirectory/queue'"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$settingsDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SomeEngine/BenchmarkQueue'
New-Item -ItemType Directory -Path $settingsDirectory -Force | Out-Null
$settingsPath = Join-Path $settingsDirectory 'settings.json'
$settings = if (Test-Path -LiteralPath $settingsPath) { Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
$settings.RemoteHost = $HostName
$settings.RemoteDirectory = $RemoteDirectory
$settings | ConvertTo-Json | Set-Content -LiteralPath $settingsPath
Write-Output "Remote queue configured: $HostName ($RemoteDirectory); settings: $settingsPath"
