<#
	Loads a Foreman preset through the real DataCache with no window at all, and prints a
	deterministic summary meant for diffing.

	Why this exists: Foreman.sln holds a single WinForms project and no tests, so a solver or preset
	change is otherwise only verifiable by opening the GUI and reading numbers off the screen. This
	script is the outside-the-process route - it compiles tools\HarnessMain.cs against the BUILT
	Foreman.exe and runs it in a scratch folder, so the real Presets folder is never touched.

	Foreman.exe is a WinExe and has no stdout attached, which is why the harness is its own console
	exe rather than a flag on Foreman itself.

	Usage:
	    tools\run-harness.ps1 -Preset "Factorio 2.1 Pyanodons 3.0"
	    tools\run-harness.ps1 -Preset "Factorio 2.0 Space Age" -Modules -OutFile before.txt
	    tools\run-harness.ps1 -Preset synthetic -PresetDir C:\scratch

	Diffing a change:
	    tools\run-harness.ps1 -Preset X -Modules -OutFile before.txt
	    ...rebuild / re-import...
	    tools\run-harness.ps1 -Preset X -Modules -OutFile after.txt
	    git diff --no-index before.txt after.txt

	Build Foreman first - this only references the output, it does not build it.
#>
param(
	[Parameter(Mandatory = $true)][string]$Preset,
	[ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
	[switch]$Modules,
	# Where to look for <Preset>.pjson. Defaults to the build's Presets folder; point it elsewhere
	# to run a preset held outside the install, leaving the real Presets folder untouched.
	[string]$PresetDir,
	[string]$OutFile
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$binDir = Join-Path $repoRoot "Foreman\bin\x64\$Configuration"
$harnessSource = Join-Path $PSScriptRoot 'HarnessMain.cs'

# The Framework64 csc is C# 5 only, so use the Roslyn compiler that ships with Visual Studio.
$csc = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe'

foreach ($required in @($csc, $harnessSource, (Join-Path $binDir 'Foreman.exe'))) {
	if (-not (Test-Path $required)) { throw "missing: $required" }
}

# Only the .pjson is needed; the matching .json is a per-preset enabled-objects file when present.
$presetsDir = if ($PresetDir) { $PresetDir } else { Join-Path $binDir 'Presets' }
$presetJson = Join-Path $presetsDir "$Preset.pjson"
if (-not (Test-Path $presetJson)) { throw "no such preset: $presetJson" }

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('foreman-harness_' + [guid]::NewGuid().ToString('N'))

try {
	New-Item -ItemType Directory -Path (Join-Path $scratch 'Presets') -Force | Out-Null

	# google-ortools-native.dll is loaded by Google.OrTools at runtime, so it has to travel too.
	foreach ($dll in @('Foreman.exe', 'Newtonsoft.Json.dll', 'Google.OrTools.dll',
			'Google.Protobuf.dll', 'google-ortools-native.dll')) {
		Copy-Item (Join-Path $binDir $dll) $scratch
	}

	Copy-Item $presetJson (Join-Path $scratch 'Presets')
	$presetMeta = Join-Path $presetsDir "$Preset.json"
	if (Test-Path $presetMeta) { Copy-Item $presetMeta (Join-Path $scratch 'Presets') }

	$harnessExe = Join-Path $scratch 'Harness.exe'
	$cscArgs = @(
		'/nologo', '/platform:x64', '/optimize+', "/out:$harnessExe",
		"/r:$(Join-Path $scratch 'Foreman.exe')",
		"/r:$(Join-Path $scratch 'Newtonsoft.Json.dll')",
		'/r:System.dll', '/r:System.Core.dll', '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll',
		$harnessSource
	)
	& $csc @cscArgs
	if ($LASTEXITCODE -ne 0) { throw "harness failed to compile (csc exit $LASTEXITCODE)" }

	$harnessArgs = @($Preset)
	if ($Modules) { $harnessArgs += '--modules' }

	# DataCache logs "Removal of Recipe ... no assemblers" lines to stderr while filtering; that is
	# normal and is kept out of the report so the diff stays clean.
	$report = & $harnessExe @harnessArgs 2>$null
	if ($LASTEXITCODE -ne 0) { throw "harness exited $LASTEXITCODE" }

	if ($OutFile) {
		$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
		[System.IO.File]::WriteAllText($OutFile, ($report -join "`n") + "`n", $utf8NoBom)
		Write-Host "wrote $OutFile"
	} else {
		$report
	}
} finally {
	if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue }
}
