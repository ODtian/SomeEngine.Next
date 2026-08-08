function Resolve-AotEvidenceDestination {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string] $Evidence
    )

    $normalizedRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    $evidencePath = [IO.Path]::GetFullPath($Evidence, $normalizedRoot)
    if (Test-Path -LiteralPath $evidencePath -PathType Container) {
        throw "NativeAOT evidence must name a file, not directory '$evidencePath'."
    }

    $repositoryPrefix = $normalizedRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $evidenceIsInsideRepository = $evidencePath.StartsWith(
        $repositoryPrefix,
        [StringComparison]::OrdinalIgnoreCase)
    if (-not $evidenceIsInsideRepository) {
        return $evidencePath
    }

    $relativeEvidencePath = [IO.Path]::GetRelativePath(
        $normalizedRoot,
        $evidencePath).Replace('\', '/')

    & git -C $normalizedRoot ls-files --error-unmatch -- $relativeEvidencePath 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        throw "NativeAOT evidence cannot overwrite tracked repository file '$relativeEvidencePath'."
    }

    & git -C $normalizedRoot check-ignore -q -- $relativeEvidencePath
    if ($LASTEXITCODE -ne 0) {
        throw "NativeAOT evidence inside the repository must use a git-ignored artifact path: '$relativeEvidencePath'."
    }

    return $evidencePath
}
