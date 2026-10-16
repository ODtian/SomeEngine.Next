param(
    [string]$SymbolsPath,
    [string]$NativeDirectory,
    [string]$LlvmBinDirectory
)

$lines = Get-Content -LiteralPath $SymbolsPath
$methods = @{}
$positions = @{}
$symbol = ''
foreach ($line in $lines) {
    if ($line.StartsWith('{')) {
        $entry = $line | ConvertFrom-Json
        if ($entry.Kind -eq 'MethodCode') { $methods[$entry.Name] = $entry.Bytes }
    }
    if ($line -match 'S_PUB32 .*`([^`]*)`') { $symbol = $Matches[1] }
    if ($line -match 'addr = 0001:(\d+)') { $positions[$symbol] = [ulong]$Matches[1] }
    if ($line -match '^\s*0\s+\.text\s+[0-9a-f]+\s+([0-9a-f]+)') {
        $codeAddress = [Convert]::ToUInt64($Matches[1], 16)
    }
}

$bodyFilter = 'CodeGenerationChecks__BatchEntry<|CodeGenerationChecks__ReferenceFieldEntry|ScalarEasing|IMotion_4<Single|Spring_2<Single.*__Evaluate$|Channel_1<Single>__WriteBatch|Channel_1<Single>__Write(Roots|Fields)<.*CodeGenerationChecks_FieldRoot|World__ExecuteComponentWrite<.*CodeGenerationChecks_FieldRoot|World__IsEnabled<.*Timeline>|Owners_Components__IsEnabled|RuntimeType.*get_Name|SystemFunctions_2<System___Canon.*UpdateSystem|SystemFunctions_2<SomeEngine_Animation_SomeEngine_Animation_TimelineSystem.*UpdateSystem|SystemGroup_1.*UpdateCore'
$executable = Join-Path $NativeDirectory 'SomeEngine.Animation.Checks.exe'
foreach ($name in ($methods.Keys | Sort-Object)) {
    if ($name -notmatch $bodyFilter -or -not $positions.ContainsKey($name)) { continue }
    $start = $codeAddress + $positions[$name]
    $end = $start + $methods[$name]
    Write-Output ('BODY {0} bytes={1} address=0x{2:X}' -f $name, $methods[$name], $start)
    & (Join-Path $LlvmBinDirectory 'llvm-objdump.exe') --disassemble --x86-asm-syntax=intel ('--start-address=0x{0:X}' -f $start) ('--stop-address=0x{0:X}' -f $end) $executable
}
foreach ($name in ($positions.Keys | Where-Object { $_ -match 'RhpBox|RhpNewFast' } | Sort-Object)) {
    Write-Output ('TARGET {0} address=0x{1:X}' -f $name, ($codeAddress + $positions[$name]))
}
