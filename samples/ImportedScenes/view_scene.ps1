$application = Join-Path $PSScriptRoot '../../samples/DefaultGame/bin/game/Release/net10.0/DefaultGame.exe'
if (!(Test-Path -LiteralPath $application)) { throw 'Publish samples/DefaultGame in Release first.' }
if ($args.Count) { throw 'Choose Sponza or Amazon Bistro in the in-game scene panel; startup options are no longer used.' }
& $application
