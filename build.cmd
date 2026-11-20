@echo off
rem Builds AutoCopy.exe with the csc.exe built into Windows (.NET Framework 4.8), no SDK needed.
cd /d "%~dp0"
taskkill /im AutoCopy.exe /f >nul 2>&1 && ping -n 2 127.0.0.1 >nul
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 ^
  /win32manifest:app.manifest /win32icon:AutoCopy.ico /out:AutoCopy.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll AutoCopy.cs
