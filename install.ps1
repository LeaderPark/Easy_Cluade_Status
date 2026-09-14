# Install (or update) ECS for the current user.
#
#   .\install.ps1                 install, and start it now
#   .\install.ps1 -Standalone     use the build that carries its own .NET runtime
#   .\install.ps1 -Uninstall      remove the files and the shortcuts
#
# Run-at-sign-in is toggled from the tray menu, not here.
#
# Everything lands under the user's own profile, so no administrator rights are needed.

param(
    [switch]$Standalone,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$AppName   = 'ECS'
$Target    = Join-Path $env:LOCALAPPDATA 'ECS'
$Exe       = Join-Path $Target 'ECS.exe'
$StartMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"
$StartupLnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup\$AppName.lnk"

function Stop-Running {
    Get-Process ECS -ErrorAction SilentlyContinue | ForEach-Object {
        $_.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 400
        if (-not $_.HasExited) { $_.Kill() }
    }
    Start-Sleep -Milliseconds 400
}

function New-Shortcut($Path) {
    New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($Path)
    $lnk.TargetPath = $Exe
    $lnk.WorkingDirectory = $Target
    $lnk.Description = 'Claude account usage widget'
    $lnk.Save()
}

if ($Uninstall) {
    Stop-Running
    foreach ($p in @($StartMenu, $StartupLnk)) { if (Test-Path $p) { Remove-Item $p -Force } }
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ECS' -ErrorAction SilentlyContinue
    if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
    Write-Host '제거했습니다. 설정과 캐시는 %APPDATA%\ECS 에 남아 있습니다.'
    return
}

$source = Join-Path $PSScriptRoot ($(if ($Standalone) { 'dist-standalone' } else { 'dist' }))
if (-not (Test-Path (Join-Path $source 'ECS.exe'))) {
    throw "빌드 결과가 없습니다: $source`n먼저 실행하세요: dotnet publish -c Release -r win-x64 --self-contained $($Standalone.IsPresent.ToString().ToLower()) -p:PublishSingleFile=true -o $(Split-Path $source -Leaf)"
}

Stop-Running
New-Item -ItemType Directory -Force -Path $Target | Out-Null
Copy-Item (Join-Path $source 'ECS.exe') $Exe -Force

New-Shortcut $StartMenu
# Older builds put a shortcut here; the app now uses the Run key instead.
if (Test-Path $StartupLnk) { Remove-Item $StartupLnk -Force }

$size = [Math]::Round((Get-Item $Exe).Length / 1MB, 2)
Write-Host "설치 완료: $Exe ($size MB)"
Write-Host "시작 메뉴: $AppName"
Write-Host '로그인 시 자동 실행: 트레이 메뉴의 Start with Windows 로 설정'

Start-Process $Exe
Write-Host '실행했습니다.'
