param(
    [string]$NativeDirectory,
    [string]$MapPath,
    [string]$LlvmBinDirectory
)

$methodFilter = 'MotionDispatcher.*Evaluate|IMotion.*EvaluateBatch|Spring.*Evaluate|HistoryMotion.*Evaluate|FunctionTween.*Evaluate|CodeGenerationChecks.*BatchEntry|CodeGenerationChecks.*ReferenceFieldEntry|Channel.*WriteRoots|Channel.*WriteFields|Channel.*WriteBatch|World.*ExecuteComponentWrite|SystemFunctions.*UpdateSystem|SystemGroup.*UpdateCore'
$reader = [System.Xml.XmlReader]::Create($MapPath)
try {
    while ($reader.Read()) {
        if ($reader.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
        $nodeKind = $reader.Name
        $nodeName = $reader.GetAttribute('Name')
        if (($nodeKind -eq 'MethodCode' -and $nodeName -match $methodFilter) -or
            ($nodeKind -eq 'NonGCStatics' -and $nodeName -match 'MotionDispatcher|SystemFunctions|MotionStorage')) {
            [pscustomobject]@{ Kind = $nodeKind; Name = $nodeName; Bytes = [int]$reader.GetAttribute('Length') } |
                ConvertTo-Json -Compress
        }
    }
}
finally { $reader.Dispose() }

$pdb = Join-Path $NativeDirectory 'SomeEngine.Animation.Checks.pdb'
& (Join-Path $LlvmBinDirectory 'llvm-pdbutil.exe') dump --publics $pdb |
    Select-String -Pattern $methodFilter -Context 0, 2 |
    ForEach-Object { $_.Line; $_.Context.PostContext }
& (Join-Path $LlvmBinDirectory 'llvm-objdump.exe') --section-headers (Join-Path $NativeDirectory 'SomeEngine.Animation.Checks.exe')
