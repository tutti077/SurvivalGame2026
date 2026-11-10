@echo off
setlocal DisableDelayedExpansion
set "SUBLIME_MERGE_FIX_FILE=%~f0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "$file = [IO.File]::ReadAllText($env:SUBLIME_MERGE_FIX_FILE); & ([ScriptBlock]::Create(($file -split '(?m)^# POWERSHELL\r?$', 2)[1]))"
if errorlevel 1 (
    echo.
    pause
    exit /b 1
)
exit /b 0

# POWERSHELL
$ErrorActionPreference = 'Stop'
$mutex = [Threading.Mutex]::new($false, 'Local\FixSublimeMergeSession')
$lockHeld = $false

try {
    try {
        $lockHeld = $mutex.WaitOne(0)
    } catch [Threading.AbandonedMutexException] {
        $lockHeld = $true
    }
    if (-not $lockHeld) { return }

    $sessionId = (Get-Process -Id $PID).SessionId
    $mergeProcesses = @(Get-Process -Name sublime_merge -ErrorAction SilentlyContinue |
        Where-Object SessionId -eq $sessionId)

    $programFiles = $env:ProgramW6432
    if (-not $programFiles) { $programFiles = $env:ProgramFiles }
    $mergeExe = Join-Path $programFiles 'Sublime Merge\sublime_merge.exe'
    if (-not (Test-Path -LiteralPath $mergeExe -PathType Leaf)) {
        $mergeExe = $mergeProcesses | Select-Object -ExpandProperty Path -First 1
        if (-not $mergeExe -or -not (Test-Path -LiteralPath $mergeExe -PathType Leaf)) {
            throw 'Sublime Merge was not found in Program Files. If it is installed elsewhere, open it and run this file again.'
        }
    }

    foreach ($process in $mergeProcesses) {
        if ($process.HasExited) { continue }
        Stop-Process -InputObject $process -Force
        if (-not $process.WaitForExit(5000)) {
            throw 'Sublime Merge did not close. End it in Task Manager and run this file again.'
        }
    }

    $mergeData = Join-Path $env:APPDATA 'Sublime Merge'
    $sessionFiles = @(
        'Session.sublime_session'
        'Auto Save Session.sublime_session'
        'Local\Session.sublime_session'
        'Local\Auto Save Session.sublime_session'
    )
    $backupDir = $null
    foreach ($sessionFile in $sessionFiles) {
        $source = Join-Path $mergeData $sessionFile
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        if (-not $backupDir) {
            $backupDir = Join-Path $mergeData ('session-backup-' + [Guid]::NewGuid().ToString('N'))
        }
        $destination = Join-Path $backupDir $sessionFile
        [IO.Directory]::CreateDirectory((Split-Path -LiteralPath $destination)) | Out-Null
        [IO.File]::Move($source, $destination)
    }

    Start-Process -FilePath $mergeExe
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
} finally {
    if ($lockHeld) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
