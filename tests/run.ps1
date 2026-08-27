<#
    Run every harness. One command, so there is no "which ones did I remember to run".

        tests\run.ps1           # everything
        tests\run.ps1 js        # just the JavaScript harnesses
        tests\run.ps1 cs        # just the C# ones

    The C# harnesses reference bin\Release\net9.0\Jellyfin.Profiles.dll, so the plugin is
    built first unless $env:SKIP_BUILD is set.
#>
param([string]$Which = 'all')

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

$failed = New-Object System.Collections.Generic.List[string]
$ran = 0

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
        Get-ChildItem (Join-Path $root 'tests\js\*.js') |
            Where-Object { $_.Name -notlike '_*' -and $_.Name -notlike '*.verify.js' } |
            ForEach-Object {
                $p = $_.FullName
                Invoke-Harness ($_.BaseName) { node $p }
            }
        Write-Host ''
    }

    if ($Which -eq 'all' -or $Which -eq 'cs') {
        Write-Host '-- C# --------------------------------------------------------'
        Get-ChildItem (Join-Path $root 'tests\cs') -Directory | ForEach-Object {
            $p = $_.FullName
            Invoke-Harness ($_.Name) { dotnet run --project $p -c Release --nologo }
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
