@echo off
@chcp 65001 >nul
set DOTNET_CLI_UI_LANGUAGE=en

echo ============================================================
echo  SHM_VIEWER Release Build
echo ============================================================

:: ── 경로 설정 ──────────────────────────────────────────────
set ROOT=%~dp0
set PROJECT=%ROOT%ShmViewer\ShmViewer.csproj
set DIST=%ROOT%dist
set PUBLISH_DIR=%DIST%\ShmViewer

:: ── dotnet CLI 경로 탐색 (VS2022 설치 경로 우선) ─────────
set DOTNET=dotnet
if exist "%ProgramFiles%\dotnet\dotnet.exe" set DOTNET="%ProgramFiles%\dotnet\dotnet.exe"

%DOTNET% --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] dotnet CLI를 찾을 수 없습니다.
    echo         .NET SDK 설치 후 다시 실행하세요.
    pause & exit /b 1
)

:: ── 이전 빌드 정리 ─────────────────────────────────────────
echo.
echo [1/3] 이전 빌드 정리 중...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%DIST%\ShmViewer_Release.zip" del /q "%DIST%\ShmViewer_Release.zip"
if not exist "%DIST%" mkdir "%DIST%"

:: ── Publish (SingleFile, framework-dependent, win-x64) ────
echo.
echo [2/3] Release 빌드 및 Publish 중...
%DOTNET% publish "%PROJECT%" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o "%PUBLISH_DIR%"
if errorlevel 1 (
    echo [ERROR] 빌드 실패.
    pause & exit /b 1
)

:: ── 불필요한 파일 정리 ─────────────────────────────────────
echo.
echo [3/3] 패키지 정리 중...
if exist "%PUBLISH_DIR%\runtimes" rmdir /s /q "%PUBLISH_DIR%\runtimes"
if exist "%PUBLISH_DIR%\ShmViewer.pdb" del /q "%PUBLISH_DIR%\ShmViewer.pdb"

:: ── ZIP 생성 (PowerShell) ───────────────────────────────────
echo.
echo [ZIP] 배포 패키지 압축 중...
powershell -NoProfile -Command "Compress-Archive -Path '%PUBLISH_DIR%\*' -DestinationPath '%DIST%\ShmViewer_Release.zip' -Force"
if errorlevel 1 (
    echo [WARN] ZIP 생성 실패. 폴더 배포본은 정상 생성됨.
) else (
    echo [OK]  %DIST%\ShmViewer_Release.zip
)

:: ── 결과 출력 ──────────────────────────────────────────────
echo.
echo ============================================================
echo  빌드 완료
echo ============================================================
echo  폴더 배포본 : %PUBLISH_DIR%
echo  배포 파일   : ShmViewer.exe + libclang.dll + libClangSharp.dll
if exist "%DIST%\ShmViewer_Release.zip" echo  ZIP 패키지  : %DIST%\ShmViewer_Release.zip
echo.
echo  (.NET 8 Desktop Runtime 필요)
echo ============================================================
echo.
pause
