@echo off
REM Builds the standalone msd tool natively on Windows (requires cmake and
REM either Visual Studio's C++ build tools or another CMake-supported
REM compiler on PATH) and drops it into Tools\win-x64\msd.exe.
setlocal
cd /d "%~dp0"

rmdir /s /q build-windows-msvc 2>nul
cmake -S . -B build-windows-msvc -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 exit /b 1
cmake --build build-windows-msvc --config Release
if errorlevel 1 exit /b 1

mkdir ..\..\Tools\win-x64 2>nul
copy /y build-windows-msvc\Release\msd.exe ..\..\Tools\win-x64\msd.exe >nul
if not exist ..\..\Tools\win-x64\msd.exe copy /y build-windows-msvc\msd.exe ..\..\Tools\win-x64\msd.exe >nul

echo Built Tools\win-x64\msd.exe
