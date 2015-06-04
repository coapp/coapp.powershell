# CoApp Powershell Cmdlets.

### Building the code

Tools required:
===============

1. The sysinternal tools (handle in particular) should be part of your path

Building
========

1. Open solution in Visual Studio 2013
2. Right click on the solution, select "manage nuget packages", then click on the "download all pacakges". Note that it isn't actually enough to just build. Some packages won't come down.
2. Build all

Running
=======

1. cd into the output\Debug\AnyCPU\bin directory in powershell
2. issue the powershell "import-module .\coapp.psd1"

As a short-cut you can run powershell in the debugger:
1. In the CoApp.Powershell.Tools property, Debug tag, fill in:
	- The executable: C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe
	- The command line arguments enter: -NoExit -Command "Import-Module .\CoApp.psd1"
2. Hit "Start" and it should bring up a debugger attached and you can set break points.

Notes: 
 - Show-CoAppEtcDirectory will not work unless you have installed powershell. I've only seen it necessary to uninstall the real version of CoApp tools if you get an error message about conflicting types during the import-module command.
 - It is also useful to attach "powershell.exe" as the program to run when with the CoApp.Powershell project so you can just click "debug"

Suggested Workflow:
===================

0. Fork coApp.Powershell
1. Build the solution. This will modify a lot of sources that are stored in git
2. Move all the modified sources to the "excluded" list of files.
3. Modify and debug your solution, and check those changes into your fork.

Comments
========

`<coming soon>`

### Installing the tools 


