@echo off
setlocal
rem ---------------------------------------------------------------
rem  PptFigmaDrag build script - no SDK install required.
rem  Uses the C# compiler that ships with .NET Framework 4.x,
rem  which is preinstalled on every Windows 10/11 machine.
rem ---------------------------------------------------------------
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [ERROR] .NET Framework 4.x csc.exe not found.
    exit /b 1
)

rem /codepage:65001 - sources are UTF-8 (without BOM); old csc would otherwise
rem read them with the system ANSI codepage and garble Korean string literals.
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 ^
    /out:PptFigmaDrag.exe ^
    /win32manifest:src\app.manifest ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
    /r:System.Windows.Forms.dll /r:Microsoft.CSharp.dll ^
    src\*.cs

if errorlevel 1 (
    echo [ERROR] Build failed.
    exit /b 1
)

echo.
echo [OK] PptFigmaDrag.exe 생성 완료. 더블클릭하면 트레이에서 실행됩니다.
