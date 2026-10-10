function Set-InstallerWindowIcon($Assembly) {
    $module = $Assembly.MainModule
    $setup = $module.Types | Where-Object Name -eq 'AirStereoSetup'
    $wizard = $setup.NestedTypes | Where-Object Name -eq 'WizardForm'
    $ctor = $wizard.Methods | Where-Object Name -eq '.ctor' | Select-Object -First 1
    if (-not $ctor -or -not $ctor.HasBody) { throw 'Installer wizard constructor missing.' }
    if (@($ctor.Body.Instructions | Where-Object { $_.Operand -and $_.Operand.ToString().Contains('::set_Icon(') }).Count) { return }
    # Retain .NET Framework metadata scopes; never import PowerShell runtime types.
    $forms = $module.AssemblyReferences | Where-Object Name -eq 'System.Windows.Forms'
    $drawing = $module.AssemblyReferences | Where-Object Name -eq 'System.Drawing'
    $application = [Mono.Cecil.TypeReference]::new('System.Windows.Forms','Application',$module,$forms)
    $form = [Mono.Cecil.TypeReference]::new('System.Windows.Forms','Form',$module,$forms)
    $icon = [Mono.Cecil.TypeReference]::new('System.Drawing','Icon',$module,$drawing)
    $path = [Mono.Cecil.MethodReference]::new('get_ExecutablePath',$module.TypeSystem.String,$application)
    $extract = [Mono.Cecil.MethodReference]::new('ExtractAssociatedIcon',$icon,$icon)
    $extract.Parameters.Add([Mono.Cecil.ParameterDefinition]::new($module.TypeSystem.String))
    $setter = [Mono.Cecil.MethodReference]::new('set_Icon',$module.TypeSystem.Void,$form)
    $setter.HasThis = $true
    $setter.Parameters.Add([Mono.Cecil.ParameterDefinition]::new($icon))
    $baseCall = $ctor.Body.Instructions | Where-Object { $_.Operand -and $_.Operand.ToString() -eq 'System.Void System.Windows.Forms.Form::.ctor()' } | Select-Object -First 1
    if (-not $baseCall) { throw 'Installer base constructor missing.' }
    $anchor = $baseCall.Next
    $il = $ctor.Body.GetILProcessor()
    $il.InsertBefore($anchor, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Ldarg_0))
    $il.InsertBefore($anchor, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call,$path))
    $il.InsertBefore($anchor, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call,$extract))
    $il.InsertBefore($anchor, [Mono.Cecil.Cil.Instruction]::Create([Mono.Cecil.Cil.OpCodes]::Call,$setter))
}
function Set-InstallerFileIcon([string]$Installer, [string]$IconPath) {
    if (-not ('AirStereoPackaging.IconResource' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace AirStereoPackaging {
 public static class IconResource {
  private delegate bool Names(IntPtr module,IntPtr type,IntPtr name,IntPtr param);
  private delegate bool Languages(IntPtr module,IntPtr type,IntPtr name,ushort language,IntPtr param);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool EnumResourceNames(IntPtr module,IntPtr type,Names callback,IntPtr param);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool EnumResourceLanguages(IntPtr module,IntPtr type,IntPtr name,Languages callback,IntPtr param);
  [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr BeginUpdateResource(string path,bool deleteExisting);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool UpdateResource(IntPtr update,IntPtr type,IntPtr name,ushort language,byte[] data,uint count);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool EndUpdateResource(IntPtr update,bool discard);
  private sealed class Entry { public int Type; public int Id; public string Name; public ushort Language; }
  public static void Apply(string path,string icoPath) {
   byte[] ico=File.ReadAllBytes(icoPath);
   if(ico.Length<6 || BitConverter.ToUInt16(ico,0)!=0 || BitConverter.ToUInt16(ico,2)!=1) throw new InvalidDataException("Invalid ICO");
   int count=BitConverter.ToUInt16(ico,4);
   if(count<1 || ico.Length<6+16*count) throw new InvalidDataException("Incomplete ICO");
   var images=new List<byte[]>();
   byte[] group=new byte[6+14*count]; Array.Copy(ico,0,group,0,6);
   for(int i=0;i<count;i++) {
    int p=6+16*i; uint length=BitConverter.ToUInt32(ico,p+8), offset=BitConverter.ToUInt32(ico,p+12);
    if(length==0 || (ulong)offset+length>(ulong)ico.Length) throw new InvalidDataException("Invalid image bounds");
    byte[] data=new byte[length]; Array.Copy(ico,(int)offset,data,0,(int)length); images.Add(data);
    Array.Copy(ico,p,group,6+14*i,12); Array.Copy(BitConverter.GetBytes((ushort)(i+1)),0,group,6+14*i+12,2);
   }
   var old=new List<Entry>(); IntPtr module=LoadLibraryEx(path,IntPtr.Zero,2);
   if(module==IntPtr.Zero) throw new Win32Exception();
   try { foreach(int type in new[]{3,14}) {
    Names names=(m,t,n,p)=> {
     int id=n.ToInt64()<=65535 ? n.ToInt32() : 0; string name=id==0 ? Marshal.PtrToStringUni(n) : null;
     Languages languages=(mm,tt,nn,lang,pp)=> {old.Add(new Entry{Type=type,Id=id,Name=name,Language=lang});return true;};
     return EnumResourceLanguages(m,t,n,languages,IntPtr.Zero);
    };
    if(!EnumResourceNames(module,new IntPtr(type),names,IntPtr.Zero)) {
     int error=Marshal.GetLastWin32Error(); if(error!=1813 && error!=1814) throw new Win32Exception(error);
    }
   }} finally {FreeLibrary(module);}
   IntPtr update=BeginUpdateResource(path,false); if(update==IntPtr.Zero) throw new Win32Exception(); bool committed=false;
   try {
    foreach(var entry in old) {
     IntPtr n=entry.Name==null ? new IntPtr(entry.Id) : Marshal.StringToHGlobalUni(entry.Name);
     try{if(!UpdateResource(update,new IntPtr(entry.Type),n,entry.Language,null,0)) throw new Win32Exception();}
     finally{if(entry.Name!=null)Marshal.FreeHGlobal(n);}
    }
    for(int i=0;i<count;i++) if(!UpdateResource(update,new IntPtr(3),new IntPtr(i+1),0,images[i],(uint)images[i].Length))throw new Win32Exception();
    if(!UpdateResource(update,new IntPtr(14),new IntPtr(1),0,group,(uint)group.Length))throw new Win32Exception();
    if(!EndUpdateResource(update,false))throw new Win32Exception(); committed=true;
   }finally{if(!committed)EndUpdateResource(update,true);}
  }
 }
}
'@
    }
    [AirStereoPackaging.IconResource]::Apply($Installer,$IconPath)
}
