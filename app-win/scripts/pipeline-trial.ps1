# Jira 역할별 처리 시험 (맥 app-mac/scripts/pipeline-trial.sh 의 Windows 판).
# 가상 Jira(JSON 파일)와 로컬 bare git 저장소만 쓴다: 회사 Jira·GitHub 는 건드리지 않는다.
# 시험 폴더를 만들고(이전 시험 폴더는 지운다) PokeMate.Trial 로 PM -> 개발 -> 테스터 -> 리뷰어 -> push 를 돌린다.
#
# 사용: .\pipeline-trial.ps1 -Root <시험 폴더> [-Prompts <프롬프트 폴더>] [-ClaudePath <claude 실행 파일>] [-RepoDir <저장소 폴더>]
#   -Root        시험 폴더(없으면 만든다). 이미 시험 폴더(jira, pipelines 가 든 폴더)면 지우고 다시 만든다. 다른 폴더는 지우지 않고 멈춘다.
#   -Prompts     역할 프롬프트 폴더. 기본은 저장소의 shared\prompts
#   -ClaudePath  시험 폴더의 data\settings.json 에 넣을 claude 경로. 없으면 설정 기본값(%USERPROFILE%\.local\bin\claude.exe)을 쓴다.
#                실제 Claude 로 돌리려면 시험 데이터 폴더(<Root>\data\claude-config)에 앱 전용 로그인이 있어야 한다.
#   -RepoDir     data\settings.json 의 repoDir (uv 가 mcp-server 를 찾는 곳). 없으면 설정 기본값
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [string]$Prompts,
    [string]$ClaudePath,
    [string]$RepoDir
)
$ErrorActionPreference = 'Stop'
$app = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Prompts) { $Prompts = (Resolve-Path (Join-Path $app '..\shared\prompts')).Path }
$Root = [System.IO.Path]::GetFullPath($Root)
$Key = 'ACME-101'
$Repo = 'acme-sample-service'

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') 실패 (종료 코드 $LASTEXITCODE)" }
}

# BOM 없는 UTF-8, 줄 끝 LF 로 쓴다 (git·python 이 그대로 읽는다)
function Write-Utf8([string]$Path, [string]$Text) {
    $dir = Split-Path -Parent $Path
    if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [System.IO.File]::WriteAllText($Path, ($Text -replace "`r`n", "`n"), (New-Object System.Text.UTF8Encoding $false))
}

# 시험 폴더 지우기: 이전 시험 폴더만. 드라이브 루트·홈 폴더·저장소 같은 곳을 잘못 줬을 때 지우지 않는다.
if (Test-Path -LiteralPath $Root) {
    $isTrial = (Test-Path -LiteralPath (Join-Path $Root 'jira')) -and (Test-Path -LiteralPath (Join-Path $Root 'pipelines'))
    $isEmpty = -not (Get-ChildItem -LiteralPath $Root -Force | Select-Object -First 1)
    if (-not ($isTrial -or $isEmpty)) { throw "$Root 는 이전 시험 폴더가 아니라서 지우지 않아요. 새 폴더를 주세요." }
    if ($isTrial) { Remove-Item -LiteralPath $Root -Recurse -Force }
}
foreach ($dir in 'workspace', 'remotes', 'jira', 'data', 'pipelines') { New-Item -ItemType Directory -Force (Join-Path $Root $dir) | Out-Null }

$bare = Join-Path $Root "remotes\$Repo.git"
Invoke-Git init -q --bare -b main $bare
$W = Join-Path $Root "workspace\$Repo"
Invoke-Git init -q -b main $W
Invoke-Git -C $W config user.name 'Sample Bot'
Invoke-Git -C $W config user.email 'sample-bot@example.com'

Write-Utf8 (Join-Path $W 'CLAUDE.md') @'
# acme-sample-service
- Python 3 표준 라이브러리만 쓴다.
- 테스트는 unittest, `python -m unittest discover -s tests -v`.
'@
Write-Utf8 (Join-Path $W 'pricing.py') @'
def total_price(prices):
    return sum(prices)
'@
Write-Utf8 (Join-Path $W 'tests\test_pricing.py') @'
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from pricing import total_price


class TotalPriceTest(unittest.TestCase):
    def test_sum(self):
        self.assertEqual(total_price([100, 200]), 300)


if __name__ == "__main__":
    unittest.main()
'@
Invoke-Git -C $W add -A
Invoke-Git -C $W commit -q -m 'feat: sample pricing'
Invoke-Git -C $W remote add origin $bare
Invoke-Git -C $W push -q origin main
Invoke-Git -C $W remote set-head origin main

$automation = [ordered]@{
    workDir      = (Join-Path $Root 'workspace')
    transitions  = [ordered]@{ start = 'In Progress'; done = 'In Review' }
    testCommands = [ordered]@{ $Repo = 'python -m unittest discover -s tests -v' }
}
Write-Utf8 (Join-Path $Root 'automation.json') ($automation | ConvertTo-Json -Depth 5)

Write-Utf8 (Join-Path $Root "jira\$Key.json") @'
{"issue": {"key": "ACME-101", "fields": {
  "summary": "할인가 계산 함수 추가",
  "description": "acme-sample-service 의 pricing.py 에 discount_price(price, percent) 를 추가한다.\n\n완료 조건\n1. 할인가 = price * (100 - percent) // 100 (원 단위 내림)\n2. percent 가 0 미만이거나 100 초과면 ValueError\n3. price 가 음수면 ValueError\n4. 위 조건마다 tests/test_pricing.py 에 unittest 테스트가 있다",
  "issuetype": {"name": "Story"}, "status": {"name": "Open"}, "priority": {"name": "Medium"},
  "assignee": {"name": "sample-user", "key": "sample-user", "displayName": "Sample User"},
  "reporter": {"name": "sample-pm", "key": "sample-pm", "displayName": "Sample PM"},
  "components": [{"name": "acme-sample-service"}], "labels": [], "issuelinks": [],
  "created": "2026-11-18T09:00:00.000+0900", "updated": "2026-11-18T09:00:00.000+0900"}},
 "comments": [],
 "transitions": [{"id": "1", "name": "Start", "to": {"name": "In Progress"}}, {"id": "2", "name": "Review", "to": {"name": "In Review"}}]}
'@

# 시험 데이터 폴더의 설정 (선택)
if ($ClaudePath -or $RepoDir) {
    $settings = [ordered]@{}
    if ($ClaudePath) { $settings.claudePath = $ClaudePath }
    if ($RepoDir) { $settings.repoDir = $RepoDir }
    Write-Utf8 (Join-Path $Root 'data\settings.json') ($settings | ConvertTo-Json)
}

# 사내 프록시 변수가 죽어 있으면 dotnet 이 기다리므로 이 프로세스에서만 뺀다
foreach ($name in 'http_proxy', 'https_proxy', 'HTTP_PROXY', 'HTTPS_PROXY') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
$env:POKEMATE_TRIAL = $Root
$env:POKEMATE_DATA_DIR = Join-Path $Root 'data'
& dotnet run --project (Join-Path $app 'src\PokeMate.Trial') -- $Root $Key acme $Repo $Prompts
exit $LASTEXITCODE
