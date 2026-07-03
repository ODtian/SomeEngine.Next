# Human shortcuts only. Harness policy lives in harness/RunHarness.ps1.
# Automation and skills should call harness/RunHarness.ps1 directly.

# Default human shortcut.
default: harness

harness run_id='':
    pwsh -NoProfile -ExecutionPolicy Bypass -File harness/RunHarness.ps1 -RunId '{{run_id}}'

hard run_id='':
    pwsh -NoProfile -ExecutionPolicy Bypass -File harness/RunHarness.ps1 -Mode Hard -RunId '{{run_id}}'

warning run_id='':
    pwsh -NoProfile -ExecutionPolicy Bypass -File harness/RunHarness.ps1 -Mode Warning -RunId '{{run_id}}'

build:
    dotnet build SomeEngine.slnx
