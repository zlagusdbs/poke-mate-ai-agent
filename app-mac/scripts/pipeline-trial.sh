#!/bin/zsh
set -euo pipefail
ROOT=${1:?"시험 폴더 경로"}
PROMPTS=${2:-"$(cd "$(dirname "$0")/../../shared/prompts" && pwd)"}
KEY=ACME-101
REPO=acme-sample-service
cd "$(dirname "$0")/.."
APP=$PWD
rm -rf "$ROOT"
mkdir -p "$ROOT"/{workspace,remotes,jira,data,pipelines}
git init -q --bare -b main "$ROOT/remotes/$REPO.git"
W="$ROOT/workspace/$REPO"
git init -q -b main "$W"
git -C "$W" config user.name "Sample Bot"
git -C "$W" config user.email "sample-bot@example.com"
mkdir -p "$W/tests"
cat > "$W/CLAUDE.md" <<'MD'
# acme-sample-service
- Python 3 표준 라이브러리만 쓴다.
- 테스트는 unittest, `python3 -m unittest discover -s tests -v`.
MD
cat > "$W/pricing.py" <<'PY'
def total_price(prices):
    return sum(prices)
PY
cat > "$W/tests/test_pricing.py" <<'PY'
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
PY
git -C "$W" add -A
git -C "$W" commit -q -m "feat: sample pricing"
git -C "$W" remote add origin "$ROOT/remotes/$REPO.git"
git -C "$W" push -q origin main
git -C "$W" remote set-head origin main
cat > "$ROOT/automation.json" <<JSON
{"workDir": "$ROOT/workspace", "transitions": {"start": "In Progress", "done": "In Review"},
 "testCommands": {"$REPO": "python3 -m unittest discover -s tests -v"}}
JSON
cat > "$ROOT/jira/$KEY.json" <<'JSON'
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
JSON
swift build -q --product PokeMateTrial
POKEMATE_TRIAL="$ROOT" POKEMATE_DATA_DIR="$ROOT/data" "$(swift build --show-bin-path)/PokeMateTrial" "$ROOT" "$KEY" acme "$REPO" "$PROMPTS"
