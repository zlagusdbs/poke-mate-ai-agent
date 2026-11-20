# 리뷰 기준 문제 묶음 평가 (맥 app-mac/scripts/review-eval.sh 의 Windows 판).
# PokeMate.Eval 을 인자 그대로 넘겨 돌린다. 인자는 PokeMate.Eval --help 참고:
#   .\review-eval.ps1 [cases.json] [--case id]... [--out 결과 폴더] [--reuse always|default|분,토큰]
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# 사내 프록시 변수가 죽어 있으면 dotnet 이 기다리므로 이 프로세스에서만 뺀다
foreach ($name in 'http_proxy', 'https_proxy', 'HTTP_PROXY', 'HTTPS_PROXY') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
& dotnet run --project (Join-Path $app 'src\PokeMate.Eval') -- @args
exit $LASTEXITCODE
