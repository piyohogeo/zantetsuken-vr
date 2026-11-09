#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$TemplateExecutable,
    [string]$VcVars='C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
)
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
$template=[IO.Path]::GetFullPath($TemplateExecutable)
foreach($file in @($template,$VcVars)){if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw 'A bootstrap build input is missing.'}}
[IO.Directory]::CreateDirectory($output)|Out-Null
$debugRoot=Join-Path $output 'CutWorldSandbox_BackUpThisFolder_ButDontShipItWithYourGame'
$scratch=Join-Path $debugRoot ('b-'+[Guid]::NewGuid().ToString('N').Substring(0,12))
[IO.Directory]::CreateDirectory($scratch)|Out-Null
$templateCopy=Join-Path $scratch 'UnityPlayerTemplate.exe'
Copy-Item -LiteralPath $template -Destination $templateCopy
$staged=Join-Path $scratch 'CutWorldSandbox.exe'
$environment=@(& cmd.exe /d /s /c ('"'+$VcVars+'" >nul && set'))
if($LASTEXITCODE -ne 0){throw 'The x64 compiler environment could not be prepared.'}
$held=@{}
try {
    foreach($name in @('PATH','INCLUDE','LIB','LIBPATH')){
        $entry=@($environment|Where-Object {$_ -match ('(?i)^'+$name+'=')})
        if($entry.Count -ne 1){throw 'The compiler environment is incomplete.'}
        $held[$name]=[Environment]::GetEnvironmentVariable($name,'Process')
        [Environment]::SetEnvironmentVariable($name,$entry[0].Substring($name.Length+1),'Process')
    }
    $compiler=(Get-Command cl.exe -ErrorAction Stop).Source
    $arguments=@('/nologo','/MT','/O2','/EHsc','/W4','/WX','/std:c++17','/utf-8','/DUNICODE','/D_UNICODE',
        (Join-Path $PSScriptRoot 'PlayerBootstrap.cpp'),'/FoPlayerBootstrap.obj',
        '/FeCutWorldSandbox.exe','/link','/SUBSYSTEM:WINDOWS','/DEBUG:NONE','/IMPLIB:CutWorldSandbox.lib','user32.lib')
    Push-Location -LiteralPath $scratch
    try {
        & $compiler @arguments
        if($LASTEXITCODE -ne 0){throw 'Native Player bootstrap compilation failed.'}
    }
    finally { Pop-Location }
}
finally {
    foreach($name in $held.Keys){[Environment]::SetEnvironmentVariable($name,$held[$name],'Process')}
}
if(-not('Zantetsu.Build.BootstrapResources' -as [type])){
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace Zantetsu.Build
{
    public static class BootstrapResources
    {
        private delegate bool Types(IntPtr module, IntPtr type, IntPtr parameter);
        private delegate bool Names(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);
        private delegate bool Languages(IntPtr module, IntPtr type, IntPtr name, ushort language, IntPtr parameter);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", EntryPoint="EnumResourceTypesW", SetLastError=true)]
        private static extern bool EnumerateTypes(IntPtr module, Types callback, IntPtr parameter);
        [DllImport("kernel32.dll", EntryPoint="EnumResourceNamesW", SetLastError=true)]
        private static extern bool EnumerateNames(IntPtr module, IntPtr type, Names callback, IntPtr parameter);
        [DllImport("kernel32.dll", EntryPoint="EnumResourceLanguagesW", SetLastError=true)]
        private static extern bool EnumerateLanguages(IntPtr module, IntPtr type, IntPtr name, Languages callback, IntPtr parameter);
        [DllImport("kernel32.dll", EntryPoint="FindResourceExW", SetLastError=true)]
        private static extern IntPtr FindResource(IntPtr module, IntPtr type, IntPtr name, ushort language);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LockResource(IntPtr resource);
        [DllImport("kernel32.dll", EntryPoint="BeginUpdateResourceW", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern IntPtr BeginUpdate(string filename, bool deleteExisting);
        [DllImport("kernel32.dll", EntryPoint="UpdateResourceW", SetLastError=true)]
        private static extern bool Update(IntPtr update, IntPtr type, IntPtr name, ushort language, IntPtr data, uint bytes);
        [DllImport("kernel32.dll", EntryPoint="EndUpdateResourceW", SetLastError=true)]
        private static extern bool EndUpdate(IntPtr update, bool discard);
        private static string Identifier(IntPtr value)
        {
            return value.ToInt64() <= 0xffff ? "#" + value.ToInt64() : Marshal.PtrToStringUni(value);
        }
        private static List<string> Visit(string filename, IntPtr update)
        {
            IntPtr module=LoadLibraryEx(filename,IntPtr.Zero,0x20|0x40);
            if(module==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            var records=new List<string>();
            Exception failure=null;
            try
            {
                Languages languages=delegate(IntPtr loaded,IntPtr type,IntPtr name,ushort language,IntPtr ignored)
                {
                    try
                    {
                        IntPtr resource=FindResource(loaded,type,name,language);
                        if(resource==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
                        uint length=SizeofResource(loaded,resource);
                        if(length>int.MaxValue)throw new InvalidOperationException("Resource is too large.");
                        IntPtr memory=LockResource(LoadResource(loaded,resource));
                        if(length!=0 && memory==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
                        byte[] bytes=new byte[length];
                        if(length!=0)Marshal.Copy(memory,bytes,0,(int)length);
                        if(update!=IntPtr.Zero)
                        {
                            IntPtr copy=Marshal.AllocHGlobal(Math.Max(1,(int)length));
                            try
                            {
                                if(length!=0)Marshal.Copy(bytes,0,copy,(int)length);
                                if(!Update(update,type,name,language,copy,length))throw new Win32Exception(Marshal.GetLastWin32Error());
                            }
                            finally{Marshal.FreeHGlobal(copy);}
                        }
                        using(var hash=SHA256.Create())
                            records.Add(Identifier(type)+"|"+Identifier(name)+"|"+language+"|"+length+"|"+BitConverter.ToString(hash.ComputeHash(bytes)));
                        return true;
                    }
                    catch(Exception error){failure=error;return false;}
                };
                Names names=delegate(IntPtr loaded,IntPtr type,IntPtr name,IntPtr ignored)
                {
                    return EnumerateLanguages(loaded,type,name,languages,IntPtr.Zero);
                };
                Types types=delegate(IntPtr loaded,IntPtr type,IntPtr ignored)
                {
                    return EnumerateNames(loaded,type,names,IntPtr.Zero);
                };
                if(!EnumerateTypes(module,types,IntPtr.Zero))
                {
                    if(failure!=null)throw failure;
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                records.Sort(StringComparer.Ordinal);
                return records;
            }
            finally{FreeLibrary(module);}
        }
        public static int CopyAndVerify(string template,string destination)
        {
            IntPtr update=BeginUpdate(destination,true);
            if(update==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            List<string> original;
            try
            {
                original=Visit(template,update);
                if(!EndUpdate(update,false))throw new Win32Exception(Marshal.GetLastWin32Error());
                update=IntPtr.Zero;
            }
            finally{if(update!=IntPtr.Zero)EndUpdate(update,true);}
            List<string> copied=Visit(destination,IntPtr.Zero);
            if(original.Count==0 || original.Count!=copied.Count || string.Join("\n",original)!=string.Join("\n",copied))
                throw new InvalidOperationException("The native bootstrap resources differ from the generated Player.");
            return copied.Count;
        }
    }
}
'@
}
$count=[Zantetsu.Build.BootstrapResources]::CopyAndVerify($templateCopy,$staged)
$final=Join-Path $output 'CutWorldSandbox.exe'
$before=if(Test-Path -LiteralPath $final){(Get-FileHash -LiteralPath $final).Hash}else{$null}
if($before){[IO.File]::Replace($staged,$final,(Join-Path $scratch 'PreviousPlayer.exe'))}else{[IO.File]::Move($staged,$final)}
$record=[ordered]@{TemplateSha256=(Get-FileHash -LiteralPath $templateCopy).Hash;OutputSha256=(Get-FileHash -LiteralPath $final).Hash;ResourceEntriesVerified=$count;PreviousOutputSha256=$before;D3D12SDKVersion=618;D3D12SDKPath='.\D3D12\'}
[IO.File]::WriteAllText((Join-Path $scratch 'build-record.json'),($record|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
Write-Output ('Native Player bootstrap built; resources verified='+$count+'; SHA256='+$record.OutputSha256)
