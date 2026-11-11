param([Parameter(Mandatory)][string]$ProgramDirectory)
$ErrorActionPreference = 'Stop'
if ($env:CI -ne 'true' -or -not $IsWindows) { throw 'Read-only probe diagnostics require disposable Windows CI.' }
$owned = @(Get-Process | Where-Object {
    try { $_.Path -and [IO.Path]::GetDirectoryName($_.Path).Equals($ProgramDirectory, [StringComparison]::OrdinalIgnoreCase) }
    catch { $false }
})
$owned | Select-Object Id, ProcessName, MainWindowTitle, Responding | Format-Table -AutoSize | Out-String | Write-Host
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class DailyToolkitOfficeProbeWindows {
    delegate bool EnumWindow(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindow callback, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr window, EnumWindow callback, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    static string Read(IntPtr window) { var text=new StringBuilder(4096); GetWindowText(window,text,text.Capacity); return text.ToString(); }
    public static string[] Capture(int[] processes) {
        var owned=new HashSet<int>(processes); var text=new List<string>();
        EnumWindows((window,data)=>{
            uint pid; GetWindowThreadProcessId(window,out pid);
            if(owned.Contains((int)pid)) {
                text.Add("Owned window: "+Read(window));
                EnumChildWindows(window,(child,unused)=>{ var value=Read(child); if(value.Length>0) text.Add(value); return true; },IntPtr.Zero);
            }
            return true;
        },IntPtr.Zero);
        return text.ToArray();
    }
}
'@
if ($owned.Count) { [DailyToolkitOfficeProbeWindows]::Capture([int[]]$owned.Id) | Write-Host }
