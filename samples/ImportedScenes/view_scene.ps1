$application = Join-Path $PSScriptRoot '../../templates/DefaultProject/Game.App/bin/Release/net10.0/Game.App.exe'
if (!(Test-Path -LiteralPath $application)) { throw 'Build DefaultProject/Game.App in Release first.' }
if ($args.Count) { throw 'Choose Sponza or Amazon Bistro in the in-game scene panel; startup options are no longer used.' }
& $application
