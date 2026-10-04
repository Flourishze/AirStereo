# Preserve a matching application VERSIONINFO in the managed installer PE.
function Set-InstallerFileVersion([string]$Installer, [string]$Application, [string]$Version) {
    if (-not ('AirStereoPackaging.VersionResource' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class AirStereoPackagingMarker { }
namespace AirStereoPackaging {
 public static class VersionResource {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
  [DllImport("kernel32.dll", SetLastError=true)] static extern uint SizeofResource(IntPtr module, IntPtr resource);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr LockResource(IntPtr resource);
  [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr BeginUpdateResource(string file, bool deleteExisting);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint length);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool EndUpdateResource(IntPtr update, bool discard);
  public static void Copy(string application, string installer) {
   var module=LoadLibraryEx(application, IntPtr.Zero, 2);
   if(module==IntPtr.Zero) throw new Win32Exception();
   byte[] bytes;
   try {
    var resource=FindResource(module,new IntPtr(1),new IntPtr(16));
    if(resource==IntPtr.Zero) throw new Win32Exception();
    var size=SizeofResource(module,resource);
    var memory=LockResource(LoadResource(module,resource));
    if(memory==IntPtr.Zero || size==0) throw new Win32Exception();
    bytes=new byte[size]; Marshal.Copy(memory,bytes,0,bytes.Length);
   } finally { FreeLibrary(module); }
   var update=BeginUpdateResource(installer,false);
   if(update==IntPtr.Zero) throw new Win32Exception();
   bool committed=false;
   try {
    // Replace the neutral wizard resource and provide the matching English resource.
    if(!UpdateResource(update,new IntPtr(16),new IntPtr(1),0,bytes,(uint)bytes.Length)) throw new Win32Exception();
    if(!UpdateResource(update,new IntPtr(16),new IntPtr(1),0x409,bytes,(uint)bytes.Length)) throw new Win32Exception();
    if(!EndUpdateResource(update,false)) throw new Win32Exception();
    committed=true;
   } finally { if(!committed) EndUpdateResource(update,true); }
  }
 }
}
'@
    }
    [AirStereoPackaging.VersionResource]::Copy($Application, $Installer)
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Installer)
    if ($info.FileVersion -ne "$Version.0" -or $info.ProductVersion -ne $Version) {
        throw 'Installer PE version metadata mismatch.'
    }
}
