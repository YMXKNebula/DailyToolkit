param([string]$Executable)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Executable) {
    [xml]$properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $Executable = Join-Path $projectRoot ('artifacts\DailyToolkit-' + [string]$properties.Project.PropertyGroup.Version + '-win-x64\DailyToolkit.exe')
}
$Executable = [IO.Path]::GetFullPath($Executable)
if (-not $Executable.StartsWith($projectRoot + '\',[StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($Executable) -ne 'DailyToolkit.exe' -or -not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
    throw 'Expected an existing DailyToolkit.exe inside this workspace.'
}
if (-not ('DailyToolkitExplorerRefresh' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
[ComImport,Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface DailyToolkitShellItem {
    [PreserveSig] int BindToHandler(IntPtr context,ref Guid handler,ref Guid iid,out DailyToolkitIconExtractor extractor);
}
[ComImport,Guid("000214fa-0000-0000-c000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface DailyToolkitIconExtractor {
    [PreserveSig] int GetIconLocation(uint flags,[Out,MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,uint count,out int index,out uint attributes);
}
public sealed class DailyToolkitIconLocation {
    public string File;
    public int Index;
    public uint Flags;
}
[StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
public struct DailyToolkitShellInfo {
    public IntPtr Icon;
    public int Index;
    public uint Attributes;
    [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string DisplayName;
    [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)] public string TypeName;
}
public static class DailyToolkitExplorerRefresh {
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)]
    public static extern void SHCreateItemFromParsingName(string path,IntPtr context,ref Guid iid,out DailyToolkitShellItem item);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfoW(string path,uint attributes,out DailyToolkitShellInfo info,uint size,uint flags);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
    public static extern void SHUpdateImageW(string path,int index,uint flags,int imageIndex);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
    public static extern void SHChangeNotify(int events,uint flags,string item,string second);
    public static DailyToolkitIconLocation GetLocation(string path) {
        Guid shellItemId=new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        Guid handler=new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5");
        Guid extractorId=new Guid("000214fa-0000-0000-c000-000000000046");
        DailyToolkitShellItem item;
        SHCreateItemFromParsingName(path,IntPtr.Zero,ref shellItemId,out item);
        DailyToolkitIconExtractor extractor=null;
        try {
            Marshal.ThrowExceptionForHR(item.BindToHandler(IntPtr.Zero,ref handler,ref extractorId,out extractor));
            var file=new StringBuilder(1024); int index; uint flags;
            int result=extractor.GetIconLocation(2,file,1024,out index,out flags);
            Marshal.ThrowExceptionForHR(result);
            if (result!=0 || file.Length==0) throw new InvalidOperationException("No specific icon location returned.");
            return new DailyToolkitIconLocation { File=file.ToString(),Index=index,Flags=flags };
        } finally {
            if (extractor!=null) Marshal.ReleaseComObject(extractor);
            Marshal.ReleaseComObject(item);
        }
    }
}
'@
}
$size = [uint32][Runtime.InteropServices.Marshal]::SizeOf([type][DailyToolkitShellInfo])
$cached = [DailyToolkitShellInfo]::new()
$location = [DailyToolkitExplorerRefresh]::GetLocation($Executable)
if ([DailyToolkitExplorerRefresh]::SHGetFileInfoW($Executable,0,[ref]$cached,$size,0x4000) -eq [IntPtr]::Zero) {
    throw 'The Shell did not return the executable icon location.'
}
if (($location.Flags -band 8) -eq 0 -and -not [IO.Path]::GetFullPath($location.File).Equals($Executable,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Shell returned an unexpected icon source.'
}
# Refresh just this executable's cached image and its containing folder.
[DailyToolkitExplorerRefresh]::SHUpdateImageW($location.File,$location.Index,($location.Flags -band 9),$cached.Index)
[DailyToolkitExplorerRefresh]::SHChangeNotify(0x2000,0x1005,$Executable,$null)
$folder = [IO.Path]::GetDirectoryName($Executable)
[DailyToolkitExplorerRefresh]::SHChangeNotify(0x1000,0x1005,$folder,$null)
[pscustomobject]@{ Executable=$Executable; SystemImageIndex=$cached.Index; IconResourceIndex=$location.Index; IconLocationFlags=$location.Flags; FolderNotified=$folder }
