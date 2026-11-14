<#
    Run every harness. One command, so there is no "which ones did I remember to run".

        tests\run.ps1           # everything
        tests\run.ps1 js        # just the JavaScript harnesses
        tests\run.ps1 cs        # just the C# ones, against the net9.0 build that ships
        tests\run.ps1 cs10      # the same set, against the net10.0 build
        tests\run.ps1 js --shard 2/6   # one slice, as CI runs it; anything after the
                                       # section is passed to run.sh as it is
        tests\run.ps1 --changed        # only the harnesses a change can affect (tests/depends.txt)
        tests\run.ps1 js --changed main   # ...against another base; also --record, --only a,b

    This hands over to tests/run.sh through Git Bash. It used to be a second copy of the
    runner, and a second copy is how the desk and the pipeline come to disagree about what
    "the tests pass" means: the parallel JavaScript run, the one-pass C# build, the
    one-full-run-at-a-time lock and the no-totals-line check would each have had to be
    written twice. BONFIRE_JOBS, BONFIRE_LOCK_WAIT and SKIP_BUILD are read by run.sh as
    they are here.

    Needs Git for Windows (bash). The WSL bash that ships in System32 is not used: it would
    run the tests inside Linux against a Windows checkout.
#>
param(
    [string]$Which = 'all',
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest = @()
)

$bash = $null
$candidates = @(
    (Join-Path $env:ProgramFiles 'Git\bin\bash.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Git\bin\bash.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Git\bin\bash.exe')
)
foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { $bash = $c; break } }
if (-not $bash) {
    $found = Get-Command bash.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.Source -notmatch '\\Windows\\System32\\' } | Select-Object -First 1
    if ($found) { $bash = $found.Source }
}
if (-not $bash) {
    Write-Host 'tests\run.ps1 needs Git for Windows (bash.exe) to run tests/run.sh. Install it, or run tests/run.sh from Git Bash.'
    exit 2
}

# `run.ps1 --changed` has no section: the first word is an option, so it goes to run.sh as
# one and the section stays "all".
if ($Which.StartsWith('-')) { $Rest = @($Which) + $Rest; $Which = 'all' }

$script = Join-Path $PSScriptRoot 'run.sh'
& $bash $script $Which @Rest
exit $LASTEXITCODE
