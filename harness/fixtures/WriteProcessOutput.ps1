param(
    [int]$LineCount = 4096,
    [int]$ExitCode = 0,
    [string]$StreamId = "fixture"
)

$payload = "x" * 256
for ($index = 0; $index -lt $LineCount; $index++) {
    [Console]::Out.WriteLine("stdout-$StreamId-$index-$payload")
    [Console]::Error.WriteLine("stderr-$StreamId-$index-$payload")
}

exit $ExitCode
