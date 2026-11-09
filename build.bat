@echo off
rem Builds Animation & Timing into dist\: the window (C#, the compiler that ships with Windows' .NET Framework 4) with the
rem in-game script and the icon beside it. Run install.ps1 afterwards for the desktop icon. Then
rem `dist\AnimTiming.exe --selftest build\selftest` checks the parts that need no game.
setlocal
set HERE=%~dp0
if not exist "%HERE%dist" mkdir "%HERE%dist"
if not exist "%HERE%build" mkdir "%HERE%build"

"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /win32icon:"%HERE%app\anim_timing.ico" ^
   /out:"%HERE%dist\AnimTiming.exe" "%HERE%app\*.cs"
if errorlevel 1 (echo APP BUILD FAILED & exit /b 1)
copy /y "%HERE%app\anim_timing.ico" "%HERE%dist\" >nul
copy /y "%HERE%app\anim_timing_backdrop.png" "%HERE%dist\" >nul
copy /y "%HERE%reframework\autorun\anim_timing.lua" "%HERE%dist\" >nul
copy /y "%HERE%install.ps1" "%HERE%dist\" >nul
echo BUILD OK: %HERE%dist
