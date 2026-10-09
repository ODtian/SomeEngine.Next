#requires -Version 7.2
<#
Checks only the source/destination pairs in a tab-separated dependency list.
The script never changes files. JSON is written to stdout; exit 1 means a check failed.
A prior JSON report also checks that valid links were not rewritten when their
source path, length and modification time have not changed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ManifestPath,
    [string] $PreviousReport
)

$ErrorActionPreference = 'Stop'
$pathComparer = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
$previous = [Collections.Generic.Dictionary[string, object]]::new($pathComparer)
$pairs = [Collections.Generic.Dictionary[string, string]]::new($pathComparer)
$errors = [Collections.Generic.List[string]]::new()
$rows = [Collections.Generic.List[object]]::new()

function Get-FullPath([string] $Value) {
    if (-not [IO.Path]::IsPathFullyQualified($Value)) {
        throw [ArgumentException]::new("An absolute path is required: $Value")
    }
    return [IO.Path]::GetFullPath($Value)
}

try {
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $ManifestPath -Encoding utf8) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $fields = $line.Split([char] 9)
        if ($fields.Count -ne 2) {
            throw [FormatException]::new("Line $lineNumber must contain source, tab, destination.")
        }
        $source = Get-FullPath $fields[0]
        $destination = Get-FullPath $fields[1]
        if ($pairs.ContainsKey($destination)) {
            if (-not $pathComparer.Equals($pairs[$destination], $source)) {
                throw [FormatException]::new("Line $lineNumber gives a second source for $destination")
            }
            continue
        }
        $pairs.Add($destination, $source)
    }
    if ($pairs.Count -eq 0) { $errors.Add('EmptyDependencyList') }
    if ($PreviousReport) {
        $before = Get-Content -LiteralPath $PreviousReport -Raw -Encoding utf8 | ConvertFrom-Json
        if ($before.FormatVersion -ne 1) {
            throw [FormatException]::new('The previous report has an unsupported format.')
        }
        foreach ($entry in $before.Entries) {
            # PowerShell versions can deserialize ISO timestamps as DateTime.
            # Keep comparisons in the same UTC round-trip form as this report.
            foreach ($name in @('SourceWriteUtc', 'LinkCreatedUtc', 'LinkWriteUtc')) {
                if ($entry.$name -is [DateTime]) {
                    $entry.$name = $entry.$name.ToUniversalTime().ToString('O')
                }
                elseif ($entry.$name -is [DateTimeOffset]) {
                    $entry.$name = $entry.$name.UtcDateTime.ToString('O')
                }
            }
            $previous.Add((Get-FullPath $entry.DestinationPath), $entry)
        }
    }
}
catch {
    $errors.Add('InvalidInput: ' + $_.Exception.Message)
}

if ($errors.Count -eq 0) {
    foreach ($pair in $pairs.GetEnumerator()) {
        $problems = [Collections.Generic.List[string]]::new()
        $row = [ordered]@{
            SourcePath = $pair.Value
            DestinationPath = $pair.Key
            SourceResolvedPath = $null
            SourceLength = $null
            SourceWriteUtc = $null
            LinkType = $null
            LinkTarget = $null
            ResolvedTarget = $null
            LinkCreatedUtc = $null
            LinkWriteUtc = $null
            PreviousLinkType = $null
            SourceUnchanged = $null
            LinkUnchanged = $null
            Passed = $false
            Errors = @()
        }
        try {
            $source = Get-Item -LiteralPath $pair.Value -Force
            if ($source.PSIsContainer) { throw [IO.IOException]::new('Source is a directory.') }
            if ($source.LinkType -eq 'SymbolicLink') {
                $source = $source.ResolveLinkTarget($true)
            }
            if ($null -eq $source -or -not $source.Exists) {
                throw [IO.FileNotFoundException]::new('Source does not resolve to a file.')
            }
            $row.SourceResolvedPath = Get-FullPath $source.FullName
            $row.SourceLength = $source.Length
            $row.SourceWriteUtc = $source.LastWriteTimeUtc.ToString('O')
        }
        catch {
            $problems.Add('SourceReadFailed: ' + $_.Exception.Message)
        }
        try {
            $link = Get-Item -LiteralPath $pair.Key -Force
            $row.LinkType = if ($link.PSIsContainer) { 'Directory' }
                elseif ($link.LinkType) { [string] $link.LinkType } else { 'File' }
            $row.LinkCreatedUtc = $link.CreationTimeUtc.ToString('O')
            $row.LinkWriteUtc = $link.LastWriteTimeUtc.ToString('O')
            if ($row.LinkType -ne 'SymbolicLink') {
                $problems.Add('NotSymbolicLink')
            }
            else {
                $row.LinkTarget = [string] $link.LinkTarget
                $target = $link.ResolveLinkTarget($true)
                if ($null -eq $target -or -not $target.Exists -or $target -is [IO.DirectoryInfo]) {
                    $problems.Add('LinkTargetMissing')
                }
                else {
                    $row.ResolvedTarget = Get-FullPath $target.FullName
                    if (-not $pathComparer.Equals($row.ResolvedTarget, $row.SourceResolvedPath)) {
                        $problems.Add('WrongLinkTarget')
                    }
                }
            }
        }
        catch {
            $problems.Add('DestinationReadFailed: ' + $_.Exception.Message)
        }
        if ($previous.ContainsKey($pair.Key)) {
            $old = $previous[$pair.Key]
            $row.PreviousLinkType = $old.LinkType
            $row.SourceUnchanged =
                $pathComparer.Equals($old.SourcePath, $row.SourcePath) -and
                $pathComparer.Equals($old.SourceResolvedPath, $row.SourceResolvedPath) -and
                $old.SourceLength -eq $row.SourceLength -and
                $old.SourceWriteUtc -ceq $row.SourceWriteUtc
            if ($row.SourceUnchanged -and $old.Passed -and $old.LinkType -eq 'SymbolicLink') {
                $row.LinkUnchanged =
                    $old.LinkCreatedUtc -ceq $row.LinkCreatedUtc -and
                    $old.LinkWriteUtc -ceq $row.LinkWriteUtc -and
                    $pathComparer.Equals($old.ResolvedTarget, $row.ResolvedTarget)
                if (-not $row.LinkUnchanged) { $problems.Add('UnchangedSourceLinkWasRewritten') }
            }
        }
        $row.Passed = $problems.Count -eq 0
        $row.Errors = $problems.ToArray()
        $rows.Add([pscustomobject] $row)
    }
}

$passed = $errors.Count -eq 0 -and @($rows | Where-Object { -not $_.Passed }).Count -eq 0
[ordered]@{
    FormatVersion = 1
    CheckedUtc = [DateTime]::UtcNow.ToString('O')
    Passed = $passed
    DependencyCount = $pairs.Count
    Errors = $errors.ToArray()
    Entries = $rows.ToArray()
} | ConvertTo-Json -Depth 6

if (-not $passed) { exit 1 }
