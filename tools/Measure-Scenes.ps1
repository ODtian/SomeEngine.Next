param(
    [switch]$Trace, [switch]$TraceDetail, [string]$ApplicationPath, [switch]$Managed,
    [string[]]$ApplicationArguments = @(),
    [ValidateSet('deferred', 'forward')][string]$ShadingPath,
    [ValidateSet('static', 'motion', 'materials', 'camera')][string]$ScenePlayback = 'camera',
    [string]$ExpectedScene,
    [ValidateRange(1, 120)][int]$StartupFrames,
    [ValidateNotNullOrEmpty()][string]$Scene,
    [Parameter(DontShow = $true)][switch]$FromBenchmarkQueue,
    [Parameter(DontShow = $true)][switch]$OnDefaultDesktop
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$IsWindows) { throw 'Scene measurements require the Windows desktop.' }
if ($TraceDetail -and !$Trace) { throw 'TraceDetail requires Trace.' }
if ($Scene -and $ExpectedScene -and $Scene -cne $ExpectedScene) {
    throw 'Scene and ExpectedScene must name the same scene when both are supplied.'
}
$playback = if ($ScenePlayback -eq 'static') { $null } else { $ScenePlayback }
# Ask the project for its normal output path rather than restoring the removed
# Game.App output convention or flattening main's runtime-identifier directories.
if ($ApplicationPath) {
    $application = [IO.Path]::GetFullPath($ApplicationPath)
} else {
    $project = Join-Path $repository 'samples/DefaultGame/DefaultGame.csproj'
    $aot = if ($Managed) { 'false' } else { 'true' }
    $outputText = & dotnet msbuild $project -nologo -p:Configuration=Release "-p:PublishAot=$aot" `
        '-getProperty:TargetDir,PublishDir,AssemblyName,UseAppHost'
    if ($LASTEXITCODE -ne 0) { throw 'Could not evaluate the DefaultGame output paths.' }
    $properties = ($outputText -join [Environment]::NewLine | ConvertFrom-Json).Properties
    if ($properties.UseAppHost -ne 'true') { throw 'Build DefaultGame with an application host before measuring.' }
    $directory = if ($Managed) { $properties.TargetDir } else { $properties.PublishDir }
    if (!$directory -or !$properties.AssemblyName) { throw 'The project did not provide its application output path.' }
    $directory = [IO.Path]::GetFullPath($directory, [IO.Path]::GetDirectoryName($project))
    $application = Join-Path $directory ($properties.AssemblyName + '.exe')
}
if (![IO.File]::Exists($application)) {
    throw 'Publish samples/DefaultGame in Release, or build Release with -p:PublishAot=false and use -Managed for diagnostics.'
}
# Reserved options have named script parameters so forwarding cannot silently
# override the measured scene, shading mode, trace mode or startup bound.
foreach ($argument in $ApplicationArguments) {
    if ($argument -match '^--(measure-scene|test-scene|shading-path|scene-playback|frames|trace-detail)(=|$)') {
        throw "Use the named measurement parameter instead of the reserved application option '$argument'."
    }
}
# The queue owns the full foreground measurement, including F9 and result validation.
# This private switch is used only by the payload submitted below to avoid requeueing.
if (!$FromBenchmarkQueue) {
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $queue = 'F:\SomeEngine.Next\tools\Invoke-Benchmark.ps1'
    $queueSession = $env:SOMEENGINE_BENCHMARK_SESSION ?? $env:CODEX_THREAD_ID ?? $env:CODEX_SESSION_ID ?? $repository
    if (![IO.File]::Exists($queue)) { throw "The shared benchmark queue is unavailable: $queue" }
    $pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop).Source
    $submit = [Diagnostics.ProcessStartInfo]::new($pwsh)
    $submit.WorkingDirectory = $repository
    $submit.UseShellExecute = $false
    $submit.CreateNoWindow = $true
    $submit.RedirectStandardOutput = $true
    $submit.RedirectStandardError = $true
    [void]$submit.Environment.Remove('SOMEENGINE_BENCHMARK_QUEUE')
    $queuedParameters = @{ FromBenchmarkQueue = $true; ApplicationPath = $application }
    foreach ($entry in $PSBoundParameters.GetEnumerator()) {
        if ($entry.Key -in @('FromBenchmarkQueue', 'OnDefaultDesktop', 'ApplicationPath')) { continue }
        $queuedParameters[$entry.Key] = if ($entry.Value -is [Management.Automation.SwitchParameter]) {
            [bool]$entry.Value.IsPresent
        } else { $entry.Value }
    }
    $data = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($queuedParameters | ConvertTo-Json -Depth 4 -Compress)))
    $script = $PSCommandPath.Replace("'", "''")
    $body = "`$ErrorActionPreference = 'Stop'; `$LASTEXITCODE = 0; try { `$parameters = ConvertFrom-Json -AsHashtable ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$data'))); & '$script' @parameters; if (!`$?) { exit 1 }; exit `$LASTEXITCODE } catch { [Console]::Error.WriteLine(`$_); exit 1 }"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body))
    $queuedArguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded)
    foreach ($argument in (@('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $queue, '--exclusive', '--session', $queueSession, $pwsh) + $queuedArguments)) {
        $submit.ArgumentList.Add($argument)
    }
    $submission = [Diagnostics.Process]::Start($submit)
    try {
        $submittedOutput = $submission.StandardOutput.ReadToEndAsync()
        $submittedErrors = $submission.StandardError.ReadToEndAsync()
        if (!$submission.WaitForExit(30000)) {
            $submission.Kill()
            throw 'Queue submission timed out. It may already be queued; do not submit a duplicate.'
        }
        $receiptText = $submittedOutput.GetAwaiter().GetResult()
        $submissionErrors = $submittedErrors.GetAwaiter().GetResult()
        if ($submission.ExitCode -ne 0) { throw "Queue submission failed: $submissionErrors $receiptText" }
    } finally { $submission.Dispose() }
    $receipt = ($receiptText -split '\r?\n' | Where-Object { $_.Trim() } | Select-Object -Last 1) | ConvertFrom-Json
    if ($receipt.Id -cnotmatch '^[a-f0-9]{32}$' -or $receipt.Kind -cne 'Exclusive' -or
        $receipt.SessionId -cne $queueSession) {
        throw 'The official queue did not return this exclusive measurement and session.'
    }
    $expectedOutput = Join-Path $repository ('artifacts/benchmark-queue/' + $receipt.Id)
    if ([IO.Path]::GetFullPath($receipt.OutputDirectory) -ne [IO.Path]::GetFullPath($expectedOutput)) {
        throw 'The queue returned an unexpected result directory.'
    }
    Write-Host "Queued $($receipt.Id); waiting for $($receipt.OutputDirectory)."
    $resultPath = Join-Path $receipt.OutputDirectory 'result.json'
    while (!(Test-Path -LiteralPath $resultPath)) { Start-Sleep -Seconds 1 }
    # The queue writes this small file once; tolerate observing that write in progress.
    for ($read = 0; $read -lt 10; $read++) {
        try {
            $completed = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json -ErrorAction Stop
            if ($null -ne $completed.ExitCode) { break }
        } catch { if ($read -eq 9) { throw } }
        Start-Sleep -Milliseconds 50
    }
    if ($completed.Id -cne $receipt.Id -or $completed.Kind -cne 'Exclusive' -or
        $completed.SessionId -cne $queueSession -or $null -eq $completed.ExitCode -or
        !$completed.Executable -or !$completed.Directory -or
        [IO.Path]::GetFullPath($completed.Executable) -ne [IO.Path]::GetFullPath($pwsh) -or
        [IO.Path]::GetFullPath($completed.Directory) -ne $repository -or
        @($completed.Arguments).Count -ne $queuedArguments.Count) {
        throw 'The queue result does not match this measurement.'
    }
    for ($index = 0; $index -lt $queuedArguments.Count; $index++) {
        if ($completed.Arguments[$index] -cne $queuedArguments[$index]) {
            throw 'The queue result arguments differ from the submitted measurement.'
        }
    }
    foreach ($name in @('stdout.log', 'stderr.log')) {
        $log = Join-Path $receipt.OutputDirectory $name
        if ([IO.File]::Exists($log)) {
            if ($name -eq 'stderr.log') { [Console]::Error.Write([IO.File]::ReadAllText($log)) }
            else { [Console]::Out.Write([IO.File]::ReadAllText($log)) }
        }
    }
    exit [int]$completed.ExitCode
}

# The shared worker can live on a tool desktop. Move only the hidden pwsh payload
# to Default; it remains a foreground child until the visible engine run finishes.
if ($IsWindows -and !$OnDefaultDesktop) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
public static class SceneDesktopProcess {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartInfo {
        public uint cb;
        public string lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ChildInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartInfo start, out ChildInfo child);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
        out IntPtr target, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static IntPtr Inherit(IntPtr handle) {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            throw new InvalidOperationException("The queue must provide redirected standard streams.");
        IntPtr current = GetCurrentProcess();
        if (!DuplicateHandle(current, handle, current, out IntPtr copy, 0, true, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return copy;
    }
    public static int Run(string executable, string encoded, string directory, uint timeoutMilliseconds) {
        StartInfo start = new StartInfo { cb = (uint)Marshal.SizeOf<StartInfo>(),
            lpDesktop = @"WinSta0\Default", dwFlags = 0x100 };
        ChildInfo child = default;
        using var input = File.OpenHandle("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try {
            start.hStdInput = Inherit(input.DangerousGetHandle());
            start.hStdOutput = Inherit(GetStdHandle(-11));
            start.hStdError = Inherit(GetStdHandle(-12));
            var command = new StringBuilder("\"" + executable + "\" -NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded);
            // CREATE_NO_WINDOW applies to pwsh only. The engine creates its own visible window.
            if (!CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000,
                IntPtr.Zero, directory, ref start, out child)) throw new Win32Exception(Marshal.GetLastWin32Error());
            uint wait = WaitForSingleObject(child.Process, timeoutMilliseconds);
            if (wait == 0x102) {
                using var process = Process.GetProcessById((int)child.ProcessId);
                process.Kill(true);
                process.WaitForExit();
                throw new TimeoutException($"The Default desktop measurement exceeded {timeoutMilliseconds / 1000} seconds; its child tree was stopped.");
            }
            if (wait != 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GetExitCodeProcess(child.Process, out uint code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)code);
        } finally {
            if (child.Thread != IntPtr.Zero) CloseHandle(child.Thread);
            if (child.Process != IntPtr.Zero) CloseHandle(child.Process);
            if (start.hStdInput != IntPtr.Zero) CloseHandle(start.hStdInput);
            if (start.hStdOutput != IntPtr.Zero) CloseHandle(start.hStdOutput);
            if (start.hStdError != IntPtr.Zero) CloseHandle(start.hStdError);
        }
    }
}
'@
    # This new outer guard must not cap a multi-scene command at one scene's budget.
    # The engine and the original measurement body retain their own existing bounds.
    $sceneCount = if ($Scene -or $StartupFrames) { 1 } else {
        $configuration = Join-Path ([IO.Path]::GetDirectoryName($application)) 'test-scenes.json'
        @(Get-Content -LiteralPath $configuration -Raw | ConvertFrom-Json).Count
    }
    if ($sceneCount -lt 1) { throw 'The measurement configuration contains no scenes.' }
    [uint32]$desktopTimeoutMilliseconds = (30 + 60 * $sceneCount) * 1000
    $desktopParameters = @{ FromBenchmarkQueue = $true; OnDefaultDesktop = $true; ApplicationPath = $application }
    foreach ($entry in $PSBoundParameters.GetEnumerator()) {
        if ($entry.Key -in @('FromBenchmarkQueue', 'OnDefaultDesktop', 'ApplicationPath')) { continue }
        $desktopParameters[$entry.Key] = if ($entry.Value -is [Management.Automation.SwitchParameter]) {
            [bool]$entry.Value.IsPresent
        } else { $entry.Value }
    }
    $data = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($desktopParameters | ConvertTo-Json -Compress)))
    $script = $PSCommandPath.Replace("'", "''")
    $body = "`$ErrorActionPreference = 'Stop'; `$LASTEXITCODE = 0; try { `$parameters = ConvertFrom-Json -AsHashtable ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$data'))); & '$script' @parameters; if (!`$?) { exit 1 }; exit `$LASTEXITCODE } catch { [Console]::Error.WriteLine(`$_); exit 1 }"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body))
    $pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop).Source
    $desktopExitCode = [SceneDesktopProcess]::Run($pwsh, $encoded, [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')), $desktopTimeoutMilliseconds)
    exit $desktopExitCode
}

$compilation = if ($Managed) { 'Managed diagnostics' } else { 'NativeAOT' }
Write-Host "Compilation: $compilation; application: $application"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SceneTestKeys {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, UIntPtr key, IntPtr data);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@
$started = [DateTime]::UtcNow
$timer = [Diagnostics.Stopwatch]::StartNew()
$start = [Diagnostics.ProcessStartInfo]::new($application)
$start.UseShellExecute = $false
$start.WorkingDirectory = [IO.Path]::GetDirectoryName($application)
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
if ($Scene) {
    $start.ArgumentList.Add('--measure-scene')
    $start.ArgumentList.Add($Scene)
}
foreach ($argument in $ApplicationArguments) { $start.ArgumentList.Add($argument) }
if ($ShadingPath) {
    $start.ArgumentList.Add('--shading-path')
    $start.ArgumentList.Add($ShadingPath)
}
if ($playback) {
    $start.ArgumentList.Add('--scene-playback')
    $start.ArgumentList.Add($playback)
}
if ($StartupFrames) {
    $start.ArgumentList.Add('--frames')
    $start.ArgumentList.Add([string]$StartupFrames)
    $start.ArgumentList.Add('--graphics-backend')
    $start.ArgumentList.Add('d3d12')
    $start.ArgumentList.Add('--no-debug-ui')
}
if ($TraceDetail) { $start.ArgumentList.Add('--trace-detail') }
$flatDirectory = [Environment]::GetEnvironmentVariable('SOMEENGINE_MEASUREMENT_OUTPUT_DIRECTORY', 'Process')
$flatBasename = [Environment]::GetEnvironmentVariable('SOMEENGINE_MEASUREMENT_OUTPUT_BASENAME', 'Process')
$flatReport = $null
if ($flatDirectory -or $flatBasename) {
    if (!$flatDirectory -or !$flatBasename -or $flatBasename -cnotmatch '^[A-Za-z0-9_-]{1,96}$' -or
        ![IO.Path]::IsPathFullyQualified($flatDirectory) -or ![IO.Directory]::Exists($flatDirectory)) {
        throw 'Flat measurement output requires an existing absolute directory and an ASCII basename.'
    }
    $flatReport = Join-Path ([IO.Path]::GetFullPath($flatDirectory)) ($flatBasename + '-results.json')
    $flatSamples = Join-Path ([IO.Path]::GetFullPath($flatDirectory)) ($flatBasename + '-frame-samples.json')
    if ([IO.File]::Exists($flatReport) -or [IO.File]::Exists($flatSamples)) { throw 'Flat measurement output already exists.' }
}
if ($Trace) {
    if ($flatReport) {
        $startupTrace = Join-Path ([IO.Path]::GetFullPath($flatDirectory)) ($flatBasename + '-startup.setrace')
        if (Test-Path -LiteralPath $startupTrace) { throw 'Flat startup trace output already exists.' }
    } else {
        $startupTraces = Join-Path ([IO.Path]::GetDirectoryName($application)) 'Performance'
        [void][IO.Directory]::CreateDirectory($startupTraces)
        $startupTrace = Join-Path $startupTraces ('startup-' + [Guid]::NewGuid().ToString('N') + '.setrace')
    }
    $start.Environment['SOMEENGINE_STARTUP_TRACE'] = $startupTrace
} else {
    [void]$start.Environment.Remove('SOMEENGINE_STARTUP_TRACE')
}
if ($ShadingPath) {
    $shadingIndex = $start.ArgumentList.IndexOf('--shading-path')
    if ($shadingIndex -lt 0 -or $shadingIndex + 1 -ge $start.ArgumentList.Count -or
        $start.ArgumentList[$shadingIndex + 1] -cne $ShadingPath) {
        throw 'The native arguments do not contain the explicit requested shading path.'
    }
}
[Console]::Out.WriteLine('NATIVE_LAUNCH ' + ([ordered]@{
    Executable = $start.FileName; Arguments = @($start.ArgumentList)
} | ConvertTo-Json -Depth 3 -Compress))
$process = [Diagnostics.Process]::Start($start)
$output = $process.StandardOutput.ReadToEndAsync()
$errors = $process.StandardError.ReadToEndAsync()
$report = $null
if ($StartupFrames) {
    try {
        if (!$process.WaitForExit(60000)) {
            throw 'The short startup did not exit naturally within 60 seconds.'
        }
    } finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        [Console]::Out.Write($output.GetAwaiter().GetResult())
        [Console]::Error.Write($errors.GetAwaiter().GetResult())
        $startupExitCode = $process.ExitCode
        $process.Dispose()
    }
    if ($startupExitCode -ne 0) { throw "Short startup failed with exit code $startupExitCode." }
    Write-Host "Startup completed: scene '$Scene', requested frames $StartupFrames, exit 0."
    exit 0
}
try {
    while (!$process.HasExited -and $process.MainWindowHandle -eq 0 -and $timer.Elapsed.TotalSeconds -lt 55) {
        Start-Sleep -Milliseconds 50
        $process.Refresh()
    }
    if ($process.HasExited) { throw 'DefaultGame exited before creating its test window.' }
    $window = $process.MainWindowHandle
    if ($window -eq [IntPtr]::Zero) { throw 'DefaultGame did not create its test window before the startup bound.' }
    # Keep the measured client area above desktop tools at its configured size and position.
    [void][SceneTestKeys]::ShowWindowAsync($window, 5)
    [void][SceneTestKeys]::SetWindowPos($window, [IntPtr]::new(-1), 0, 0, 0, 0, 0x4043)
    $key = if ($Trace) { 0x79 } else { 0x78 }
    [void][SceneTestKeys]::PostMessage($window, 0x100, [UIntPtr]$key, [IntPtr]0)
    [void][SceneTestKeys]::PostMessage($window, 0x101, [UIntPtr]$key, [IntPtr]0)
    $reports = Join-Path ([IO.Path]::GetDirectoryName($application)) 'Performance'
    while (!$process.HasExited -and $timer.Elapsed.TotalSeconds -lt 57) {
        $report = if ($flatReport) {
            Get-Item -LiteralPath $flatReport -ErrorAction SilentlyContinue
        } else {
            Get-ChildItem -LiteralPath $reports -Filter results.json -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.LastWriteTimeUtc -ge $started } | Select-Object -First 1
        }
        if ($report) { break }
        Start-Sleep -Milliseconds 100
    }
    if (!$process.HasExited) {
        [void][SceneTestKeys]::PostMessage($process.MainWindowHandle, 0x10, [UIntPtr]0, [IntPtr]0)
        [void]$process.WaitForExit([Math]::Max(0, [int](59000 - $timer.Elapsed.TotalMilliseconds)))
    }
} finally {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $applicationOutput = $output.GetAwaiter().GetResult()
    $errorText = $errors.GetAwaiter().GetResult()
    if ($errorText) { Write-Host $errorText }
    $applicationExitCode = $process.ExitCode
    $process.Dispose()
}
if (!$report -and $flatReport) {
    $report = Get-Item -LiteralPath $flatReport -ErrorAction SilentlyContinue
} elseif (!$report -and (Test-Path -LiteralPath $reports)) {
    $report = Get-ChildItem -LiteralPath $reports -Filter results.json -Recurse |
        Where-Object { $_.LastWriteTimeUtc -ge $started } | Select-Object -First 1
}
if (!$report -or $applicationExitCode -ne 0) {
    Write-Host $applicationOutput
    throw 'The scene test did not finish successfully within 60 seconds. No complete performance result is available.'
}
if ($env:SOMEENGINE_VSM_INVALIDATION_TIMING -eq '1') { [Console]::Out.Write($applicationOutput) }
$result = Get-Content -LiteralPath $report.FullName -Raw | ConvertFrom-Json
if ($ShadingPath) {
    $expectedShadingPath = if ($ShadingPath -eq 'forward') { 'Forward' } else { 'Deferred' }
    if ($result.ShadingPath -cne $expectedShadingPath) {
        throw "The report shading path '$($result.ShadingPath)' does not match '$expectedShadingPath'."
    }
}
Write-Host "NativeAOT: $($result.NativeAot)"
$result.Scenes | Format-Table Scene, Status, Frames, MeanMs, MedianMs, P95Ms, PrepareMs, ExecuteMs
if ($result.Startup) {
    $result.Startup | Format-Table ApplicationToWindowMs, WindowToFirstFrameMs, ApplicationToFirstFrameMs
    $result.Startup.Stages | Format-Table Stage, ElapsedMs
}
Write-Host "Report: $($report.FullName) ($([Math]::Round($timer.Elapsed.TotalSeconds, 2)) seconds including application startup)"
if ($result.NativeAot -isnot [bool] -or $result.NativeAot -ne (!$Managed)) {
    throw 'The reported compilation mode does not match the requested NativeAOT or managed diagnostic run.'
}
if ($result.TraceEnabled -isnot [bool] -or $result.TraceEnabled -ne [bool]$Trace) {
    throw 'The reported trace mode does not match this measurement.'
}
if ($flatReport) {
    if ($result.FrameSamples -cne [IO.Path]::GetFileName($flatSamples) -or ![IO.File]::Exists($flatSamples)) {
        throw 'The flat measurement report is missing its matching frame samples.'
    }
    $null = Get-Content -LiteralPath $flatSamples -Raw | ConvertFrom-Json -ErrorAction Stop
}
$requiredScenes = if ($ExpectedScene) { @($ExpectedScene) } elseif ($Scene) { @($Scene) }
    else { @('1024 spheres', 'Sponza', 'Amazon Bistro') }
if (!$result.Complete -or $result.Scenes.Count -ne $requiredScenes.Count -or
    @(Compare-Object -CaseSensitive $requiredScenes @($result.Scenes.Scene)).Count -ne 0 -or
    @($result.Scenes | Where-Object Status -ne 'Measured').Count -ne 0) {
    if ($env:SOMEENGINE_VSM_INVALIDATION_TIMING -ne '1') { [Console]::Out.Write($applicationOutput) }
    throw 'The requested scene run is incomplete; do not use it as a passing performance comparison.'
}
if ($requiredScenes.Count -eq 1 -or $Managed -or $Trace) {
    Write-Host 'Diagnostic result only; this is not a passing three-scene performance comparison.'
}
