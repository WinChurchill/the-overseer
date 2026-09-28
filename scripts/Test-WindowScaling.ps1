#requires -Version 7.0
<#
.SYNOPSIS
    Checks the HUD's native resize, maximize/restore and per-monitor DPI behaviour.

.DESCRIPTION
    Launches one isolated --hud-smoke run and drives its own window with native
    messages. It never changes a display setting, never moves the pointer and never
    sends keyboard input, so the only thing it can disturb is the display the window
    is on. That is why the target display is chosen explicitly:

    * the isolated run's saved placement is seeded for the chosen display, and the
      same device is passed as --placement-monitor, so the window never starts on the
      display you are working on;
    * the per-monitor transition loop skips the primary display unless you pass
      -IncludePrimaryDisplay, which is intended for a single-display qualification VM;
    * with no usable non-primary display and no explicit opt-in the check defers,
      records the gap and exits 1 instead of opening a window on your display.

.EXAMPLE
    pwsh -NoProfile -File scripts/Test-WindowScaling.ps1 -Executable artifacts/rc6/TheOverseer.App.exe -EvidenceDirectory artifacts/rc6/scaling
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Executable,
    [Parameter(Mandatory = $true)][string] $EvidenceDirectory,
    # Pin the display the run uses instead of taking the first non-primary one.
    [string] $Monitor = '',
    # Only for a machine where nothing else is using the primary display.
    [switch] $AllowPrimaryDisplay,
    # Move the window across the primary display during the DPI transition loop.
    # Implied by -AllowPrimaryDisplay on a single-display machine.
    [switch] $IncludePrimaryDisplay,
    [ValidateRange(1, 600)][int] $SettleSeconds = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$exe = (Resolve-Path -LiteralPath $Executable).Path
$root = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $root) { throw 'Use a fresh evidence directory.' }
[void](New-Item -ItemType Directory -Path $root)

# ---- display selection -------------------------------------------------------
Add-Type -AssemblyName System.Windows.Forms

function Get-Screens {
    try { return @([System.Windows.Forms.Screen]::AllScreens) } catch { return @() }
}

$screens = Get-Screens
if ($screens.Count -eq 0) { throw 'No display was enumerated; refusing to run a windowed check blindly.' }

$primary = $screens | Where-Object { $_.Primary } | Select-Object -First 1
$nonPrimary = @($screens | Where-Object { -not $_.Primary } | Sort-Object -Property DeviceName)

$target = $null
$deferredReason = ''
if ($Monitor) {
    $match = $screens | Where-Object { $_.DeviceName -eq $Monitor } | Select-Object -First 1
    if (-not $match) {
        $deferredReason = 'requested display was not found: ' + $Monitor
    } elseif ($match.Primary -and -not $AllowPrimaryDisplay) {
        $deferredReason = 'requested display is the primary one and -AllowPrimaryDisplay was not passed: ' + $Monitor
    } else {
        $target = $match
    }
} elseif ($nonPrimary.Count -gt 0) {
    $target = $nonPrimary[0]
} elseif ($AllowPrimaryDisplay) {
    $target = $primary
} else {
    $deferredReason = 'no non-primary display was found and -AllowPrimaryDisplay was not passed'
}

if (-not $target) {
    $evidence = [ordered]@{
        product = 'OVERSEER'
        check = 'window-scaling'
        deferred = $true
        deferralReason = $deferredReason
        displays = @($screens | ForEach-Object { [ordered]@{ device = $_.DeviceName; primary = $_.Primary; bounds = $_.Bounds.ToString() } })
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    }
    $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'window-scaling-evidence.json') -Encoding utf8
    Write-Host ('window-scaling deferred: ' + $deferredReason)
    Write-Host ('evidence: ' + (Join-Path $root 'window-scaling-evidence.json'))
    exit 1
}

$targetDevice = [string]$target.DeviceName
$includePrimary = $IncludePrimaryDisplay -or ($AllowPrimaryDisplay -and $screens.Count -eq 1)
$transitionScreens = @($screens | Where-Object { $includePrimary -or -not $_.Primary })
Write-Host ('target display: ' + $targetDevice + ' (primary=' + $target.Primary + ')')
Write-Host ('transition displays: ' + (($transitionScreens | ForEach-Object { $_.DeviceName }) -join ', '))

# Seed the isolated run's saved placement for the target display. Nothing outside
# this evidence directory is written, and no display setting is touched.
#
# Both monitors are seeded because the two presentation modes read different fields:
# the collapsed HUD places by `monitorDevice` + `offsetXDips/YDips`, while the window
# view places by `WindowMonitor` + `WindowX/Y` relative to that monitor's origin. A
# window-view run that seeds only `monitorDevice` silently falls back to the primary
# display, which is exactly what this check exists to avoid.
$work = $target.WorkingArea
$offsetDips = 60.0
@{
    version = 1
    isExpanded = $false
    WindowView = $true
    WindowPinned = $false
    WindowMaximized = $false
    WindowMonitor = $targetDevice
    WindowX = $offsetDips
    WindowY = $offsetDips
    WindowWidth = 480
    WindowHeight = 640
    WindowDpi = 1.0
    monitorDevice = $targetDevice
    offsetXDips = $offsetDips
    offsetYDips = $offsetDips
    dpiScale = 1.0
    savedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'state.json') -Encoding utf8
Write-Host ('seeded placement: ' + $targetDevice + ' work-area ' + $work.ToString() + ' offset ' + $offsetDips + ' DIP')

Add-Type -TypeDefinition @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class ScalingProbe {
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p,IntPtr l);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint id);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h,int n);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr z,int x,int y,int w,int ht,uint f);
 [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [StructLayout(LayoutKind.Sequential)] public struct Rect {public int Left,Top,Right,Bottom;}
 public static IntPtr Find(int process){IntPtr result=IntPtr.Zero;EnumWindows((h,l)=>{uint id;GetWindowThreadProcessId(h,out id);var s=new StringBuilder(256);GetWindowText(h,s,256);if(id==process&&s.ToString()=="OVERSEER")result=h;return true;},IntPtr.Zero);return result;}
}
'@
[void][ScalingProbe]::SetThreadDpiAwarenessContext([IntPtr](-4))

$process = Start-Process -FilePath $exe -ArgumentList @(
    '--hud-smoke', '--duration', [string]$SettleSeconds, '--no-tray',
    '--placement-monitor', $targetDevice,
    '--data-directory', ('"' + $root + '"')) -WindowStyle Hidden -PassThru
try {
    $window = [IntPtr]::Zero
    for ($i = 0; $i -lt 40 -and $window -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 250
        $window = [ScalingProbe]::Find($process.Id)
    }
    if ($window -eq [IntPtr]::Zero) { throw 'Window view did not start.' }
    Start-Sleep -Milliseconds 1500

    # The window must really be on the chosen display, or the rest of the check is
    # measuring the display we were trying to avoid.
    $style = [ScalingProbe]::GetWindowLongPtr($window, -16).ToInt64()
    if (($style -band 0x40000) -eq 0 -or ($style -band 0x10000) -eq 0) { throw 'Native resize/maximize styles missing.' }

    $r = New-Object ScalingProbe+Rect
    [void][ScalingProbe]::GetWindowRect($window, [ref]$r)
    $dpi = [ScalingProbe]::GetDpiForWindow($window)
    $startedOnTarget = $r.Left -ge $target.Bounds.Left -and $r.Right -le $target.Bounds.Right -and
                       $r.Top -ge $target.Bounds.Top -and $r.Bottom -le $target.Bounds.Bottom
    if (-not $startedOnTarget) {
        throw ('the run started on the wrong display: window=' + $r.Left + ',' + $r.Top + ',' + $r.Right + ',' + $r.Bottom +
               ' expected inside ' + $target.Bounds.ToString())
    }

    $maxHit = $false
    for ($y = $r.Top + 5; $y -lt $r.Top + [int](50 * $dpi / 96) -and -not $maxHit; $y += 4) {
        for ($x = $r.Right - [int](180 * $dpi / 96); $x -lt $r.Right; $x += 4) {
            $point = [IntPtr](($y -shl 16) -bor ($x -band 0xffff))
            if ([ScalingProbe]::SendMessage($window, 0x84, [IntPtr]::Zero, $point).ToInt64() -eq 9) { $maxHit = $true; break }
        }
    }
    if (-not $maxHit) { throw 'Native maximize hit target missing (Snap menu prerequisite).' }

    [void][ScalingProbe]::SetWindowPos($window, [IntPtr]::Zero, $r.Left, $r.Top, 760, 700, 0x14)
    Start-Sleep -Seconds 4
    [void][ScalingProbe]::GetWindowRect($window, [ref]$r)
    if ([Math]::Abs(($r.Right - $r.Left) - 760) -gt 2) { throw 'Live updates reset resized width.' }

    [void][ScalingProbe]::SendMessage($window, 0x112, [IntPtr]0xF030, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 800
    if (-not [ScalingProbe]::IsZoomed($window)) { throw 'Native maximize failed.' }
    $saved = Get-Content -LiteralPath (Join-Path $root 'state.json') -Raw | ConvertFrom-Json
    if (-not $saved.WindowMaximized -or [Math]::Abs($saved.WindowWidth * $dpi / 96 - 760) -gt 3) { throw 'Normal restore size not retained while maximized.' }
    [void][ScalingProbe]::SendMessage($window, 0x112, [IntPtr]0xF120, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 700
    [void][ScalingProbe]::GetWindowRect($window, [ref]$r)
    if ([Math]::Abs(($r.Right - $r.Left) - 760) -gt 2) { throw 'Native restore failed.' }

    $transitions = @()
    $originalLeft = $r.Left
    $originalTop = $r.Top
    $logicalWidth = ($r.Right - $r.Left) * 96 / $dpi
    foreach ($screen in $transitionScreens) {
        $work = $screen.WorkingArea
        [void][ScalingProbe]::SetWindowPos($window, [IntPtr]::Zero, ($work.Left + 40), ($work.Top + 40), 0, 0, 0x15)
        Start-Sleep -Milliseconds 1200
        $newDpi = [ScalingProbe]::GetDpiForWindow($window)
        [void][ScalingProbe]::GetWindowRect($window, [ref]$r)
        $newLogical = ($r.Right - $r.Left) * 96 / $newDpi
        if ([Math]::Abs($newLogical - $logicalWidth) -gt 3) { throw 'DPI transition changed logical width.' }
        if ($r.Left -lt $work.Left - 8 -or $r.Right -gt $work.Right + 8 -or $r.Top -lt $work.Top - 8 -or $r.Bottom -gt $work.Bottom + 8) { throw 'DPI transition left window outside the work area.' }
        $transitions += @{ monitor = $screen.DeviceName; primary = $screen.Primary; dpi = $newDpi; logicalWidth = $newLogical }
    }

    [void][ScalingProbe]::SetWindowPos($window, [IntPtr]::Zero, $originalLeft, $originalTop, 0, 0, 0x15)
    Start-Sleep -Milliseconds 1200

    [ordered]@{
        product = 'OVERSEER'
        check = 'window-scaling'
        deferred = $false
        targetDisplay = $targetDevice
        targetIsPrimary = [bool]$target.Primary
        primaryIncludedInTransitions = [bool]$includePrimary
        displays = @($screens | ForEach-Object { [ordered]@{ device = $_.DeviceName; primary = $_.Primary; bounds = $_.Bounds.ToString() } })
        nativeResize = $true
        resizeSurvivesUpdates = $true
        nativeMaximizeHitTarget = $maxHit
        maximizeRestore = $true
        normalBoundsPersisted = $true
        startedOnTargetDisplay = $startedOnTarget
        dpi = $dpi
        monitorTransitions = $transitions
        checksNotAutomated = @(
            'Snap layouts by keyboard (Win+Left/Right) and the Snap hover menu',
            'physical drag by pointer and menu interaction',
            'display scale changes (100/125/150/200%) made through Windows settings',
            'monitor disconnect/reconnect'
        )
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $root 'window-scaling-evidence.json')
} finally {
    if (-not $process.HasExited) {
        $window = [ScalingProbe]::Find($process.Id)
        if ($window -ne [IntPtr]::Zero) { [void][ScalingProbe]::PostMessage($window, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) }
        if (-not $process.WaitForExit(5000) -and -not $process.WaitForExit(50000)) {
            # A failed probe must not leave its own diagnostic window behind.
            $process.Kill($true)
            [void]$process.WaitForExit(10000)
        }
    }
    $process.Dispose()
}
