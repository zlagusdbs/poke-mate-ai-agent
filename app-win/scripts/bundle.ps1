# 앱 묶기 (맥 app-mac/scripts/bundle.sh 의 Windows 판).
# 프레임워크 의존 publish (런타임을 내려받지 않고 이 PC 의 .NET Desktop Runtime 을 쓴다).
# 결과: app-win\build\PokeMate\PokeMate.exe
#   .\bundle.ps1
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out = Join-Path $app 'build\PokeMate'
# 사내 프록시 변수가 죽어 있으면 dotnet 이 기다리므로 이 프로세스에서만 뺀다
foreach ($name in 'http_proxy', 'https_proxy', 'HTTP_PROXY', 'HTTPS_PROXY') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
& dotnet publish (Join-Path $app 'src\PokeMate') -c Release --self-contained false -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패 (종료 코드 $LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $out 'PokeMate.exe'))) { throw "PokeMate.exe 가 만들어지지 않았다: $out" }
Write-Host "묶기 완료: $out"
