# Run the headless Unity gate in WSL, from Windows, without touching the Windows Editor.
#
# WHY THIS EXISTS. tools/ci/unity-verify.ps1 is the real Unity gate - it drives an Editor,
# compiles every assembly and runs HeadlessVerify's self-checks - but the Windows Editor is a
# SINGLE-WRITER resource (CLAUDE.md), so you cannot run it while one is open. unity-compile.ps1
# is the documented fallback for exactly that case, and it CANNOT run on a fresh clone: it needs
# Editor-generated Emberfall.*.csproj, and nothing in the pipeline produces them. Verified
# 2026-08-15: after a fully green headless run (Library built to 3.6 GB, 8/8 checks PASS) the
# project still had 0 csproj and 0 sln, and `-executeMethod UnityEditor.SyncVS.SyncSolution`
# resolved, ran in 0.132 ms and produced nothing - project generation is delegated to a
# CodeEditor package, and a headless box has none registered.
#
# So this script takes the other road. The WSL distro has its own Unity 6000.5.1f1 Linux Editor,
# which is a DIFFERENT Editor from the Windows one - nothing contends for it. Run the full gate
# there while your Editor stays busy.
#
# WHY NOT JUST COPY THE CSPROJS OVER. They are per-machine AND per-checkout. One generated
# Emberfall.Core.Contracts.csproj holds 155 absolute Windows paths: 141 into
# 'C:\Program Files\Unity\Hub\Editor\...', 13 into the specific worktree it was generated in, and
# one into a VS Code extension. None resolve in the distro, which has automount disabled by
# design and therefore no /mnt/c at all. Rewriting them is possible and is a trap: it rots on any
# Unity bump, extension bump or worktree move, and unity-compile.ps1's own header records what
# that failure looks like - "hundreds of type-not-found errors that look like real code breakage."
#
# HOW CODE GETS THERE. The distro sets automount=false and interop=false on purpose (see its
# /etc/wsl.conf, which carries a RETIREMENT TRIGGER telling you not to relax them). It cannot read
# the Windows filesystem or run Windows binaries. Windows -> WSL over stdin is the direction that
# file deliberately leaves open, so this ships a git bundle through it: a full one the first time,
# and an incremental one (`--not <what the checkout already has>`) every time after, which is
# usually kilobytes.
#
# Usage: pwsh tools/ci/unity-gate-wsl.ps1
#        pwsh tools/ci/unity-gate-wsl.ps1 -Ref worktree-effect-model-fork
#        pwsh tools/ci/unity-gate-wsl.ps1 -Fresh          # re-clone from scratch
#
# RETIREMENT TRIGGER: if unity-compile.ps1 ever gains a way to generate its own csprojs headless,
# the "WHY NOT JUST COPY" paragraph above and the -Fresh cold-import cost both stop being true.
[CmdletBinding()]
param(
    [string]$Ref         = 'HEAD',
    [string]$Distro      = 'agents',
    [string]$WslCheckout = '/home/agent/projects/ef-gate',
    [switch]$Fresh
)

$ErrorActionPreference = 'Stop'

# Path.Combine, never a literal '\' - same reason as the sibling scripts.
$REPO_ROOT = [System.IO.Path]::GetFullPath([System.IO.Path]::Combine($PSScriptRoot, '..', '..'))

function Invoke-Wsl {
    <#
      Runs a command in the distro and returns its stdout+stderr as string[].
      Always via `bash -c` with a single-quoted-safe payload written to a file when it matters -
      see Invoke-WslScript. Inline is fine for reads that do not report a number.
    #>
    param([string]$Command)
    return @(& wsl.exe -d $Distro -- bash -c $Command 2>&1 | ForEach-Object { $_.ToString() })
}

function Invoke-WslScript {
    <#
      Ships a shell script into the distro and runs it there.

      WHY NOT INLINE. A `$?` or `$VAR` sent inline through this hop can be expanded before bash
      ever sees it. That is not theoretical: on 2026-08-15 an inline `echo "EXIT=$?"` reported 0
      for a script whose source ends in `exit 1`, and an inline `$LOG` arrived empty, turning a
      grep into a read of empty stdin that returned a clean "0 matches" having read nothing.
      Anything whose value gets REPORTED must come from a real script file.
    #>
    param([string]$Body, [string]$RemotePath = '/tmp/unity-gate-wsl.sh')

    $tmp = [System.IO.Path]::GetTempFileName()
    # LF endings and no BOM: bash will not run a script with CRLF or a leading BOM.
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($tmp, ($Body -replace "`r`n", "`n"), $utf8NoBom)
    try {
        Copy-ToWsl -LocalPath $tmp -RemotePath $RemotePath
        return Invoke-Wsl "bash $RemotePath"
    } finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
}

function Copy-ToWsl {
    <#
      Streams a local file into the distro over stdin.

      -RedirectStandardInput takes a PATH and lets the OS do the copy, which matters: piping a
      713 MB bundle through PowerShell's own `Get-Content -AsByteStream` did not finish in ten
      minutes, while this shifts the same file in about ninety seconds.
    #>
    param([string]$LocalPath, [string]$RemotePath)

    $p = Start-Process -FilePath 'wsl.exe' `
        -ArgumentList @('-d', $Distro, '--', 'bash', '-c', "cat > '$RemotePath'") `
        -RedirectStandardInput $LocalPath -NoNewWindow -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "Failed to copy '$LocalPath' into ${Distro}:${RemotePath} (exit $($p.ExitCode))." }

    $localSize  = (Get-Item $LocalPath).Length
    $remoteSize = [int64](Invoke-Wsl "stat -c %s '$RemotePath'" | Select-Object -Last 1)
    if ($remoteSize -ne $localSize) {
        throw "Short write into ${Distro}:${RemotePath} - sent $localSize bytes, landed $remoteSize."
    }
}

# ── Preflight ────────────────────────────────────────────────────────────────
$commit = (& git -C $REPO_ROOT rev-parse $Ref 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $commit) { throw "'$Ref' does not resolve to a commit in $REPO_ROOT." }
$commit = $commit.Trim()
$commitShort = (& git -C $REPO_ROOT rev-parse --short $commit).Trim()

if (-not (Invoke-Wsl 'echo ok' | Where-Object { $_ -eq 'ok' })) {
    throw "WSL distro '$Distro' is not reachable. `wsl.exe --list --verbose` to check."
}

$dirty = @(& git -C $REPO_ROOT status --porcelain -- unity src | Where-Object { $_ })
if ($dirty.Count -gt 0) {
    Write-Host "unity-gate-wsl: $($dirty.Count) uncommitted path(s) under unity/ or src/ - the gate runs $commitShort, NOT your working tree." -ForegroundColor Yellow
}

if ($Fresh) {
    Write-Host "unity-gate-wsl: -Fresh - removing $WslCheckout (cold asset import costs ~25 min)." -ForegroundColor Yellow
    Invoke-Wsl "rm -rf '$WslCheckout'" | Out-Null
}

$haveHead = (Invoke-Wsl "cd '$WslCheckout' 2>/dev/null && git rev-parse HEAD 2>/dev/null" | Select-Object -Last 1)
$haveHead = if ($haveHead -match '^[0-9a-f]{40}$') { $haveHead } else { $null }

# Does the checkout already CONTAIN the target? Not the same question as "is it checked out".
# Re-running an older commit (or any ancestor of what is already there) needs no transfer at all,
# and asking for one produces "Refusing to create empty bundle" - a fatal-looking message for a
# situation that is completely fine.
$haveCommit = $haveHead -and
    ((Invoke-Wsl "cd '$WslCheckout' 2>/dev/null && git cat-file -t '$commit' 2>/dev/null" |
      Select-Object -Last 1) -eq 'commit')

# ── Ship the commit ──────────────────────────────────────────────────────────
$bundle = Join-Path ([System.IO.Path]::GetTempPath()) "unity-gate-wsl-$commitShort.bundle"
Remove-Item $bundle -Force -ErrorAction SilentlyContinue

if ($haveHead -eq $commit) {
    Write-Host "unity-gate-wsl: $WslCheckout is already at $commitShort - no transfer." -ForegroundColor DarkGray
} elseif ($haveCommit) {
    # Present but not checked out - e.g. re-running an earlier commit after a newer one. Move the
    # working tree, ship nothing.
    Write-Host "unity-gate-wsl: $commitShort already present in the checkout - moving to it, no transfer." -ForegroundColor DarkGray
    $moveOnly = @"
set -e
cd '$WslCheckout'
git checkout --quiet --detach '$commit'
git rev-parse HEAD > /tmp/unity-gate-wsl-head.txt
"@
    Invoke-WslScript -Body $moveOnly -RemotePath '/tmp/unity-gate-wsl-sync.sh' | ForEach-Object { Write-Host "  $_" }
    $landed = (Invoke-Wsl 'cat /tmp/unity-gate-wsl-head.txt' | Select-Object -Last 1)
    if ($landed -ne $commit) { throw "Checkout landed on '$landed', expected '$commit'." }
} else {
    # `git bundle create` refuses a bare SHA - "Refusing to create empty bundle" - because a bundle
    # stores REFS, and a detached commit gives it no ref name to write. So pin the resolved commit
    # under a temporary ref and bundle that; this also makes -Ref accept a branch, tag, or SHA
    # uniformly, and gives the clone path on the other side a branch to check out.
    $tmpRef = 'refs/heads/unity-gate-wsl'
    & git -C $REPO_ROOT update-ref $tmpRef $commit
    if ($LASTEXITCODE -ne 0) { throw "Could not pin $commitShort to $tmpRef." }
    try {
        $basisKnown = $haveHead -and ((& git -C $REPO_ROOT cat-file -t $haveHead 2>$null) -eq 'commit')
        if ($basisKnown) {
            Write-Host "unity-gate-wsl: bundling $($haveHead.Substring(0,7))..$commitShort" -ForegroundColor Cyan
            $bundleOut = @(& git -C $REPO_ROOT bundle create $bundle $tmpRef --not $haveHead 2>&1)
        } else {
            Write-Host "unity-gate-wsl: no reusable basis in the checkout - full bundle (this is the slow one)." -ForegroundColor Cyan
            $bundleOut = @(& git -C $REPO_ROOT bundle create $bundle $tmpRef 2>&1)
        }
        # Never swallow git's stderr here: "Refusing to create empty bundle" is the one message
        # that explains an otherwise silent missing file, and hiding it cost a debugging round.
        if (-not (Test-Path $bundle)) {
            throw "git bundle produced nothing for ${commitShort}: $($bundleOut -join '; ')"
        }
    } finally {
        & git -C $REPO_ROOT update-ref -d $tmpRef 2>&1 | Out-Null
    }

    $mb = [math]::Round((Get-Item $bundle).Length / 1MB, 1)
    Write-Host "unity-gate-wsl: shipping $mb MB into $Distro…" -ForegroundColor Cyan
    Copy-ToWsl -LocalPath $bundle -RemotePath '/tmp/unity-gate-wsl.bundle'
    Remove-Item $bundle -Force -ErrorAction SilentlyContinue

    # Detached checkout: no branch bookkeeping, and it cannot collide with anything else in there.
    $sync = @"
set -e
if [ -d '$WslCheckout/.git' ]; then
  cd '$WslCheckout'
  git fetch --quiet /tmp/unity-gate-wsl.bundle 'refs/heads/unity-gate-wsl:refs/heads/unity-gate-wsl'
else
  rm -rf '$WslCheckout'
  git clone --quiet --branch unity-gate-wsl /tmp/unity-gate-wsl.bundle '$WslCheckout'
  cd '$WslCheckout'
fi
# Detached: no branch bookkeeping, and nothing here can collide with another checkout.
git checkout --quiet --detach '$commit'
git branch -D unity-gate-wsl >/dev/null 2>&1 || true
rm -f /tmp/unity-gate-wsl.bundle
git rev-parse HEAD > /tmp/unity-gate-wsl-head.txt
"@
    Invoke-WslScript -Body $sync -RemotePath '/tmp/unity-gate-wsl-sync.sh' | ForEach-Object { Write-Host "  $_" }

    $landed = (Invoke-Wsl 'cat /tmp/unity-gate-wsl-head.txt' | Select-Object -Last 1)
    if ($landed -ne $commit) { throw "Checkout landed on '$landed', expected '$commit'." }
}

# ── Run the gate ─────────────────────────────────────────────────────────────
Write-Host "unity-gate-wsl: running unity-verify.ps1 on $commitShort (warm Library ~2 min, cold ~25)…" -ForegroundColor Cyan

# Exit code and output both come back through a FILE, never an inline `$?`.
$run = @"
cd '$WslCheckout'
pwsh -NoProfile -File tools/ci/unity-verify.ps1 > /tmp/unity-gate-wsl-verify.log 2>&1
echo "VERIFY_EXIT=`$?" > /tmp/unity-gate-wsl-verify.rc
cat /tmp/unity-gate-wsl-verify.rc
grep -E '^unity-verify: (PASS|FAIL|ALL PASS|FAILED)' /tmp/unity-gate-wsl-verify.log || true
# A compile break emits NO per-check lines at all, so the check table alone would report the
# failure as "0 failing checks". Carry the first few real compiler errors back with it.
grep -oE '[^ ]+\.cs\([0-9]+,[0-9]+\): error CS[0-9]+: .*' /tmp/unity-gate-wsl-verify.log | head -5 || true
"@
$out = Invoke-WslScript -Body $run -RemotePath '/tmp/unity-gate-wsl-run.sh'
$out | ForEach-Object { Write-Host "  $_" }

$rcLine = $out | Where-Object { $_ -match '^VERIFY_EXIT=(\d+)$' } | Select-Object -Last 1
if (-not $rcLine) { throw "Could not read an exit code back from the distro. Full log: ${Distro}:/tmp/unity-gate-wsl-verify.log" }
$rc = [int]([regex]::Match($rcLine, '^VERIFY_EXIT=(\d+)$').Groups[1].Value)

$failed   = @($out | Where-Object { $_ -match '^unity-verify:\s+FAIL\s' })
$allPass  = @($out | Where-Object { $_ -match '^unity-verify: ALL PASS$' }).Count -gt 0
# Unity logs each diagnostic once per compilation pass, so the same CS error arrives several
# times. Dedupe, or a single typo reads as five separate failures.
$csErrors = @($out | Where-Object { $_ -match 'error CS\d+' } | Select-Object -Unique)
$reason   = ($out | Where-Object { $_ -match '^unity-verify: FAILED — (.+)$' } |
             ForEach-Object { [regex]::Match($_, '^unity-verify: FAILED — (.+)$').Groups[1].Value } |
             Select-Object -Last 1)

Write-Host ''
if ($rc -eq 0 -and $failed.Count -eq 0 -and $allPass) {
    Write-Host "unity-gate-wsl: PASS — $commitShort compiles and verifies under Unity (log: ${Distro}:/tmp/unity-gate-wsl-verify.log)" -ForegroundColor Green
    exit 0
}

# Say WHICH kind of failure. A compile break produces no per-check lines, so reporting a check
# count here would read as "0 failing checks" on a tree that does not build - the exact
# misleading-gate-output problem this script exists to avoid.
$locked = @($out | Where-Object { $_ -match 'project is locked|UnityLockfile' }).Count -gt 0

$what = if ($locked) {
    # A run that dies on a compile break exits abnormally and leaves Temp/UnityLockfile behind,
    # so the NEXT run refuses to start and would otherwise be reported as a code failure.
    # unity-verify.ps1 never clears locks on purpose, and neither does this - a lock can mean a
    # live Editor. Say which it is instead of guessing.
    "the project is LOCKED, not broken - a previous run left ${WslCheckout}/unity/Emberfall/Temp/UnityLockfile. Confirm no Editor is running in '$Distro' (wsl -d $Distro -- pgrep -l Unity), then remove it"
} elseif ($csErrors.Count -gt 0) {
    "compile errors"
} elseif ($failed.Count -gt 0) {
    "$($failed.Count) failing check(s)"
} elseif (-not $allPass) {
    "no check output (the Editor produced no PASS/FAIL table)"
} else {
    "verify exit $rc"
}
$detail = if ($reason -and $reason -ne $what) { " [$reason]" } else { '' }
Write-Host "unity-gate-wsl: FAIL — $what$detail (verify exit $rc; log: ${Distro}:/tmp/unity-gate-wsl-verify.log)" -ForegroundColor Red
$csErrors | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
if ($csErrors.Count -gt 5) { Write-Host "    … and $($csErrors.Count - 5) more" -ForegroundColor Red }
exit 1
