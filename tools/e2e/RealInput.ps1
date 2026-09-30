$ErrorActionPreference = 'Stop'
Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class RI {
  public delegate bool EP(IntPtr h, IntPtr l);
  public delegate bool MEP(IntPtr h, IntPtr d, ref RECT r, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X,Y; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct MI { public int cb; public RECT mon; public RECT work; public uint flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dev; }
  [DllImport("user32.dll")] static extern bool EnumWindows(EP p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern bool GetCursorPos(out PT p);
  [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PT p);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MEP p, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MI i);
  public static List<string> Monitors() { var r = new List<string>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr d, ref RECT rc, IntPtr l) => { var i = new MI{cb=Marshal.SizeOf(typeof(MI))}; GetMonitorInfo(h, ref i);
      r.Add(i.work.L+","+i.work.T+","+i.work.R+","+i.work.B+","+((i.flags&1)!=0?"primary":"")); return true; }, IntPtr.Zero); return r; }
  public static List<string> Windows(uint pid) { var res = new List<string>();
    EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h,out p); if(p==pid){ RECT r; GetWindowRect(h,out r); var sb=new StringBuilder(256); GetWindowText(h,sb,256);
      if (sb.Length > 0) res.Add(sb+"|"+IsWindowVisible(h)+"|"+r.L+","+r.T+","+(r.R-r.L)+","+(r.B-r.T)); } return true;}, IntPtr.Zero); return res; }
  public static bool Ours(int x, int y, uint pid) { PT p; p.X=x; p.Y=y; var h = WindowFromPoint(p); if (h==IntPtr.Zero) return false; var root = GetAncestor(h, 2); uint owner; GetWindowThreadProcessId(root, out owner); return owner==pid; }
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
  public static void Topmost(uint pid, string title) {
    EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h,out p); if(p==pid){ var sb=new StringBuilder(256); GetWindowText(h,sb,256);
      if (sb.ToString()==title) SetWindowPos(h, new IntPtr(-1), 0,0,0,0, 0x0001|0x0002|0x0010); } return true;}, IntPtr.Zero); }
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
  // absolute move as a real input event (SetCursorPos alone does not feed window move loops)
  public static void Move(int x, int y) {
    int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
    uint ax = (uint)(x * 65535L / (w - 1)), ay = (uint)(y * 65535L / (h - 1));
    mouse_event(0x0001 | 0x8000, ax, ay, 0, UIntPtr.Zero);
  }
  public static void Down() { mouse_event(0x0002,0,0,0,UIntPtr.Zero); }
  public static void Up() { mouse_event(0x0004,0,0,0,UIntPtr.Zero); }
}
'@

function Get-Win($pid1, $title) { [RI]::Windows([uint32]$pid1) | ? { $_.StartsWith($title + '|') } | % { $f = $_.Split('|'); $r = $f[2].Split(','); [pscustomobject]@{ Visible = ($f[1] -eq 'True'); X=[int]$r[0]; Y=[int]$r[1]; W=[int]$r[2]; H=[int]$r[3] } } | Select -First 1 }
function Step-Move($pid1, $x0, $y0, $x1, $y1, $n) {
  for ($i = 1; $i -le $n; $i++) {
    $x = [int]($x0 + ($x1 - $x0) * $i / $n); $y = [int]($y0 + ($y1 - $y0) * $i / $n)
    [RI]::Move($x, $y); Start-Sleep -Milliseconds 15
  }
}
