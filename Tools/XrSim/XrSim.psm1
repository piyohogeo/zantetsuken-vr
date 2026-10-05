#Requires -Version 5.1
<#
.SYNOPSIS
    Shared helpers for the Meta XR Simulator VRS fixture tools (Tools/XrSim).

.DESCRIPTION
    Used by Build-XrSimHarnessPlayer.ps1, New-XrSimFixture.ps1, Invoke-XrSimReplay.ps1
    and Test-XrSimFixture.ps1. Nothing here changes machine-wide state:

      - the OpenXR runtime is selected for the harness Player process only
        (XR_RUNTIME_JSON); the HKLM ActiveRuntime is read and compared, never written;
      - the Simulator's per-user settings under %APPDATA%\MetaXR are saved byte for byte
        and written back (files created meanwhile are removed; Simulator logs are kept);
      - the Git working tree is snapshotted before and compared after.
#>

Set-StrictMode -Version Latest

# Captured at import: $PSScriptRoot is not reliable inside every module call context.
$script:ModuleRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:UnityEditorPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
$script:SimulatorDirectory = 'C:\Program Files\MetaXRSimulator\v207.0'
$script:SimulatorRuntimeJson = Join-Path $script:SimulatorDirectory 'meta_openxr_simulator.json'
$script:SimulatorExpectedVersionPrefix = '207.'
$script:SimulatorSettingsRoot = Join-Path $env:APPDATA 'MetaXR'
$script:PersistentDataPath = Join-Path $script:SimulatorSettingsRoot 'MetaXrSimulator\persistent_data.json'
$script:SimulatorLogDirectory = Join-Path $script:SimulatorSettingsRoot 'MetaXrSimulator\logs'

function Get-XrSimConstants {
    [pscustomobject]@{
        UnityEditorPath                = $script:UnityEditorPath
        SimulatorDirectory             = $script:SimulatorDirectory
        SimulatorRuntimeJson           = $script:SimulatorRuntimeJson
        SimulatorExpectedVersionPrefix = $script:SimulatorExpectedVersionPrefix
        PersistentDataPath             = $script:PersistentDataPath
        SimulatorLogDirectory          = $script:SimulatorLogDirectory
    }
}

function Write-XrSimLog {
    param([string]$Message)
    Write-Output ('[{0:HH:mm:ss}] {1}' -f (Get-Date), $Message)
}

# ---------------------------------------------------------------------------
# Repository and Git state
# ---------------------------------------------------------------------------
function Get-XrSimRepositoryRoot {
    $ErrorActionPreference = 'Continue'
    # Collect all output first: Select-Object -First in the pipeline stops git early and loses its exit code.
    $output = @(& git -C $script:ModuleRoot rev-parse --show-toplevel)
    $code = $LASTEXITCODE
    if ($code -ne 0 -or $output.Count -eq 0 -or -not $output[0]) { throw "Tools/XrSim must run inside the Git repository (git -C $($script:ModuleRoot) rev-parse exit $code)." }
    return [System.IO.Path]::GetFullPath($output[0].ToString().Trim())
}

function Get-XrSimGitState {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)
    $head = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
    $status = @(& git -C $RepositoryRoot status --porcelain=v1 --untracked-files=all)
    [pscustomobject]@{ Head = $head; Status = ($status -join "`n"); Lines = $status.Count }
}

function Assert-XrSimGitStateUnchanged {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Before,
        [string]$When = 'after'
    )
    $after = Get-XrSimGitState -RepositoryRoot $RepositoryRoot
    if ($after.Head -ne $Before.Head -or $after.Status -ne $Before.Status) {
        throw "Git working tree changed ($When): HEAD $($Before.Head) -> $($after.Head); status lines $($Before.Lines) -> $($after.Lines)"
    }
    return "git state $When unchanged (HEAD $($after.Head.Substring(0, 7)), $($after.Lines) status lines)"
}

# ---------------------------------------------------------------------------
# OpenXR runtime registry (read only)
# ---------------------------------------------------------------------------
function Get-XrSimOpenXrRegistryState {
    $key = Get-Item -Path 'HKLM:\SOFTWARE\Khronos\OpenXR\1' -ErrorAction SilentlyContinue
    $values = if ($key) { ($key.Property | Sort-Object | ForEach-Object { "$_=$($key.GetValue($_))" }) -join ' ; ' } else { '<no key>' }
    return "$values ; HKCU=$(Test-Path 'HKCU:\SOFTWARE\Khronos\OpenXR')"
}

function Assert-XrSimOpenXrRegistryUnchanged {
    param([Parameter(Mandatory = $true)][string]$Expected, [string]$When = 'after')
    $state = Get-XrSimOpenXrRegistryState
    if ($state -ne $Expected) { throw "OpenXR runtime registry changed ($When): '$Expected' -> '$state'" }
    return "OpenXR runtime registry $When unchanged"
}

function Assert-XrSimRegistryNotSimulator {
    $active = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Khronos\OpenXR\1' -ErrorAction SilentlyContinue).ActiveRuntime
    if ($active -and $active -like "$($script:SimulatorDirectory)*") {
        throw "The system OpenXR runtime is the Meta XR Simulator ($active). Turn the Simulator runtime toggle off first; these tools select it per process."
    }
    return "system OpenXR runtime: $active"
}

# ---------------------------------------------------------------------------
# Simulator installation and settings
# ---------------------------------------------------------------------------
function Get-XrSimSimulatorVersion {
    $exe = Join-Path $script:SimulatorDirectory 'MetaXRSimulator.exe'
    if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $script:SimulatorRuntimeJson)) {
        throw "Meta XR Simulator not found under $($script:SimulatorDirectory)"
    }
    $version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
    if (-not $version -or -not $version.StartsWith($script:SimulatorExpectedVersionPrefix)) {
        throw "Meta XR Simulator file version '$version' is not $($script:SimulatorExpectedVersionPrefix)x"
    }
    return $version
}

function Save-XrSimSimulatorSettings {
    param([Parameter(Mandatory = $true)][string]$BackupDirectory)
    if (Test-Path -LiteralPath $BackupDirectory) { throw "settings backup directory already exists: $BackupDirectory" }
    New-Item -ItemType Directory -Path $BackupDirectory | Out-Null
    $entries = @()
    if (Test-Path -LiteralPath $script:SimulatorSettingsRoot) {
        foreach ($file in Get-ChildItem -LiteralPath $script:SimulatorSettingsRoot -Recurse -File) {
            if ($file.FullName.StartsWith($script:SimulatorLogDirectory, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $relative = $file.FullName.Substring($script:SimulatorSettingsRoot.Length).TrimStart('\')
            $copy = Join-Path $BackupDirectory ($relative -replace '[\\/]', '__')
            [IO.File]::WriteAllBytes($copy, [IO.File]::ReadAllBytes($file.FullName))
            $entries += [pscustomobject]@{ Relative = $relative; Copy = $copy; Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
        }
    }
    $manifest = [pscustomobject]@{ SettingsRoot = $script:SimulatorSettingsRoot; SavedAt = (Get-Date).ToString('o'); Files = $entries }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $BackupDirectory 'manifest.json') -Encoding UTF8
    return $manifest
}

function Restore-XrSimSimulatorSettings {
    param([Parameter(Mandatory = $true)]$Manifest)
    $report = @()
    foreach ($entry in $Manifest.Files) {
        $target = Join-Path $script:SimulatorSettingsRoot $entry.Relative
        $same = (Test-Path -LiteralPath $target) -and ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $entry.Sha256)
        if (-not $same) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
            [IO.File]::WriteAllBytes($target, [IO.File]::ReadAllBytes($entry.Copy))
            $report += "restored $($entry.Relative)"
        }
    }
    $known = @($Manifest.Files | ForEach-Object { $_.Relative.ToLowerInvariant() })
    if (Test-Path -LiteralPath $script:SimulatorSettingsRoot) {
        foreach ($file in Get-ChildItem -LiteralPath $script:SimulatorSettingsRoot -Recurse -File) {
            if ($file.FullName.StartsWith($script:SimulatorLogDirectory, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $relative = $file.FullName.Substring($script:SimulatorSettingsRoot.Length).TrimStart('\')
            if ($known -notcontains $relative.ToLowerInvariant()) {
                Remove-Item -LiteralPath $file.FullName
                $report += "removed $relative (created during the run)"
            }
        }
    }
    foreach ($entry in $Manifest.Files) {
        $target = Join-Path $script:SimulatorSettingsRoot $entry.Relative
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.Sha256) { throw "Simulator setting not restored: $($entry.Relative)" }
    }
    if ($report.Count -eq 0) { $report += 'all Simulator settings already identical to the backup' }
    return $report
}

function Set-XrSimReplayAutomation {
    <# Writes the documented persistent_data.json session_capture block (Meta XR Simulator automated replay). #>
    param(
        [Parameter(Mandatory = $true)][byte[]]$OriginalBytes,
        [Parameter(Mandatory = $true)][string]$RecordPath,
        [int]$DelayStartMs = 20000,
        [bool]$QuitWhenComplete = $true,
        [int]$QuitBufferMs = 5000
    )
    $text = [Text.Encoding]::UTF8.GetString($OriginalBytes).TrimStart([char]0xFEFF)
    $data = if ($text.Trim()) { $text | ConvertFrom-Json } else { [pscustomobject]@{} }
    $capture = [pscustomobject][ordered]@{
        exec_state         = 'replay'
        record_path        = $RecordPath
        delay_start_ms     = $DelayStartMs
        quit_when_complete = $QuitWhenComplete
        quit_buffer_ms     = $QuitBufferMs
    }
    $data | Add-Member -NotePropertyName session_capture -NotePropertyValue $capture -Force
    [IO.File]::WriteAllText($script:PersistentDataPath, ($data | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding($false)))
}

# ---------------------------------------------------------------------------
# Processes
# ---------------------------------------------------------------------------
function Assert-XrSimNoConflictingProcess {
    $running = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(Unity\.exe|AssetImportWorker|zantetsuken-vr\.exe)' })
    if ($running.Count -gt 0) { throw "Unity or a Player is already running: $(($running | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ', ')" }
}

function Start-XrSimHarnessPlayer {
    param(
        [Parameter(Mandatory = $true)][string]$PlayerExe,
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][string[]]$HarnessArguments,
        # Meta XR Operator API layer, opt-in and process-scoped only: never set as a user or machine variable
        # and never written to HKLM. Off by default so the other tools keep their unchanged conditions.
        [string]$OperatorApiLayerPath = ''
    )
    $log = Join-Path $RunDirectory 'player.log'
    $psi = New-Object System.Diagnostics.ProcessStartInfo $PlayerExe
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['XR_RUNTIME_JSON'] = $script:SimulatorRuntimeJson
    if ($OperatorApiLayerPath) {
        $psi.EnvironmentVariables['XR_API_LAYER_PATH'] = $OperatorApiLayerPath
        $psi.EnvironmentVariables['XR_ENABLE_API_LAYERS'] = 'XR_APILAYER_METAX_operator'
    }
    $quoted = @($HarnessArguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } })
    $psi.Arguments = (@('-logFile', ('"' + $log + '"'), '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720', '-xrsimOut', ('"' + $RunDirectory + '"')) + $quoted) -join ' '
    return [System.Diagnostics.Process]::Start($psi)
}

function Stop-XrSimPlayer {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)
    if ($Process.HasExited) { return 'exited' }
    [void]$Process.CloseMainWindow()
    if ($Process.WaitForExit(30000)) { return 'closed' }
    $Process.Kill(); [void]$Process.WaitForExit(10000)
    return 'killed'
}

function Wait-XrSimFile {
    param([Parameter(Mandatory = $true)][string]$Path, [int]$TimeoutSeconds = 240, [System.Diagnostics.Process]$Process)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $Path) -and (Get-Date) -lt $deadline) {
        if ($Process -and $Process.HasExited) { return $false }
        Start-Sleep -Milliseconds 200
    }
    return (Test-Path -LiteralPath $Path)
}

# ---------------------------------------------------------------------------
# Python (analysis and the v205 adapter run in a venv under the work root)
# ---------------------------------------------------------------------------
function Get-XrSimPython {
    param([Parameter(Mandatory = $true)][string]$WorkRoot)
    $venv = Join-Path $WorkRoot '.venv'
    $python = Join-Path $venv 'Scripts\python.exe'
    $requirements = Join-Path $script:ModuleRoot 'requirements.txt'
    $stamp = Join-Path $venv 'requirements.sha256'
    $hash = (Get-FileHash -LiteralPath $requirements -Algorithm SHA256).Hash
    if (-not (Test-Path -LiteralPath $python)) {
        & python -m venv $venv
        if ($LASTEXITCODE -ne 0) { throw 'python -m venv failed' }
    }
    if (-not (Test-Path -LiteralPath $stamp) -or (Get-Content -LiteralPath $stamp -Raw).Trim() -ne $hash) {
        & $python -m pip install --disable-pip-version-check -q -r $requirements
        if ($LASTEXITCODE -ne 0) { throw 'pip install -r Tools/XrSim/requirements.txt failed' }
        Set-Content -LiteralPath $stamp -Value $hash
    }
    return $python
}

function Invoke-XrSimPython {
    <# Runs a Tools/XrSim Python script without writing __pycache__ into the repository; the output is returned, the exit code is in $LASTEXITCODE. #>
    param([Parameter(Mandatory = $true)][string]$Python, [Parameter(Mandatory = $true)][string[]]$Arguments)
    $previous = $env:PYTHONDONTWRITEBYTECODE
    $env:PYTHONDONTWRITEBYTECODE = '1'
    try { & $Python @Arguments }
    finally { $env:PYTHONDONTWRITEBYTECODE = $previous }
}

# ---------------------------------------------------------------------------
# Simulator window: foreground, input and visibility (Win32)
# ---------------------------------------------------------------------------
if (-not ('XrSimWin32' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class XrSimWin32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder text, int count);
    public static string WindowTitle(IntPtr h) { var text = new System.Text.StringBuilder(256); GetWindowText(h, text, 256); return text.ToString(); }
    public static uint WindowProcess(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
    public static void HoldKey(byte vk, bool extended, int milliseconds) {
        byte scan = (byte)MapVirtualKey(vk, 0);
        uint ext = extended ? 1u : 0u;
        keybd_event(vk, scan, ext, UIntPtr.Zero);
        System.Threading.Thread.Sleep(milliseconds);
        keybd_event(vk, scan, ext | 2u, UIntPtr.Zero);
    }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    // The Simulator process also owns an untitled data-preferences dialog, and MainWindowHandle resolves to it,
    // so the Simulator window has to be found by its title instead.
    public static IntPtr FindTopLevelWindow(uint pid, string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner == pid && IsWindowVisible(h) && WindowTitle(h) == title) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static string DescribeWindows(uint pid) {
        var rows = new System.Collections.Generic.List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner == pid && IsWindowVisible(h)) {
                RECT r;
                GetWindowRect(h, out r);
                rows.Add(string.Format("'{0}' {1}x{2}", WindowTitle(h), r.Right - r.Left, r.Bottom - r.Top));
            }
            return true;
        }, IntPtr.Zero);
        return string.Join(", ", rows.ToArray());
    }
}
"@
}

function Test-XrSimBytesEqual {
    <# Byte-for-byte comparison. [Linq.Enumerable]::SequenceEqual cannot be bound from PowerShell 5.1: it is a
       generic method and the type argument cannot be inferred, which throws "Cannot find an overload". #>
    param([byte[]]$Expected, [byte[]]$Actual)
    if ($null -eq $Expected -or $null -eq $Actual) { return ($null -eq $Expected -and $null -eq $Actual) }
    if ($Expected.Length -ne $Actual.Length) { return $false }
    for ($i = 0; $i -lt $Expected.Length; $i++) {
        if ($Expected[$i] -ne $Actual[$i]) { return $false }
    }

    return $true
}

function Get-XrSimSimulatorWindow {
    <# The Simulator's own window, located by its title. The Simulator process also owns an untitled
       data-preferences dialog, and MainWindowHandle resolves to that dialog: keys sent to it never reach the
       simulated head. Returns $null while no titled window exists. #>
    foreach ($process in @(Get-Process -Name 'MetaXRSimulator' -ErrorAction SilentlyContinue)) {
        $handle = [XrSimWin32]::FindTopLevelWindow([uint32]$process.Id, 'Meta XR Simulator')
        if ($handle -ne [IntPtr]::Zero) {
            return [pscustomobject]@{ Process = $process; Handle = $handle; VisibleWindows = [XrSimWin32]::DescribeWindows([uint32]$process.Id) }
        }
    }
    return $null
}

function Set-XrSimSimulatorForeground {
    <# Brings the Simulator window to the foreground and clicks inside it so key input reaches its viewport. True when it is the foreground window afterwards. #>
    param([Parameter(Mandatory = $true)]$Window)
    $handle = $Window.Handle
    $foreground = $false
    for ($i = 0; $i -lt 15 -and -not $foreground; $i++) {
        [XrSimWin32]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [XrSimWin32]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        [void][XrSimWin32]::ShowWindow($handle, 9)
        [void][XrSimWin32]::SetForegroundWindow($handle)
        Start-Sleep -Milliseconds 300
        $foreground = [XrSimWin32]::GetForegroundWindow() -eq $handle
    }
    if (-not $foreground) { return $false }
    $rect = New-Object XrSimWin32+RECT
    [void][XrSimWin32]::GetWindowRect($handle, [ref]$rect)
    [void][XrSimWin32]::SetCursorPos([int](($rect.Left + $rect.Right) / 2), [int](($rect.Top + $rect.Bottom) / 2))
    [XrSimWin32]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 100; [XrSimWin32]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    return ([XrSimWin32]::GetForegroundWindow() -eq $handle)
}

function Get-XrSimForegroundOwner {
    <# Describes the process that owns the current foreground window, for failure reasons (e.g. a Windows Security dialog). #>
    $handle = [XrSimWin32]::GetForegroundWindow()
    if ($handle -eq [IntPtr]::Zero) { return 'no foreground window' }
    $ownerId = [XrSimWin32]::WindowProcess($handle)
    $owner = Get-CimInstance Win32_Process -Filter "ProcessId = $ownerId" -ErrorAction SilentlyContinue
    $name = if ($owner) { $owner.Name } else { '?' }
    $commandLine = if ($owner) { $owner.CommandLine } else { '' }
    return "$name#$ownerId window '$([XrSimWin32]::WindowTitle($handle))' $commandLine"
}

function Close-XrSimSimulatorWindows {
    <# Closes every Simulator window until none is left: the runtime can open a replacement frontend after one exits. #>
    param([int]$Attempts = 5)
    $report = @()
    for ($i = 0; $i -lt $Attempts; $i++) {
        $windows = @(Get-Process -Name 'MetaXRSimulator' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
        if ($windows.Count -eq 0) { break }
        foreach ($window in $windows) {
            [void]$window.CloseMainWindow()
            $report += "closed Simulator window of process $($window.Id) (exited: $($window.WaitForExit(20000)))"
        }
        Start-Sleep -Seconds 2
    }
    $remaining = @(Get-Process -Name 'MetaXRSimulator' -ErrorAction SilentlyContinue)
    if ($remaining.Count -gt 0) { $report += "WARNING: $($remaining.Count) Meta XR Simulator process(es) still running" }
    return $report
}

function Get-XrSimSimulatorInputTarget {
    <# Re-resolves the Simulator window and makes it the foreground input target; $null when that fails (the runtime can
       replace its frontend window, so a cached handle goes stale). #>
    param([int]$TimeoutSeconds = 10)
    # Retry: right after the adapter starts the recording the foreground can be empty for a moment (launching
    # the adapter takes it), and the runtime can replace its frontend window. A loss that outlasts the
    # timeout is still reported as a failure.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $window = Get-XrSimSimulatorWindow
        if ($window) {
            if (Test-XrSimSimulatorForeground -Window $window) { return $window }
            if (Set-XrSimSimulatorForeground -Window $window) { return $window }
        }

        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Test-XrSimSimulatorForeground {
    param([Parameter(Mandatory = $true)]$Window)
    return ([XrSimWin32]::GetForegroundWindow() -eq $Window.Handle)
}

function Invoke-XrSimKey {
    <# Holds one Simulator key binding (default bindings of v205: W/A/S/D move, R/F up/down, arrows rotate). #>
    param([Parameter(Mandatory = $true)][string]$Key, [Parameter(Mandatory = $true)][int]$Milliseconds)
    $map = @{
        W = @(0x57, $false); A = @(0x41, $false); S = @(0x53, $false); D = @(0x44, $false); R = @(0x52, $false); F = @(0x46, $false)
        Left = @(0x25, $true); Up = @(0x26, $true); Right = @(0x27, $true); Down = @(0x28, $true)
    }
    if (-not $map.ContainsKey($Key)) { throw "unsupported Simulator key $Key" }
    [XrSimWin32]::HoldKey([byte]$map[$Key][0], [bool]$map[$Key][1], $Milliseconds)
}

function Set-XrSimSimulatorWindowHidden {
    <# Hides (or shows again) the running Simulator window. Closing it instead makes the runtime relaunch it. #>
    param([Parameter(Mandatory = $true)][bool]$Hidden, [IntPtr]$Handle = [IntPtr]::Zero)
    if ($Hidden) {
        $window = Get-XrSimSimulatorWindow
        if (-not $window) { return [IntPtr]::Zero }
        [void][XrSimWin32]::ShowWindow($window.Handle, 0)
        return $window.Handle
    }
    if ($Handle -ne [IntPtr]::Zero) { [void][XrSimWin32]::ShowWindow($Handle, 5) }
    return $Handle
}

Export-ModuleMember -Function *-XrSim*
