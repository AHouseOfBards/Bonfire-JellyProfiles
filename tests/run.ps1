<#
    Run every harness. One command, so there is no "which ones did I remember to run".

        tests\run.ps1           # everything
        tests\run.ps1 js        # just the JavaScript harnesses
        tests\run.ps1 cs        # just the C# ones, against the net9.0 build that ships
        tests\run.ps1 cs10      # the same set, against the net10.0 build

    The plugin multi-targets net9.0 and net10.0 — Jellyfin 10.11.x runs on .NET 9 and 12.0
    on .NET 10. net9.0 is what ships, because it is the only one of the two that loads on
    both servers, so `all` runs the C# set against that one. `cs10` is what proves the
    other build is not merely compiling.

    The C# harnesses reference bin\Release\<tfm>\Jellyfin.Profiles.dll, so the plugin is
    built first unless $env:SKIP_BUILD is set.
#>
param([string]$Which = 'all')

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

$failed = New-Object System.Collections.Generic.List[string]
$ran = 0

# The framework the C# harnesses load the plugin from. They set their own TargetFramework
# from it too, because a net9 harness cannot load a net10 assembly.
$tfm = if ($Which -eq 'cs10') { 'net10.0' } else { 'net9.0' }

function Invoke-Harness {
    param([string]$Label, [scriptblock]$Body)
    Write-Host ("  {0,-14} " -f $Label) -NoNewline
    $out = & $Body 2>&1
    $script:ran++
    if ($LASTEXITCODE -eq 0) {
        $tail = ($out | Select-Object -Last 1)
        if ($null -ne $tail) { Write-Host ([string]$tail).Trim() } else { Write-Host 'ok' }
    } else {
        $script:failed.Add($Label)
        Write-Host 'FAILED'
        $out | Select-Object -Last 25 | ForEach-Object { Write-Host ('        ' + $_) }
    }
}

try {
    if ($Which -ne 'js') {
        Write-Host '-- Building the plugin (Release) ------------------------------'
        if (-not $env:SKIP_BUILD) {
            dotnet build -c Release -warnaserror --nologo -v q | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Host '  build FAILED'
                dotnet build -c Release -warnaserror --nologo -v q
                exit 1
            }
        }
        Write-Host '  ok'
        Write-Host ''
    }

    if ($Which -eq 'all' -or $Which -eq 'js') {
        Write-Host '-- JavaScript ------------------------------------------------'
        # _lib.js is shared plumbing. *.verify.js needs the network and refreshes what the
        # offline checks compare against — it is a maintenance tool, not a gate.
        # *.scan.js surveys the source and prints what it finds; it asserts nothing and
        # always exits 0, so counting it would add a harness that can never go red.
        Get-ChildItem (Join-Path $root 'tests\js\*.js') |
            Where-Object { $_.Name -notlike '_*' -and $_.Name -notlike '*.verify.js' -and $_.Name -notlike '*.scan.js' } |
            ForEach-Object {
                $p = $_.FullName
                Invoke-Harness ($_.BaseName) { node $p }
            }
        Write-Host ''
    }

    if ($Which -eq 'all' -or $Which -eq 'cs' -or $Which -eq 'cs10') {
        Write-Host "-- C# (against the $tfm build) -------------------------------"
        Get-ChildItem (Join-Path $root 'tests\cs') -Directory | ForEach-Object {
            $p = $_.FullName
            Invoke-Harness ($_.Name) {
                dotnet run --project $p -c Release --nologo "-p:BonfireTfm=$tfm"
            }
        }
        Write-Host ''
    }

    Write-Host '--------------------------------------------------------------'
    if ($failed.Count -eq 0) {
        Write-Host "  $ran harnesses, all green."
        exit 0
    }
    Write-Host "  $ran harnesses, $($failed.Count) failed: $($failed -join ', ')"
    exit 1
}
finally {
    Pop-Location
}
