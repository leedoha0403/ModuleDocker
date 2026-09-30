<#
  End-to-end check with REAL mouse input against the built Host (Debug build).

  WARNING: this script moves your physical mouse cursor and presses buttons for about 40 seconds.
  Do not use the mouse meanwhile. Every press is preceded by a check that the window under the cursor
  belongs to the Host under test; otherwise the run aborts. Run `dotnet build ModuleDock.sln` first.

  Scenario A: title-bar drag snaps to the screen edge, auto-hide, hover reveal, handle drags.
  Scenario B: widget reorder by drag, detach of a widget that cannot float, permission prompt free start.
#>
param([string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\RealInput.ps1"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$exe = Join-Path $repo "src\Dora.Widget.Host\bin\$Configuration\net8.0-windows\Dora.Widget.Host.exe"
if (-not (Test-Path $exe)) { throw "Host not built: $exe" }

# Only one script may drive the physical mouse at a time (two runs would click into each other's windows and turn
# single clicks into double clicks). Wait for a running one to finish.
$mouseLock = New-Object System.Threading.Mutex($false, 'Local\ModuleDock.E2E.Mouse')
try { $got = $mouseLock.WaitOne([TimeSpan]::FromMinutes(5)) } catch [System.Threading.AbandonedMutexException] { $got = $true }
if (-not $got) { throw 'another e2e run is still using the mouse' }

$script:failures = @()
function Check($name, [bool]$ok, $detail = '') {
  if ($ok) { "  PASS  $name" } else { "  FAIL  $name  $detail"; $script:failures += $name }
}

function Start-Host($settingsFile) {
  $d = Join-Path $env:TEMP ('md-e2e-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
  New-Item -ItemType Directory -Path $d | Out-Null
  Copy-Item (Join-Path $PSScriptRoot $settingsFile) (Join-Path $d 'host-settings.json')
  Copy-Item (Join-Path $PSScriptRoot 'permissions.json') (Join-Path $d 'permissions.json')   # no modal permission prompt
  $p = Start-Process $exe -ArgumentList '--data', $d -PassThru
  Start-Sleep 4
  [RI]::Topmost([uint32]$p.Id, 'ModuleDock'); Start-Sleep -Milliseconds 300
  [pscustomobject]@{ Proc = $p; Pid = [uint32]$p.Id; Dir = $d }
}

function Stop-Host($h) {
  $h.Proc.CloseMainWindow() | Out-Null
  Start-Sleep -Milliseconds 1500
  if (-not $h.Proc.HasExited) { $h.Proc.Kill() }
}

function Cleanup($h) {
  if ($env:E2E_KEEP -eq '1') { "  (kept $($h.Dir))"; return }
  if (Test-Path $h.Dir) { Remove-Item -LiteralPath $h.Dir -Recurse -Force }
}

$mon = [RI]::Monitors() | ? { $_ -like '*primary*' } | Select -First 1
$m = $mon.Split(',')
$workLeft = [int]$m[0]; $workTop = [int]$m[1]; $workRight = [int]$m[2]; $workBottom = [int]$m[3]
"Primary work area: $workLeft,$workTop - $workRight,$workBottom"

# ------------------------------------------------------------------ Scenario A
"`nScenario A: edge snap / auto-hide / handle"
$h = Start-Host 'snap-settings.json'
$w = Get-Win $h.Pid 'ModuleDock'
$px = $w.X + 130; $py = $w.Y + 30      # the header (title text) is the drag handle of the borderless Host
if (-not [RI]::Ours($px, $py, $h.Pid)) { Stop-Host $h; throw 'header is not under the cursor target (window covered?)' }
[RI]::Move($px, $py); Start-Sleep -Milliseconds 200
[RI]::Down(); Start-Sleep -Milliseconds 150
Step-Move $h.Pid $px $py ($workRight - $w.W - 8 + 130) $py 60
Start-Sleep -Milliseconds 200
[RI]::Up(); Start-Sleep -Milliseconds 400
$s = Get-Win $h.Pid 'ModuleDock'
Check 'header drag near the right edge snaps flush to the work area' (($s.X + $s.W) -eq $workRight) "right=$($s.X + $s.W) expected $workRight"

[RI]::Move(($workLeft + 300), ($workBottom - 200)); Start-Sleep -Milliseconds 1300
$s = Get-Win $h.Pid 'ModuleDock'; $hd = Get-Win $h.Pid 'ModuleDock handle'
Check 'auto-hide hides the host after the delay' (-not $s.Visible)
Check 'handle strip stays on screen with the configured thickness' ($hd.Visible -and $hd.W -eq 10 -and ($hd.X + $hd.W) -eq $workRight) "handle=$($hd.X),$($hd.Y) $($hd.W)x$($hd.H)"

$hx = $hd.X + [int]($hd.W / 2); $hy = $hd.Y + [int]($hd.H / 2)
Step-Move $h.Pid ($workLeft + 300) ($workBottom - 200) $hx $hy 25
for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Milliseconds 100; if ((Get-Win $h.Pid 'ModuleDock').Visible) { break } }   # reveal delay + slide
$s = Get-Win $h.Pid 'ModuleDock'
Check 'hovering the handle reveals the host' $s.Visible

[RI]::Move(($workLeft + 300), ($workBottom - 200)); Start-Sleep -Milliseconds 1300
$s = Get-Win $h.Pid 'ModuleDock'; $hd = Get-Win $h.Pid 'ModuleDock handle'
Check 'host hides again when the pointer leaves' (-not $s.Visible)

$before = $hd.Y
$hx = $hd.X + [int]($hd.W / 2); $hy = $hd.Y + [int]($hd.H / 2)
if ([RI]::Ours($hx, $hy, $h.Pid)) {
  Step-Move $h.Pid ($workLeft + 300) ($workBottom - 200) $hx $hy 25
  Start-Sleep -Milliseconds 30                       # press before the reveal delay elapses
  [RI]::Down(); Start-Sleep -Milliseconds 300
  Step-Move $h.Pid $hx $hy $hx ($hy - 200) 20
  [RI]::Up(); Start-Sleep -Milliseconds 400
  [RI]::Move(($workLeft + 300), ($workBottom - 200)); Start-Sleep -Milliseconds 300
  $hd = Get-Win $h.Pid 'ModuleDock handle'; $s = Get-Win $h.Pid 'ModuleDock'
  Check 'dragging the handle along the edge moves it and keeps the host hidden' (($hd.Y -lt $before - 100) -and -not $s.Visible) "y $before -> $($hd.Y)"
} else { Check 'handle reachable for drag' $false }

$hx = $hd.X + [int]($hd.W / 2); $hy = $hd.Y + [int]($hd.H / 2)
$midX = [int](($workLeft + $workRight) / 2); $midY = [int](($workTop + $workBottom) / 2)
if ([RI]::Ours($hx, $hy, $h.Pid)) {
  Step-Move $h.Pid ($workLeft + 300) ($workBottom - 200) $hx $hy 25
  Start-Sleep -Milliseconds 30
  [RI]::Down(); Start-Sleep -Milliseconds 300
  Step-Move $h.Pid $hx $hy $midX $midY 60
  [RI]::Up(); Start-Sleep -Milliseconds 600
  $s = Get-Win $h.Pid 'ModuleDock'
  $cx = $s.X + [int]($s.W / 2); $cy = $s.Y + [int]($s.H / 2)
  Check 'dragging the handle to the middle detaches into a floating host centred on the pointer' `
    ($s.Visible -and [math]::Abs($cx - $midX) -le 3 -and [math]::Abs($cy - $midY) -le 3) "centre=$cx,$cy expected $midX,$midY"
} else { Check 'handle reachable for detach drag' $false }
[RI]::Move(($workLeft + 300), ($workBottom - 200))
Stop-Host $h; Cleanup $h

# ------------------------------------------------------------------ Scenario B
"`nScenario B: widget drags"
$h = Start-Host 'floating-settings.json'

function Get-Labels($hostPid) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$hostPid)
  $out = @{}
  foreach ($win in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
    foreach ($t in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))) {
      $n = $t.Current.Name; $r = $t.Current.BoundingRectangle
      if ($n -in @('Clock', 'Counter', 'Disk') -and $r.Width -gt 0 -and -not $out.ContainsKey($n)) { $out[$n] = $r }
    }
  }
  $out
}
function Order-Now($hostPid) { (Get-Labels $hostPid).GetEnumerator() | Sort-Object { $_.Value.Y } | % { $_.Key } }

$labels = Get-Labels $h.Pid
Check 'three sample widgets are docked in name order' ((Order-Now $h.Pid) -join ',' -eq 'Clock,Counter,Disk') ((Order-Now $h.Pid) -join ',')
$w = Get-Win $h.Pid 'ModuleDock'
$first = $labels['Clock']; $last = $labels['Disk']
$sx = [int]($w.X + 100); $sy = [int]($first.Y + $first.Height / 2); $ey = [int]($last.Y + $last.Height + 25)
if ([RI]::Ours($sx, $sy, $h.Pid)) {
  [RI]::Move($sx, $sy); Start-Sleep -Milliseconds 300
  [RI]::Down(); Start-Sleep -Milliseconds 300
  Step-Move $h.Pid $sx $sy $sx $ey 30
  Start-Sleep -Milliseconds 200
  [RI]::Up(); Start-Sleep -Milliseconds 900
  [RI]::Move(($workLeft + 300), ($workBottom - 200)); Start-Sleep -Milliseconds 400
  Check 'dragging Clock down reorders it between Counter and Disk' ((Order-Now $h.Pid) -join ',' -eq 'Counter,Clock,Disk') ((Order-Now $h.Pid) -join ',')
} else { Check 'first widget reachable' $false }

# Counter cannot float: dragging it out must snap it back
$labels = Get-Labels $h.Pid
$top = $labels['Counter']; $w = Get-Win $h.Pid 'ModuleDock'
$sx = [int]($w.X + 100); $sy = [int]($top.Y + $top.Height / 2)
if ([RI]::Ours($sx, $sy, $h.Pid)) {
  [RI]::Move($sx, $sy); Start-Sleep -Milliseconds 300
  [RI]::Down(); Start-Sleep -Milliseconds 300
  Step-Move $h.Pid $sx $sy ($w.X + $w.W + 400) $sy 40
  Start-Sleep -Milliseconds 300
  [RI]::Up(); Start-Sleep -Milliseconds 900
  [RI]::Move(($workLeft + 300), ($workBottom - 200)); Start-Sleep -Milliseconds 300
  $others = [RI]::Windows($h.Pid) | ? { $_.Split('|')[1] -eq 'True' -and -not $_.StartsWith('ModuleDock|') }
  Check 'a widget that cannot float snaps back instead of detaching' ((Order-Now $h.Pid) -join ',' -eq 'Counter,Clock,Disk' -and -not $others) ((Order-Now $h.Pid) -join ',')
}

# Clock can float: detach then dock back
$labels = Get-Labels $h.Pid
$clk = $labels['Clock']; $w = Get-Win $h.Pid 'ModuleDock'
$sx = [int]($w.X + 100); $sy = [int]($clk.Y + $clk.Height / 2)
if ([RI]::Ours($sx, $sy, $h.Pid)) {
  [RI]::Move($sx, $sy); Start-Sleep -Milliseconds 300
  [RI]::Down(); Start-Sleep -Milliseconds 300
  Step-Move $h.Pid $sx $sy ($w.X + $w.W + 500) $sy 40
  Start-Sleep -Milliseconds 300
  [RI]::Up(); Start-Sleep -Milliseconds 900
  $floating = [RI]::Windows($h.Pid) | ? { $_.Split('|')[1] -eq 'True' -and -not $_.StartsWith('ModuleDock|') } | Select -First 1
  Check 'dragging Clock out of the host creates a floating window' ([bool]$floating) ''
  if ($floating) {
    $fr = $floating.Split('|')[2].Split(',')
    $fsx = [int]$fr[0] + 60; $fsy = [int]$fr[1] + 20
    $w = Get-Win $h.Pid 'ModuleDock'
    if ([RI]::Ours($fsx, $fsy, $h.Pid)) {
      [RI]::Move($fsx, $fsy); Start-Sleep -Milliseconds 300
      [RI]::Down(); Start-Sleep -Milliseconds 300
      Step-Move $h.Pid $fsx $fsy ($w.X + 120) ($w.Y + 200) 40
      Start-Sleep -Milliseconds 300
      [RI]::Up(); Start-Sleep -Milliseconds 900
      [RI]::Move(($workLeft + 300), ($workBottom - 200)); Start-Sleep -Milliseconds 300
      $left = [RI]::Windows($h.Pid) | ? { $_.Split('|')[1] -eq 'True' -and -not $_.StartsWith('ModuleDock|') }
      Check 'dragging the floating Clock back over the host docks it again' ((Order-Now $h.Pid) -contains 'Clock' -and -not $left) ((Order-Now $h.Pid) -join ',')
    }
  }
}
Stop-Host $h; Cleanup $h

""
if ($script:failures.Count -eq 0) { 'E2E: all checks passed' } else { "E2E: $($script:failures.Count) check(s) FAILED: " + ($script:failures -join '; '); exit 1 }
$mouseLock.ReleaseMutex()
