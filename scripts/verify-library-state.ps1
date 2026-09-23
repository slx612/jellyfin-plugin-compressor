param(
    [Parameter(Mandatory=$true)][string]$Fixture,
    [Parameter(Mandatory=$true)][string]$Jellyfin,
    [Parameter(Mandatory=$true)][string]$Ffmpeg
)
$ErrorActionPreference = 'Stop'
$fixturePath = (Resolve-Path -LiteralPath $Fixture).Path
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/identity-check')) + [IO.Path]::DirectorySeparatorChar
if (-not $fixturePath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Fixture must be inside this repository artifacts/identity-check directory.'
}
if (-not (Test-Path -LiteralPath "$fixturePath/fixture.json")) { throw 'Prepare the synthetic fixture first.' }
if (Test-Path -LiteralPath "$fixturePath/state.json") { throw 'Use a fresh fixture for a complete run.' }
$serverExecutable = (Resolve-Path -LiteralPath $Jellyfin).Path
$ffmpegExecutable = (Resolve-Path -LiteralPath $Ffmpeg).Path
$driver = Join-Path $PSScriptRoot 'verify-library-state.py'
$server = $null

function Start-FixtureServer {
    if (Get-NetTCPConnection -State Listen -LocalPort 18096 -ErrorAction SilentlyContinue) {
        throw 'Port 18096 already in use; refusing to contact an existing server.'
    }
    $serverArgs = @('--datadir', ('"' + "$fixturePath/server" + '"'),
        '--configdir', ('"' + "$fixturePath/server/config" + '"'),
        '--cachedir', ('"' + "$fixturePath/server/cache" + '"'),
        '--logdir', ('"' + "$fixturePath/server/log" + '"'),
        '--ffmpeg', ('"' + $ffmpegExecutable + '"'), '--nowebclient', '--nonetchange')
    $script:server = Start-Process -FilePath $serverExecutable -ArgumentList $serverArgs -WindowStyle Hidden -PassThru
    $deadline = (Get-Date).AddSeconds(180)
    do {
        if ($script:server.HasExited) { throw 'Fixture server exited; inspect server/log.' }
        $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $script:server.Id -ErrorAction SilentlyContinue)
        if ($listeners.Count) {
            if (@($listeners | Where-Object { $_.LocalAddress -ne '127.0.0.1' -or $_.LocalPort -ne 18096 }).Count) {
                throw 'Fixture unexpectedly listening outside 127.0.0.1:18096.'
            }
            try {
                # The startup app also serves System/Info/Public before the API is ready.
                $health = Invoke-RestMethod -Uri 'http://127.0.0.1:18096/health' -TimeoutSec 2
                if ($health -eq 'Healthy') {
                    $public = Invoke-RestMethod -Uri 'http://127.0.0.1:18096/System/Info/Public' -TimeoutSec 2
                    if ($public.Version -eq '10.11.6') { return }
                }
            } catch { }
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    throw 'Fixture server startup timed out; inspect server/log.'
}

function Stop-FixtureServer {
    if ($script:server -and -not $script:server.HasExited) {
        & py -3 $driver shutdown --fixture $fixturePath
        if ($LASTEXITCODE -ne 0) { throw 'Graceful fixture shutdown failed.' }
        # Jellyfin optimizes SQLite during shutdown; allow time after a library scan.
        $shutdownDeadline = (Get-Date).AddSeconds(180)
        while (-not $script:server.WaitForExit(1000)) {
            if ((Get-Date) -gt $shutdownDeadline) { throw 'Fixture shutdown timed out.' }
        }
    }
}

try {
    Start-FixtureServer
    & py -3 $driver compress --fixture $fixturePath
    if ($LASTEXITCODE -ne 0) { throw 'Compression identity check failed.' }
    Stop-FixtureServer
    Start-FixtureServer
    & py -3 $driver after-restart --fixture $fixturePath
    if ($LASTEXITCODE -ne 0) { throw 'Restart/restore identity check failed.' }
    Stop-FixtureServer
    Write-Output "All live checks passed. Evidence: $fixturePath/result.json"
} finally {
    # Only the process object created above, never another Jellyfin instance.
    if ($server -and -not $server.HasExited) { $server.Kill(); $server.WaitForExit() }
}
