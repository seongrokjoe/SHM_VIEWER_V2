@echo off
setlocal EnableDelayedExpansion

echo ============================================================
echo  SHM_VIEWER Release Build
echo ============================================================

:: ── 경로 설정 ──────────────────────────────────────────────
set ROOT=%~dp0
set PROJECT=%ROOT%ShmViewer\ShmViewer.csproj
set DIST=%ROOT%dist
set PUBLISH_DIR=%DIST%\ShmViewer

:: ── dotnet CLI 확인 ────────────────────────────────────────
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] dotnet CLI를 찾을 수 없습니다.
    echo         Visual Studio 2022 설치 후 다시 실행하세요.
    pause & exit /b 1
)

:: ── 이전 빌드 정리 ─────────────────────────────────────────
echo.
echo [1/3] 이전 빌드 정리 중...
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if exist "%DIST%\ShmViewer_Release.zip" del /q "%DIST%\ShmViewer_Release.zip"

:: ── Publish (framework-dependent, win-x64) ─────────────────
echo.
echo [2/3] Release 빌드 및 Publish 중...
dotnet publish "%PROJECT%" ^
    --configuration Release ^
    --runtime win-x64 ^
    --self-contained false ^
    --output "%PUBLISH_DIR%" ^
    -p:PublishSingleFile=false ^
    -p:DebugType=none ^
    -p:DebugSymbols=false
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
if not exist "%DIST%" mkdir "%DIST%"
powershell -NoProfile -Command ^
    "Compress-Archive -Path '%PUBLISH_DIR%\*' -DestinationPath '%DIST%\ShmViewer_Release.zip' -Force"
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
if exist "%DIST%\ShmViewer_Release.zip" (
    echo  ZIP 패키지  : %DIST%\ShmViewer_Release.zip
)
echo.
echo  배포 시 ShmViewer_Release.zip 을 대상 PC에 압축 해제 후
echo  ShmViewer.exe 를 실행하세요.
echo  (.NET 8 Desktop Runtime 이상 필요 - VS2022 설치 환경 기준 충족)
echo ============================================================
echo.
pause
endlocal
