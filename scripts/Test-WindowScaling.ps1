#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$Executable, [Parameter(Mandatory=$true)][string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$root = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $root) { throw 'Use a fresh evidence directory.' }
[void](New-Item -ItemType Directory -Path $root)
@{ version=1; WindowView=$true; WindowWidth=480; WindowHeight=640 } | ConvertTo-Json | Set-Content (Join-Path $root 'state.json')
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
$process = Start-Process -FilePath $exe -ArgumentList @('--hud-smoke','--duration','45','--no-tray','--data-directory',('"'+$root+'"')) -WindowStyle Hidden -PassThru
try {
 $window = [IntPtr]::Zero
 for ($i=0;$i -lt 40 -and $window -eq [IntPtr]::Zero;$i++) { Start-Sleep -Milliseconds 250; $window=[ScalingProbe]::Find($process.Id) }
 if ($window -eq [IntPtr]::Zero) { throw 'Window view did not start.' }
 Start-Sleep -Milliseconds 1500
 $style=[ScalingProbe]::GetWindowLongPtr($window,-16).ToInt64()
 if (($style -band 0x40000) -eq 0 -or ($style -band 0x10000) -eq 0) { throw 'Native resize/maximize styles missing.' }
 $r=New-Object ScalingProbe+Rect
 [void][ScalingProbe]::GetWindowRect($window,[ref]$r)
 $dpi=[ScalingProbe]::GetDpiForWindow($window)
 $maxHit=$false
 for ($y=$r.Top+5;$y -lt $r.Top+[int](50*$dpi/96) -and -not $maxHit;$y+=4) {
 for ($x=$r.Right-[int](180*$dpi/96);$x -lt $r.Right;$x+=4) {
  $point=[IntPtr](($y -shl 16) -bor ($x -band 0xffff))
  if ([ScalingProbe]::SendMessage($window,0x84,[IntPtr]::Zero,$point).ToInt64() -eq 9) { $maxHit=$true; break }
 }
 }
 if (-not $maxHit) { throw 'Native maximize hit target missing (Snap menu prerequisite).' }
 [void][ScalingProbe]::SetWindowPos($window,[IntPtr]::Zero,$r.Left,$r.Top,760,700,0x14)
 Start-Sleep -Seconds 4
 [void][ScalingProbe]::GetWindowRect($window,[ref]$r)
 if ([Math]::Abs(($r.Right-$r.Left)-760) -gt 2) { throw 'Live updates reset resized width.' }
 [void][ScalingProbe]::SendMessage($window,0x112,[IntPtr]0xF030,[IntPtr]::Zero)
 Start-Sleep -Milliseconds 800
 if (-not [ScalingProbe]::IsZoomed($window)) { throw 'Native maximize failed.' }
 $saved=Get-Content (Join-Path $root 'state.json') -Raw | ConvertFrom-Json
 if (-not $saved.WindowMaximized -or [Math]::Abs($saved.WindowWidth*$dpi/96-760) -gt 3) { throw 'Normal restore size not retained while maximized.' }
 [void][ScalingProbe]::SendMessage($window,0x112,[IntPtr]0xF120,[IntPtr]::Zero)
 Start-Sleep -Milliseconds 700
 [void][ScalingProbe]::GetWindowRect($window,[ref]$r)
 if ([Math]::Abs(($r.Right-$r.Left)-760) -gt 2) { throw 'Native restore failed.' }
 [ordered]@{ nativeResize=$true; resizeSurvivesUpdates=$true; nativeMaximizeHitTarget=$maxHit; maximizeRestore=$true; normalBoundsPersisted=$true; dpi=$dpi; snapKeyboardAndHover='not automated'; realMixedDpiTransitions='not exercised' } | ConvertTo-Json | Tee-Object -FilePath (Join-Path $root 'window-scaling-evidence.json')
} finally {
 if (-not $process.HasExited) {
  $window=[ScalingProbe]::Find($process.Id)
  if ($window -ne [IntPtr]::Zero) { [void][ScalingProbe]::PostMessage($window,0x10,[IntPtr]::Zero,[IntPtr]::Zero) }
  if (-not $process.WaitForExit(5000)) { $process.WaitForExit(50000) | Out-Null }
 }
}
