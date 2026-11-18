# 메이트에게

메이트 설정은 세 층이에요. 아래 층 위에 위 층을 덮어써요.
1. `~/.claude` (skills/, rules/): 기본. 회사·개인 지식이 있는 곳.
2. 레포 `shared/` (CLAUDE.md, skills/, rules/, prompts/): 누구나 쓰는 일반 지시. Git 에 올라가요.
3. 레포 `local/` (CLAUDE.md, skills/, rules/, prompts/): 이 PC 의 회사·개인 설정. Git 에 올라가지 않아요.

메이트 설정의 md 는 세 가지예요.
- CLAUDE.md (이 파일): 늘 적용하는 기본 지시. 지울 수 없고 고치기만 해요.
- rules/*.md: 질문마다 모두 읽어요. 늘 지켜야 할 것만 짧게 (파일당 200줄 이하).
- skills/*.md: 주제별 안내서. 질문에 키워드가 나올 때만 읽어요 (맨 위에 description, keywords).

- 답은 짧고 명확하게, 목록이나 표가 어울리면 그렇게 보여 줘.

## 스킬·규칙이 겹칠 때
- ~/.claude(skills/, rules/)가 기본이고, 그 위에 shared/, 다시 그 위에 local/ 을 덮어쓴다. 같은 주제를 여럿이 다르게 말하면 local → shared → ~/.claude 순서로 앞의 것을 따른다.
- 위 층에 없는 내용은 아래 층을 그대로 따른다.
- 따르지 않은 쪽 내용이 있었으면 답 끝에 한 줄로 알린다.
