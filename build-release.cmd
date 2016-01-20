@echo off
setlocal
cd %~dp0

set TOOLSDIR=%~dp0\tools
set ILREPACKDIR=%~dp0\packages\ILRepack.2.0.10\tools

:: Scrub the output directories clea
msbuild /t:Clean ClrPlus.sln /p:Configuration=Release /p:TargetFrameworkVersion=v4.0 || goto failed
msbuild /t:Clean ClrPlus.sln /p:Configuration=Release /p:TargetFrameworkVersion=v4.5 || goto failed

rmdir /s /q output\v40
rmdir /s /q output\v45
rmdir /s /q intermediate\v40
rmdir /s /q intermediate\v45

:: Build .NET 4.0 and 4.5 versions
msbuild /t:Rebuild ClrPlus.sln /p:Configuration=Release /p:TargetFrameworkVersion=v4.0 || goto failed
msbuild /t:Rebuild ClrPlus.sln /p:Configuration=Release /p:TargetFrameworkVersion=v4.5 || goto failed

:: Do merges for monolithic packages
pushd output\v40\AnyCPU\Release\bin
echo Merging Monolithic build for .NET 4.0 [ClrPlus.Powershell.dll]
%ILREPACKDIR%\ILRepack.exe /log:merge.log /t:library /xmldocs /targetplatform:v4 /lib:"C:\Program Files (x86)\Reference Assemblies\Microsoft\WindowsPowerShell\3.0" /out:ClrPlus.Powershell.dll  ClrPlus.Core.dll ClrPlus.Scripting.dll ClrPlus.Scripting.MsBuild.dll ClrPlus.Platform.dll ClrPlus.Windows.Api.dll ClrPlus.Powershell.Core.dll || goto failed

:: rem skip ClrPlus.Powershell.Azure.dll for now.
echo Merging Monolithic build for .NET 4.0 [ClrPlus.dll]
%ILREPACKDIR%\ILRepack.exe /log:merge.log /t:library /xmldocs /targetplatform:v4 /lib:"C:\Program Files (x86)\Reference Assemblies\Microsoft\WindowsPowerShell\3.0" /out:ClrPlus.dll ClrPlus.CommandLine.dll ClrPlus.Console.dll ClrPlus.Core.dll  ClrPlus.Crypto.dll ClrPlus.Networking.dll ClrPlus.Platform.dll ClrPlus.Powershell.Core.dll ClrPlus.Powershell.Provider.dll ClrPlus.Remoting.dll ClrPlus.Scripting.dll ClrPlus.Scripting.MsBuild.dll ClrPlus.Windows.Api.dll ClrPlus.Windows.Debugging.dll ClrPlus.Windows.PeBinary.dll Microsoft.Cci.MetadataHelper.dll Microsoft.Cci.MetadataModel.dll Microsoft.Cci.MutableMetadataModel.dll Microsoft.Cci.PeReader.dll Microsoft.Cci.PeWriter.dll Microsoft.Cci.SourceModel.dll Microsoft.WindowsAzure.Storage.dll System.Spatial.dll  ClrPlus.Powershell.Azure.dll Microsoft.Threading.Tasks.dll Microsoft.Threading.Tasks.Extensions.dll Microsoft.Threading.Tasks.Extensions.Desktop.dll System.Runtime.dll System.Threading.Tasks.dll  || goto failed

REM for %%v in (clrplus*.dll) do "C:\Program Files (x86)\Windows Kits\8.0\bin\x86\signtool.exe" sign /a /t http://timestamp.verisign.com/scripts/timstamp.dll %%v || goto fin
popd 
        
echo Merging Monolithic builds for .NET 4.5
pushd output\v45\AnyCPU\Release\bin

echo Merging Monolithic build for .NET 4.5 [ClrPlus.Powershell.dll]
%ILREPACKDIR%\ILRepack.exe /log:merge.log /t:library /xmldocs /targetplatform:v4 /lib:"C:\Program Files (x86)\Reference Assemblies\Microsoft\WindowsPowerShell\3.0" /out:ClrPlus.Powershell.dll ClrPlus.Powershell.Azure.dll  ClrPlus.Core.dll ClrPlus.Scripting.dll ClrPlus.Scripting.MsBuild.dll ClrPlus.Platform.dll ClrPlus.Windows.Api.dll ClrPlus.Powershell.Core.dll || goto failed

:: rem skip ClrPlus.Powershell.Azure.dll for now.
echo Merging Monolithic build for .NET 4.5 [ClrPlus.dll]
%ILREPACKDIR%\ILRepack.exe /log:merge.log /t:library /xmldocs /targetplatform:v4 /lib:"C:\Program Files (x86)\Reference Assemblies\Microsoft\WindowsPowerShell\3.0" /out:ClrPlus.dll ClrPlus.CommandLine.dll ClrPlus.Console.dll ClrPlus.Core.dll  ClrPlus.Crypto.dll ClrPlus.Networking.dll ClrPlus.Platform.dll ClrPlus.Powershell.Core.dll ClrPlus.Powershell.Provider.dll  ClrPlus.Remoting.dll ClrPlus.Scripting.dll ClrPlus.Scripting.MsBuild.dll ClrPlus.Windows.Api.dll ClrPlus.Powershell.Azure.dll ClrPlus.Windows.Debugging.dll ClrPlus.Windows.PeBinary.dll Microsoft.Cci.MetadataHelper.dll Microsoft.Cci.MetadataModel.dll Microsoft.Cci.MutableMetadataModel.dll Microsoft.Cci.PeReader.dll Microsoft.Cci.PeWriter.dll Microsoft.Cci.SourceModel.dll Microsoft.WindowsAzure.Storage.dll System.Spatial.dll  || goto failed

REM for %%v in (clrplus*.dll) do "C:\Program Files (x86)\Windows Kits\8.0\bin\x86\signtool.exe" sign /a /t http://timestamp.verisign.com/scripts/timstamp.dll %%v || goto fin
popd


nuget pack  clrplus\Core\Core.nuspec
nuget pack  clrplus\Windows.Api\Windows.Api.nuspec
nuget pack  clrplus\Platform\Platform.nuspec
nuget pack  clrplus\CommandLine\CommandLine.nuspec
nuget pack  clrplus\Console\Console.nuspec
nuget pack  clrplus\Crypto\Crypto.nuspec
nuget pack  clrplus\Networking\Networking.nuspec
nuget pack  clrplus\Remoting\Remoting.nuspec
nuget pack  clrplus\Scripting\Scripting.nuspec
nuget pack  clrplus\Scripting.MsBuild\Scripting.MsBuild.nuspec
nuget pack  clrplus\Windows.Debugging\Windows.Debugging.nuspec
nuget pack  clrplus\Windows.PeBinary\Windows.PeBinary.nuspec        
nuget pack  clrplus\Powershell.Core\Powershell.Core.nuspec        
rem nuget pack  clrplus\Powershell.Rest\Powershell.Rest.nuspec        
nuget pack  clrplus\Powershell.Provider\Powershell.Provider.nuspec        
nuget pack  clrplus\Powershell.Azure\Powershell.Azure.nuspec        
nuget pack  clrplus\ClrPlus.Powershell.nuspec   
nuget pack  clrplus\ClrPlus.nuspec  

goto FIN

:failed
echo ===== ERROR =======

:FIN
