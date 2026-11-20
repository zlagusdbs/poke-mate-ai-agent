# 설치 (맥의 bundle.sh 뒤 /Applications 복사에 해당).
# 실행 중인 PokeMate.exe 를 끄고 app-win\build\PokeMate 를 %LOCALAPPDATA%\Programs\PokeMate 로 복사하고
# 시작 메뉴 바로 가기 PokeMate.lnk 를 만든다. 먼저 .\bundle.ps1 을 실행해 둔다. 앱은 켜지 않는다.
#   .\install.ps1
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$src = Join-Path $app 'build\PokeMate'
if (-not (Test-Path (Join-Path $src 'PokeMate.exe'))) { throw "묶은 결과가 없다. 먼저 bundle.ps1 을 실행한다: $src" }
$dest = Join-Path $env:LOCALAPPDATA 'Programs\PokeMate'

# 설치 폴더의 PokeMate.exe 만 끈다 (다른 프로세스는 건드리지 않는다)
$destExe = Join-Path $dest 'PokeMate.exe'
$running = @(Get-Process -Name PokeMate -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $destExe })
foreach ($p in $running) {
    Write-Host "실행 중인 PokeMate (PID $($p.Id)) 를 끈다"
    if (-not $p.CloseMainWindow()) { }
    if (-not $p.WaitForExit(5000)) { Stop-Process -Id $p.Id -Force; $p.WaitForExit(5000) | Out-Null }
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Path (Join-Path $src '*') -Destination $dest -Recurse -Force
Write-Host "복사 완료: $dest"

$programs = Join-Path ([Environment]::GetFolderPath('Programs')) 'PokeMate.lnk'
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($programs)
$lnk.TargetPath = $destExe
$lnk.WorkingDirectory = $dest
$lnk.IconLocation = "$destExe,0"
$lnk.Description = 'PokeMate'
$lnk.Save()
Write-Host "시작 메뉴 바로 가기: $programs"
