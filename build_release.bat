@echo off
setlocal
@chcp 65001 >nul
set "DOTNET_CLI_UI_LANGUAGE=en"

set "ROOT=%~dp0"
set "PROJECT=%ROOT%ShmViewer\ShmViewer.csproj"
set "DIST=%ROOT%dist"
set "PUBLISH_DIR=%DIST%\ShmViewer"
set "ZIP_PATH=%DIST%\ShmViewer_Release.zip"
set "NO_PAUSE="

if /I "%~1"=="--no-pause" set "NO_PAUSE=1"

echo ============================================================
echo  SHM_VIEWER Release Build
echo ============================================================

rem Resolve dotnet CLI.
set "DOTNET=dotnet"
if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"

"%DOTNET%" --version >nul 2>&1
if errorlevel 1 goto :dotnet_missing

echo.
echo [1/3] Cleaning previous release output...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%ZIP_PATH%" del /q "%ZIP_PATH%"
if not exist "%DIST%" mkdir "%DIST%"

echo.
echo [2/3] Publishing release build...
"%DOTNET%" publish "%PROJECT%" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o "%PUBLISH_DIR%"
if errorlevel 1 goto :publish_failed

echo.
echo [3/3] Finalizing package...
if exist "%PUBLISH_DIR%\runtimes" rmdir /s /q "%PUBLISH_DIR%\runtimes"
if exist "%PUBLISH_DIR%\ShmViewer.pdb" del /q "%PUBLISH_DIR%\ShmViewer.pdb"

if not exist "%PUBLISH_DIR%\ShmViewer.exe" goto :exe_missing

echo.
echo [ZIP] Creating release archive...
powershell -NoProfile -Command "Compress-Archive -Path '%PUBLISH_DIR%\*' -DestinationPath '%ZIP_PATH%' -Force"
if errorlevel 1 (
    echo [WARN] Failed to create ZIP archive. Folder output is still available.
) else (
    echo [OK]  %ZIP_PATH%
)

echo.
echo ============================================================
echo  Build Complete
echo ============================================================
echo  Release folder : %PUBLISH_DIR%
echo  Main executable: %PUBLISH_DIR%\ShmViewer.exe
if exist "%ZIP_PATH%" echo  ZIP package    : %ZIP_PATH%
echo.
echo  Requires .NET 8 Desktop Runtime.
echo ============================================================
echo.
goto :done

:dotnet_missing
echo [ERROR] dotnet CLI was not found.
echo         Install the .NET SDK and try again.
goto :fail

:publish_failed
echo [ERROR] Publish failed.
goto :fail

:exe_missing
echo [ERROR] ShmViewer.exe was not found in the publish output.
goto :fail

:fail
echo.
call :maybe_pause
exit /b 1

:done
call :maybe_pause
exit /b 0

:maybe_pause
if defined NO_PAUSE exit /b 0
pause
exit /b 0

