<#
	Packages the foremantasklist mod once per supported Factorio version.

	Factorio compares info.json's factorio_version against its own major.minor exactly, so one zip
	cannot cover both 2.0 and 2.1. We therefore ship one zip per Factorio version; Factorio loads the
	newest mod version whose factorio_version matches the running game, so dropping all of them into
	the mods folder works on any supported install.

	To support a new Factorio version, add a row to $Targets - the mod versions must ascend in step
	with the Factorio versions, otherwise an older game would win the newest mod version.
#>
param(
	[Parameter(Mandatory = $true)][string]$ProjectDir,
	[Parameter(Mandatory = $true)][string]$TargetDir
)

$ErrorActionPreference = 'Stop'

$Targets = [ordered]@{
	'2.0' = '1.4.0'
	'2.1' = '1.5.0'
}

$modSource = Join-Path $ProjectDir 'Mods\foremantasklist'
$modsDir = Join-Path $ProjectDir 'Mods'
$outputDir = Join-Path $TargetDir 'Mods'

$rawInfo = Get-Content (Join-Path $modSource 'info.json') -Raw
$modName = ($rawInfo | ConvertFrom-Json).name

if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir -Force | Out-Null }

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

foreach ($factorioVersion in $Targets.Keys) {
	$modVersion = $Targets[$factorioVersion]
	$folderName = $modName + '_' + $modVersion
	$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('foremantasklist_' + [guid]::NewGuid().ToString('N'))
	$stagedMod = Join-Path $staging $folderName

	try {
		New-Item -ItemType Directory -Path $stagedMod -Force | Out-Null
		Copy-Item (Join-Path $modSource 'control.lua') $stagedMod
		Copy-Item (Join-Path $modSource 'data.lua') $stagedMod

		# text replace rather than a json round trip so nothing else in the file shifts
		$info = $rawInfo -replace '(?<!factorio_)"version"\s*:\s*"[^"]*"', ('"version": "' + $modVersion + '"')
		$info = $info -replace '"factorio_version"\s*:\s*"[^"]*"', ('"factorio_version": "' + $factorioVersion + '"')
		[System.IO.File]::WriteAllText((Join-Path $stagedMod 'info.json'), $info, $utf8NoBom)

		$zipPath = Join-Path $modsDir ($folderName + '.zip')
		Compress-Archive -Path $stagedMod -DestinationPath $zipPath -Force
		Copy-Item $zipPath $outputDir -Force
		Write-Host ('packaged ' + $folderName + '.zip for Factorio ' + $factorioVersion)
	}
	finally {
		if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
	}
}
