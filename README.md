# PokeMate

macOS 메뉴바에 사는 업무 메이트. Claude Code 토큰 사용량을 보여 주고, 토큰을 쓸수록 자라는 포켓몬을 키우며, 대화·알람·메모로 업무를 돕는다.

```
메뉴바 [포켓몬] 12.3M  →  [ 홈 | 알람 | 메모 | 컬렉션 ]   🔌 ⚙︎
```

| 탭 | 내용 |
|---|---|
| 홈 | 키우는 포켓몬과 진화 단계, 오늘·이번 주·이번 달 토큰과 추정 비용, 메이트와 대화 |
| 알람 | 정한 시각에 할 일, 또는 이벤트(리뷰어 지정, 새 이슈 할당 등)가 생기면 할 일 |
| 작업 | 레포별로 지식이 쌓이는 Claude 작업 세션. 기록 보기·말 걸기·새로 만들기·새 세션으로 바꾸기·보관 |
| 메모 | md 파일 메모. 대화에서 "메모해줘" 로도 남긴다 |
| 컬렉션 | 고른 범위(1세대·전체)의 포켓몬 도감. 10억 토큰까지 키운 포켓몬이 등록된다 |
| 🔌 연결 · ⚙︎ 설정 | 사내 시스템 연결, 포켓몬 범위, 규칙·권한, 단축키, 호칭 (모델·추론 수준은 홈 입력창 아래에서 고른다) |

## 구조

```
PokeMate 앱 (Swift, 메뉴바)  ──claude -p (stream-json)──▶  Claude Code  ──▶  Claude
        │   ▲                                                      │
        │   └── 권한 요청 (승인 카드에서 [허용/거부]) ◀─────────────┤  기본 도구: Bash · Read · Edit · Write · WebFetch …
        │                                                          ├─▶ PokeMate MCP 서버 (Python) ──▶ Jira / Wiki / 근태 / 배포 / GitHub(gh) / 토큰 집계
        │                                                          └─▶ claude.ai Microsoft 365 커넥터 ──▶ 메일·일정·Teams
        └─▶ ~/.claude/projects/*.jsonl 을 읽어 토큰 집계 → usage.json
```

엔진은 로그인된 Claude Code CLI 다. 별도 API 키가 필요 없고 Claude Code 계정 사용량으로 동작한다. 앱 전용 Claude 로그인(`~/Library/Application Support/poke-mate/claude-config`)을 써서 터미널 Claude Code 로그인과 섞이지 않는다.

| 폴더 | 내용 |
|---|---|
| `app-mac/` | 메뉴바 앱 (SwiftUI + AppKit). `PokeMate` 화면, `PokeMateCore` 화면과 무관한 로직, `PokeMateCoreChecks` 로직 검증 |
| `mcp-server/` | Python 패키지 `pokemate`. MCP 서버(`pokemate serve`), 연결 관리(`pokemate connect`, `pokemate status`), 이벤트 조회(`pokemate events`) |
| `shared/` | 앱과 서버가 같이 쓰는 설정: 연결 대상 목록, MCP 설정 템플릿, 프롬프트, 규칙 예시 |

## Claude Code 도구와 권한

⚙︎ 설정 → 규칙 칸의 **Claude Code 도구 쓰기**를 켜면 메이트 대화에서 Claude Code 기본 도구(Bash, Read, Edit, Write, Glob, Grep, WebSearch, WebFetch 등)를 쓴다. 기본값은 꺼짐이고, 끄면 조회 도구(MCP)만 쓴다.

- 대화는 데이터 폴더의 `work/` 에서 실행하고 홈 폴더(`~`)도 볼 수 있다(`--add-dir ~`).
- Claude Code 가 사람에게 물어볼 동작(명령 실행, 파일 수정·생성, 웹 가져오기 등)은 대화창에 승인 카드로 뜬다. 카드에는 도구, 명령 또는 파일 경로, 수정 내용 요약이 나온다.
  - [허용] 이번 한 번만 · [거부] · [이 대화에서 항상 허용] Claude Code 가 제안한 규칙을 이 대화가 끝날 때까지 허용한다(새 대화를 시작하면 사라진다).
  - 누르기 전에는 실행하지 않고, 10분 안에 답이 없으면 거부한다. 읽기 전용 동작은 Claude Code 기본 규칙대로 묻지 않는다.
- 카드가 뜨면 맥 알림("{메이트 이름} 승인 요청", 본문은 "Bash: 명령" 한 줄)도 보낸다. 알림을 누르면 메이트 창이 열리고, 허용은 창의 카드에서만 한다. 5초 안에 연달아 뜬 카드는 알림 하나로 묶고, 카드에 답하거나 10분이 지나면 알림을 지운다.
- 실행 중에는 "Bash 실행 중: ls ~/Downloads" 처럼 지금 하는 일을 한 줄로 보여 준다.
- 권한 요청은 앱이 받아 답한다(`--permission-prompts host --permission-prompt-tool stdio`). 허용은 사용자 클릭으로만 한다.
- 토큰 조회 도구(`usage_summary`)는 이 스위치와 상관없이 늘 켜져 있다. 앱이 1분마다 집계 결과를 `usage.json` 에 남기고 MCP 도구가 읽는다.

### 권한 파일

규칙은 `~/Library/Application Support/poke-mate/work/.claude/settings.json` 한 파일에 모은다. 설정의 [권한 파일 열기]로 연다. Claude Code 표준 형식이고 우선순위도 Claude Code 그대로다: **deny > ask > allow**, 어디에도 없으면 승인 카드.

```json
{
  "permissions": {
    "deny": ["Bash(sudo:*)", "Bash(rm -rf:*)"],
    "ask": ["Bash(git push:*)"],
    "allow": ["Bash(git status:*)"]
  }
}
```

처음 실행할 때 파일이 없으면 아래 deny 기본값으로 만든다. 이미 있으면 빠진 기본 deny 만 더하고, 사용자가 넣은 것은 그대로 둔다.

- 명령: `sudo` `su` `doas` `defaults write` `defaults delete` `scutil` `networksetup` `systemsetup` `pmset` `spctl` `csrutil` `nvram` `launchctl` `dscl` `security` `tccutil` `diskutil` `fdesetup` `profiles` `softwareupdate` `kextload` `shutdown` `reboot` `halt` `osascript`
- 파일 수정·생성 금지: 이 권한 파일(`work/.claude/**`), 앱 `settings.json`, `local/` 의 `write-tools.json`·`alarms.json`·`pipeline-tools.json`·`domains.json`

deny 에 있는 것은 승인 카드 없이 거부한다. 고친 내용은 다음 질문부터 적용된다.

홈 폴더를 볼 수 있어서 `ls ~/Downloads` 같은 읽기 전용 명령은 카드 없이 실행된다. 명령 실행을 늘 묻게 하려면 `ask` 에 `"Bash"` 를 넣는다.

## 토큰 집계

- `~/.claude/projects`, `~/.config/claude/projects`, 앱 전용 로그인 폴더의 대화 기록(jsonl)을 읽는다.
- 같은 응답이 여러 파일에 들어 있어도 메시지 id 로 한 번만 센다.
- 처음 한 번 전체를 읽고, 이후에는 파일에서 늘어난 부분만 읽는다(1분마다). 캐시는 `~/Library/Application Support/poke-mate/token-cache.json`.
- 비용은 [Claude 공개 단가](https://platform.claude.com/docs/en/about-claude/pricing)로 환산한 추정치다. 구독료나 실제 청구액이 아니다. 단가표에 없는 모델은 비용에서 빼고 화면에 따로 알린다.

## 포켓몬

[PokéAPI](https://pokeapi.co)에서 받은 포켓몬을 키운다. 설정의 "포켓몬 범위"에서 1세대(도감 #1~#151, 기본)와 전체 세대 중에 고른다. 전체 세대의 마지막 번호는 PokéAPI 에서 받아 온다. 도감과 그림은 처음 쓸 때 받아 `~/Library/Application Support/poke-mate/pokemon/` 에 범위별로 저장하고, 저장소에는 그림을 넣지 않는다.

- 범위를 바꿔도 키우는 포켓몬과 도감 등록 기록은 그대로다. 새 범위는 다음 알부터 쓰고, 키우는 포켓몬은 알이 나올 때 정한 진화 경로를 끝까지 따른다.

```
알 ──3천만 토큰──▶ 기본 ──3억──▶ 1진화 ──6억──▶ 2진화 ──10억──▶ 도감 등록 → 다음 알
```

- 진화 단계 표기는 포켓몬 카드 용어(기본 · 1진화 · 2진화)를 따른다. 진화가 한 번뿐이면 `기본 → 1진화`, 진화하지 않으면 `기본` 상태로 10억까지 간다.
- 진화가 갈라지는 포켓몬(예: 이브이)은 알이 나올 때 최종 모습을 무작위로 정한다.
- 처음 알은 도감 #1, 그다음부터는 희귀도별 확률로 고른다: 일반 70% · 고급 20% · 희귀 8% · 전설 2%. 희귀도는 포획률로 나눈다(전설·환상 = 전설, 45 이하 = 희귀, 190 이하 = 고급, 나머지 = 일반).
- 메뉴바 아이콘과 홈 카드는 지금 키우는 포켓몬의 모습을 보여 준다. 대화 상대도 부화한 뒤에는 이 포켓몬 이름으로 자신을 부른다.
- 앱을 처음 켠 뒤로 늘어난 토큰만 센다. 오래된 대화 기록이 지워져 합계가 줄어도 포켓몬은 뒤로 가지 않는다.
- 넘친 토큰은 다음 알로 이어진다.
- 상태는 `~/Library/Application Support/poke-mate/mates.json`.

## 알람

- **시각 알람**: 정한 시각(매일·평일·요일·한 번)에 "내용"을 메이트에게 시킨다. 내용이 비면 알림만.
- **명령 실행**: 시각 알람의 "할 일"을 [명령 실행]으로 고르면 Claude 를 부르지 않고(토큰 0) 셸 명령만 실행한다. 로그인 셸(`zsh -lc`)로 홈 폴더에서 돌리고 5분이 지나면 멈춘다. 종료 코드 0 이면 성공이고, 출력 마지막 몇 줄을 실행 기록(`alarm-runs.json`)과 알림에 남긴다. 명령 알람은 알람 수정 화면에서 직접 입력해야만 만들어지고, 대화의 알람 제안으로는 만들 수 없다.
- **트리거 알람**: 1·3·5·10·30·60분마다 이벤트를 조회한다. 조회는 MCP 서버가 직접 해서 토큰을 쓰지 않고, 새 항목이 생기고 "내용"이 있을 때만 Claude 를 실행한다. 처음 켤 때 있던 항목은 기준으로 잡고 알리지 않는다.
- **내용을 파일로**: 내용에 `{{prompt:이름}}` 을 쓰면 `local/prompts/이름.md`(없으면 `shared/prompts/이름.md`)를 지시로 쓴다. 알람 수정 화면의 [프롬프트 불러오기]로 고를 수 있다. 다른 곳의 파일은 `{{file:경로}}` 로 쓴다.
- **켜기·끄기**: 알람 수정 화면의 "사용" 스위치로 바꾼다. 꺼진 알람은 목록에서 흐리게 보인다.
- **순서**: 알람 행을 끌어서 같은 그룹 안이나 다른 그룹으로 옮긴다. 그룹 머리줄에 놓으면 그 그룹 맨 끝에 들어간다. 오른쪽 클릭의 "위로 옮기기 / 아래로 옮기기"로도 바꿀 수 있다.
- **실행 기록**: 맨 위 카드에 켜진 알람 수, 오늘 실행 횟수, 마지막 실행 결과를 보여 주고, 각 알람 아래에도 주기와 마지막 결과를 한 줄로 보여 준다. 기록은 `~/Library/Application Support/poke-mate/alarm-runs.json`. 새 건이 없던 확인은 실행 횟수에 세지 않는다.
- **사람이 없는 실행**: 알람은 지켜보는 사람이 없어서 승인 카드를 쓰지 않는다. 그 밖의 권한 요청은 묻지 않고 거부한다(`--permission-prompts none`). 권한 파일의 deny 도 그대로 적용된다.
- **쓰기 허용**: 기본은 조회 전용이다. 트리거 알람에서 [쓰기 허용]을 켜면 `local/write-tools.json` 에 적은 명령과 폴더만 추가로 쓸 수 있다.
  ```json
  {
    "tools": ["Bash(python3 /path/to/helper.py:*)"],
    "dirs": ["~/Workspace"]
  }
  ```
  파일이 없거나 tools 가 비어 있으면 조회만 한다. `Edit(경로/**)`·`Write(경로/**)` 를 적으면 그 경로 안에서만 파일을 고친다. 읽기(Read·Grep·Glob)는 작업 폴더와 dirs 에 적은 폴더 안만 된다. 쓰기 알람은 대화와 따로 돌고, 알람 수정 화면의 "제한 시간"(기본 30분, 30·60·90·120분)까지 실행한다.
- **Jira 자동 처리 (예시 프롬프트 흐름)**: 나에게 할당된 이슈가 생기면 Story 는 In Progress → 지식 모으기(레포 CLAUDE.md, 문서, 위키, 비슷한 티켓, git 기록) → 처리 계획 댓글 → `feature/` 브랜치 worktree 에서 개발 → 빌드·테스트 → 통과하면 커밋·push → In Review → 결과 댓글. Task 는 In Progress → 실행 절차 댓글(실행은 사람이). 근거가 없거나 해석이 갈리면 개발하지 않고 "확인 필요" 댓글로 묻고 기다리며, 그 아래에 답 댓글을 달면 트리거가 다시 잡아 이어서 처리한다. PR 생성·머지·main/dev push·force push 는 하지 않는다.

## 작업 세션

레포별로 하나씩 이어 쓰는 Claude 세션이다. 같은 레포의 다음 일은 이미 읽은 CLAUDE.md·문서·위키를 다시 읽지 않는다. 세 군데에서 생긴다.

- **자동 처리**: 쓰기 허용 트리거 알람에서 "레포별 작업 세션 이어 쓰기"를 켜면, 자동 처리를 레포별 세션으로 묶는다(아래 1~3).
- **채팅에서 보내기**: 홈 입력창 아래 [보낼 곳 ▾]에서 메이트(기본) 대신 작업 세션을 고르면, 그 대화의 메시지가 그 세션으로 간다. `--resume`, 작업 세션 권한 규칙(`write-tools.json` 의 tools 는 미리 허용, 그 밖은 승인 카드), 토큰 기록, 레포별 차례(자동 처리와 겹치면 기다림)는 자동 처리와 같다. 답은 홈 채팅에 레포 이름 라벨을 달고 보인다. 고른 보낼 곳은 그 대화에서 유지되고 새 대화를 시작하면 메이트로 돌아간다. 메이트는 레포 코드 수정·조사·티켓 처리처럼 한 레포 일이라고 보면 답 끝에 `pokemate-route-suggest` 블록을 붙이고, 앱이 "X 작업 세션으로 보낼까요? [보내기]"를 보여 준다. 누르면 같은 질문을 그 세션으로 다시 보내고 보낼 곳도 그 세션으로 바뀐다. 메이트 세션에는 "작업 세션 X 로 보냄: (요약)" 한 줄만 다음 메시지 앞에 붙여 남긴다.
- **직접 만들기**: "작업" 탭이나 [보낼 곳 ▾]의 [+ 새 작업 세션]에서 `~/Workspace` 아래 git 레포를 고르면 빈 세션이 생긴다. 첫 메시지에 레포 지식 모으기(CLAUDE.md·README·빌드·테스트 방법·최근 git log) 지시가 붙는다.

1. **레포 정하기**: 새 건이 오면 가벼운 새 실행으로 처리할 레포만 정한다(읽기만). 이미 그 세션이 처리한 이슈(예: 확인 필요 답변)나 레포 이름이 보이는 PR 은 바로 그 세션으로 간다. 레포가 없는 운영 건은 "운영" 세션 하나에 모인다.
2. **이어 쓰기**: `claude -p --resume <세션 id>` 로 "이번 건: KEY" 와 처리 지시를 보낸다. 같은 세션에 두 건이 오면 차례로 처리한다.
3. **자동 처리의 세션 규칙 (하나)**: 자동 처리 세션은 작업 한 건마다 따로다. 한 건은 Jira 역할 처리에서는 티켓 하나(`jira:{KEY}`, 역할 세션 PM·개발·테스터·리뷰어), GitHub 리뷰에서는 PR 하나(`pr:{owner}/{repo}#{number}`, 리뷰어 세션)다. 같은 건 안의 핑퐁만 그 건의 세션을 `--resume` 하고, 건이 바뀌면 같은 도메인이어도 새로 시작한다(`--session-id`). 같은 건이어도 마지막 사용이 정한 시간(기본 55분, Claude 캐시가 1시간이라 그보다 짧게)을 넘었거나 문맥이 정한 크기(기본 120k) 이상이면 새로 시작한다. 이 판단은 앱 Core 의 `SessionPolicy` 한 곳이 모든 자동 처리(Jira 역할 처리·GitHub 리뷰·시험 Trial·평가 Eval)에 같이 쓴다. 역할별 예외(예전 설정 "리뷰어는 늘 새 세션으로 시작")는 없다. 예전 `settings.json` 의 `reviewerAlwaysFresh` 는 읽을 때 무시하고 다시 쓰지 않는다.
   - **맥락은 파일로**: 세션을 이어 쓰는 것은 캐시 비용을 아끼는 수단일 뿐이고, 일의 맥락은 대화 기록이 아니라 파일로 넘긴다. 역할이 한 차례를 마칠 때 앱이 그 건 파일의 `handoffs` 에 인계 메모 한 줄(`{role, at, summary}`: summary 는 그 차례의 note 와 답 앞부분, 리뷰어는 결과 문서의 결함·미확인 항목 제목 `findings` 도)을 남긴다. 같은 건의 앞 차례가 있는데 새 세션으로 시작하면 역할 프롬프트 앞에 "이번 건 인계" 블록(최근 메모 8개 오래된 것부터, 블록 전체 4,000자 안. 리뷰어에게는 자신의 이전 지적)을 붙인다. 이어 쓰는 세션에는 붙이지 않는다(이미 대화에 있다). 도메인 지식은 노트·지도에 있어 새 세션도 그것부터 읽는다.
   - **이유·설정**: 새로 시작한 이유(예: "새 티켓 (앞: ACME-100)", "새 PR (앞: sample-web#12)", "식음 61분 (기준 55분)", "문맥 214K (기준 120K)")는 작업 탭 세션 줄에 보인다. 설정 → 작업 세션의 쉰 시간·문맥 크기는 모든 자동 처리 역할 공통이고, 둘 다 "늘 이어 쓰기"(0)면 같은 건 안에서는 늘 이어 쓴다(다른 건은 그래도 새로).
   - **끝난 건 정리**: 티켓이 끝나면(PM 완료 후 종료, 또는 멈춤) 그 티켓의 역할 세션을, PR 은 리뷰를 승인으로 올렸거나 닫힘·머지됨을 확인하면 그 리뷰어 세션을 보관한다. 보관한 세션은 작업 탭 목록에서 빠지고 기록은 남는다.
   - **이 규칙을 따르지 않는 것**: 레포 이름 작업 세션의 자동 처리(위 1~2)는 그 레포 자체를 한 건으로 보고 같은 시간·문맥 기준만 쓴다. 사람이 말을 거는 세션(채팅에서 보내기, 직접 만든 레포 작업 세션, 작업 탭에서 말 걸기)은 이 규칙과 관계없이 그 세션을 이어 쓴다(문맥 % 알림만, 바꾸기는 사람이).
4. **문맥 알림**: 위 규칙 말고는 자동으로 세션을 바꾸지 않는다. 실행(자동 처리·말 걸기)이 끝나 문맥 %를 다시 계산했을 때 설정한 %(기본 70%, 50~90% 또는 끄기)를 처음 넘으면 미리 알림 창과 Mac 알림으로 한 번 알린다. 다시 내려가거나 새 세션으로 바뀌기 전에는 또 알리지 않는다(sessions.json 에 기록). 알림의 [새 세션으로 바꾸기](누르면 "작업" 탭이 열림)나 "작업" 탭 세션 줄의 ⋯ 메뉴에서 직접 바꾸면, 지금까지 익힌 레포 지식을 요약해 새 세션에 넘길지 고른다. 문맥이 가득 차면 Claude Code 가 스스로 대화를 줄인다(자동 compact). 앱은 그 사이 결과 줄만 기다리므로 멈추지 않고, 줄어든 뒤 문맥 %가 내려가면 알림 기록도 풀린다.
5. **화면**: "작업" 탭 맨 위에 "작업 세션 N개 · 실행 중 K · 이번 달 토큰·비용"(이번 달 1일부터 각 세션 기록 파일의 토큰을 모은 값)을 보여 준다. 목록에서 레포별 마지막 사용·처리한 티켓 수·문맥 %(알림 기준 이상이면 주황)·누적 토큰·실행 중/대기를 보고, 세션을 누르면 아래에 그 세션의 기록(사용자 메시지·Claude 답·도구 호출)과 입력창이 열려 이어서 말을 건다(대화와 같은 승인 카드). ⋯ 메뉴에서 새 세션으로 바꾸거나 보관한다.
6. **기록 위치**: `~/Library/Application Support/poke-mate/automation/sessions.json` (세션 id, 레포, 만든 시각, 마지막 사용, 처리한 키, 문맥 크기, 누적 토큰·비용). 작업 건 파일은 `automation/pipelines/{KEY}.json`(Jira 티켓)과 `automation/prs/{owner}__{repo}__{number}.json`(PR)이다(둘 다 `sessions`·`handoffs` 같은 모양). 세션 대화 기록은 PokeMate 전용 로그인 폴더(`claude-config/projects/`)에 생기므로 다른 Claude 앱의 세션 목록에는 나오지 않는다.

메이트 대화에서 "NOVA-1234 어느 세션에서 처리했어?", "지금 자동 처리 뭐 하고 있어?" 처럼 물으면 조회 도구 `work_sessions` 로 답한다. 지금 실행 중·대기 중인 건은 앱이 `automation/sessions-live.json` 에 남긴다.

### 도메인 리뷰어 (GitHub 리뷰)

GitHub 리뷰 자동 처리 알람(트리거 "리뷰어로 지정됐을 때" + 작업 세션)은 기본으로 "도메인 리뷰어 세션으로 리뷰"가 켜져 있다. 레포 이름 세션이 아니라 도메인의 리뷰어 칸(`{도메인}:reviewer`, 작업 탭에서 Jira 역할별 처리의 리뷰어와 같은 줄)에서 리뷰한다. 세션은 PR 하나마다 따로다(위 작업 세션 3의 규칙): 같은 PR 에 다시 리뷰 요청이 오면 55분·120k 안이면 그 PR 세션을 이어 쓰고, 아니면 새로 시작하며 그 PR 의 인계 메모와 이전 지적을 받는다. 지식은 세션이 아니라 도메인 노트·지도에 쌓인다.

1. **도메인 정하기**: PR 레포가 `local/domains.json` 의 `repos` 에 있으면 그 도메인. 없으면 예전에 정한 기록(`automation/state/review-domains.json`)을 쓰고, 그것도 없으면 PR 제목·본문·바뀐 파일·연결 Jira 를 도메인마다 적은 `description`·`keywords` 와 비교해 정하는 읽기 전용 실행을 한 번 돌린다. 어디에도 맞지 않으면 `default`. 정한 도메인과 이유는 리뷰 본문 "확인한 범위"와 시작 알림, 작업 탭에 보인다. 잘못 정했으면 `domains.json` 의 `repos` 에 넣거나 기록 파일에서 그 줄을 지운다.
2. **세션**: 새 세션이면 domain-review 스킬 1절대로 지식을 읽은 뒤 리뷰하고, 같은 PR 을 이어 쓰면 새로 필요한 것만 읽는다. PR 마다 작업 건 파일 `automation/prs/{owner}__{repo}__{number}.json`(`key`·`status`(open·approved·closed)·`sessions`·`handoffs`·`archivedAt`)을 둔다. 리뷰어의 마지막 답 첫 줄(`{repo}#{n} · 결과 · …`)이 승인이거나 닫힘이면 그 PR 건을 보관한다(같은 PR 에 다시 요청이 오면 다시 연다). 같은 도메인의 Jira 역할별 처리와 차례(큐)를 같이 쓴다. 기다리는 건은 작업 탭 도메인 줄의 "대기 n"(마우스를 올리면 목록)에 보인다.
3. **도메인 노트 (데스크톱과 같이 쓴다)**: 노트 폴더(기본 `~/.claude/references/notes`, 설정 `domainNotesDir`) 아래 `{도메인}/checklist.md`(그 도메인을 건드릴 때 반드시 답할 질문, 사람이 확정)와 `{도메인}/notes.md`(코드·문서로 확인한 사실, 종류별 절: 용어집·업무 규칙·장애·함정·결정·동작). `{도메인}/maps/{주제}.md` 는 여러 저장소·문서를 읽은 건 뒤에 남기는 지도(관련 저장소·호출 흐름·데이터 위치·읽은 문서를 위치 중심으로)다. 앱은 안내 줄에 지도 주제 목록(최대 20개)을 넣어 주제가 맞으면 먼저 읽게 한다. 자동 실행은 이 폴더를 읽기만 하고, 쓰기는 `pokemate auto domain-note --kind …`·`domain-map` 으로만 한다(파일 직접 편집은 막힌다. git 은 부르지 않으니 노트 폴더가 Git 저장소면 커밋은 사람이 한다). 노트 폴더가 심볼릭 링크면 앱이 실제 경로로 풀어 읽기 폴더에 넣는다(Claude 권한은 실제 경로로 판단). 도메인 폴더가 없으면 helper 가 빈 틀을 만들고 결과·알림에 "점검표가 아직 없음"을 남긴다. 쓰는 규칙은 노트 폴더의 `README.md`.
4. **리뷰 절차는 스킬이 원본**: 판단(깊이 → 지식 읽기 → 코드 읽기 → 점검표 답하기 → 후보 분류 → 판정 → 결과 문서 → 노트)은 홈의 `domain-review` 스킬(`~/.claude/skills/domain-review`)을 그대로 따른다. 데스크톱에서 리뷰할 때와 같은 스킬이다. 프롬프트(`github-review`·`jira-reviewer`)는 PokeMate 고유 단계(확인·알림·`gh-review` 등록·`review-state`, 명령 사용법)만 정한다. 결함이나 미확인이 하나라도 있으면 승인하지 않고 질문을 담은 댓글 리뷰로 올린다. 스킬은 앱이 홈 스킬을 작업 폴더에 연결할 때(실행마다) 같이 연결되고, 허용 목록에 `Skill(domain-review)` 가 있어야 한다.
5. **로컬 레포가 없을 때**: `pokemate auto gh-file --repo … --ref … --path …` 로 PR 저장소 파일을 읽는다(읽기 전용, 리뷰 중에는 그 PR 저장소만).
6. **위키·관련 저장소**: 위키는 도메인 키워드로 `wiki_search` 해서 맞는 페이지만 읽는다. diff 만으로 판단이 안 되거나 깊게 리뷰할 때는 바뀐 API·테이블·캐시 키를 쓰는 다른 저장소를 `~/Workspace` 아래에서 읽고, 없는 저장소는 결과 문서 "확인한 범위" 에 적는다.
7. **팀 KB 스냅샷 갱신 (`kb-sync`)**: 리뷰가 끝나면 `pokemate auto kb-sync --domain {도메인}` 을 한 번 실행한다. 사내 GitHub 의 팀 KB 가 개인 second-brain 저장소의 스냅샷(`resources/{KB}/`)보다 새로우면 스냅샷을 맞추고 갱신 PR 을 올린다. 설정은 `local/kb-sync.json`(예시 `local.example/kb-sync.json`).
   - `target`: second-brain 저장소(`host`·`repo`·`base`). `domains`: 도메인 → KB 이름. `kbs.{KB}`: `dest`(스냅샷 폴더), `keep`(고치지 않을 파일, 기본 `README.md`·`.sync.json`), `sources`(원본마다 `name`·`host`·`repo`·`paths`(가져올 경로, 비우면 전체)·`into`(스냅샷 안 하위 폴더)·`onlyExt`·`exclude`·`excludeDirs`·`base`(처음 기준 커밋)).
   - 원본마다 마지막으로 가져온 커밋(`{dest}/.sync.json`, 없으면 `base`) 이후 바뀐 파일만 반영한다. 손으로 더 가린 예전 파일을 덮어쓰지 않기 위해서다.
   - 반영하는 글은 가린다: IP(루프백·버전 번호처럼 보이는 것 제외), 메일 주소, 전화번호, `PWD=`·`UID=`·`password:` 형식의 값(`@변수`·`{자리표시}`·코드 대입은 제외). 예전에 손으로 가린 곳이 있던 파일과 비밀번호·계정 낱말이 남은 파일은 PR 본문의 "머지 전에 사람이 확인할 파일" 에 경로만 적는다.
   - 브랜치는 `hotfix/{main 에 들어간 마지막 버전의 minor+1}/kb-{KB}` (main 에서), PR 은 main 으로, Assignee 는 나. 같은 KB 의 갱신 PR 이 열려 있으면 새로 만들지 않고 그 브랜치에 커밋하고 PR 에 댓글로 바뀐 내용을 남긴다. 머지는 사람이 한다.
   - 원본·대상 저장소는 데이터 폴더 `automation/kb-sync/` 에 따로 clone 해 둔다(원본은 blobless). 사람이 쓰는 체크아웃은 건드리지 않는다. git 인증은 `gh auth git-credential` 이라 `gh auth login` 이 두 호스트 모두 되어 있어야 한다.
   - `--dry-run` 은 커밋·push·PR 없이 바뀔 파일과 PR 본문만 출력한다. 리뷰 기준 문제 평가(`POKEMATE_EVAL=1`)에서는 막힌다.
   - 허용 목록(`write-tools.json`)에 `Bash({helper} kb-sync:*)` 가 있어야 한다.

### 리뷰 기준 문제 묶음 (수준 확인)

프롬프트·스킬·노트를 바꾼 뒤 자동 리뷰가 나빠졌는지 보려고, 답을 아는 PR 을 모아 같은 경로로 다시 돌려 본다.

1. **문제 묶음**: `local/eval/reviews.json` (Git 밖). 문제마다 `id`, `url`(PR 주소), `domain`(비우면 domains.json·기록으로 정함), `mustFind`(꼭 나와야 할 지적: `{"name": "…", "all": [["낱말", "다른 표현"], ["낱말"]]}` — 묶음마다 하나 이상이 결함·미확인 절에 나오면 맞힘), `mustNotVerdict`(나오면 안 되는 판정, 예: `["승인"]`), 선택 `expectVerdict`·`expectDepth`(가볍게·보통·깊게). `mustFind` 항목에 `"in": ["결함"]` 을 주면 그 절에서만 찾는다(미확인으로만 낸 것은 놓침). 예시는 `shared/eval/reviews.example.json`.
   - **가상 레포 문제**: `url` 대신 `"fixture": "이름"` 을 쓰면 `local/eval/fixtures/이름/`(없으면 `shared/eval/fixtures/이름/`)의 `case.json`(티켓 키·제목·레포·브랜치·PM 개발 지시서·앞 단계 말), `base/`(기준 파일), `head/`(바뀐 파일)로 가상 레포와 작업 폴더를 만들고, Jira 역할 처리의 리뷰어 경로(지시서 + diff 파일 + 작업 폴더)로 리뷰한다. `shared/eval/fixtures/acme-coupon-boundary` 는 최소 주문 금액 경계값 결함(`>` 를 `>=` 로)을 하나 심은 문제다.
2. **실행**: `app-mac/scripts/review-eval.sh [묶음 파일] [--case id]` 가 문제마다 새 세션으로 실제 자동 리뷰와 같은 프롬프트(github-review + 도메인 리뷰어 안내 + 공유 노트)와 domain-review 스킬로 리뷰한다. 쓰기는 두 겹으로 막는다: 허용 목록에 읽기 명령(gh-get·gh-diff·gh-file·git-read·jira-get·jira-comments)과 스킬만 넣고, `POKEMATE_EVAL=1` 이면 helper 가 쓰기 명령을 모두 거절한다. 결과 문서는 답 끝 `pokemate-eval` 블록으로 받는다.
   - `--reuse always|default|분,토큰` 을 주면 앱과 같은 세션 규칙(`SessionPolicy`)으로 문제마다 세션을 고른다: 같은 건(같은 PR·같은 가상 티켓)만 그 기준으로 이어 쓰고 다른 건은 새로 시작한다(세션 기록은 결과 폴더의 `sessions.json`). 없으면 문제마다 새 세션.
3. **결과**: `local/eval/runs/{시각}/` 에 문제마다 답 원문(`{id}.md`)·실행 정보(`{id}.json`: 비용·시간·토큰·부른 스킬)와 채점표(`score.md`·`score.json`: 통과·판정·깊이·맞힘/놓침·위반·비용·시간·세션(이어 쓰기/새로와 이유)·끝 문맥·스킬·원문 경로). 핵심어 일치라 표현이 다르면 놓침으로 나올 수 있으니 원문을 같이 본다.
4. **데스크톱과 비교**: 데스크톱에서 같은 PR 을 domain-review 스킬로 리뷰한 결과 문서를 한 폴더에 `{id}.md` 로 두고 `pokemate auto eval-score --cases local/eval/reviews.json --results {폴더} --label 데스크톱` 으로 같은 표를 만든다.

### 역할별 처리 (Jira)

트리거 알람에서 "레포별 작업 세션 이어 쓰기" 아래 "역할별 세션으로 나눠 처리"를 켜면, 레포 세션 하나가 다 하던 Jira 처리를 역할 세션 4개(PM·개발·테스터·리뷰어)가 나눠 맡는다.

1. **도메인**: 레포 정하기 결과 레포를 `local/domains.json`(예: `{"acme": {"repos": ["acme-sample-service", "acme-sample-web"], "knowledge": ["acme-kb", "~/.claude/references/acme-wiki"]}, "default": {"knowledge": ["acme-kb"]}}`)으로 도메인에 묶는다. `knowledge` 는 지식 모으기 때 읽는 읽기 전용 지식 저장소(`~/Workspace` 아래 폴더 이름 또는 경로)로, 프롬프트의 `{{knowledge}}` 자리에 들어가고 역할 세션의 읽기 폴더에 더해진다. 도메인에 `knowledge` 가 없으면 `default` 를 쓴다(역할별 처리를 끈 `jira.md` 도 같은 값). 어느 도메인에도 없으면 레포 이름이 도메인이다. 작업 탭은 `sessions.json` 의 `{도메인}:{역할}` 키로 도메인마다 역할 4줄을 보여 주고(지금 처리 중이거나 마지막 건의 세션과 새로 시작한 이유. 끝나 보관한 칸은 "끝난 건 KEY" 로), 같은 도메인은 한 번에 티켓 하나씩 처리한다.
2. **흐름 (Story)**: PM(확인·지식 모으기·계획 댓글·레포·브랜치·개발 지시서) → PokeMate(`create-branch`·`worktree-add`) → 개발(W 안에서 코드·테스트) → 테스터(`run-tests` 와 완료 조건 대조, 테스트를 쓰지 않음) → PM(통과 확인) → 리뷰어(W 의 변경을 `domain-review` 스킬로 리뷰. 승인이면 push, 결함이면 개발, 미확인만 있으면 PM) → PokeMate(`commit-push`) → PM(In Review·결과 댓글·노트 남기기·여러 저장소·문서를 읽은 건이면 지도 남기기·알림). PM 은 계획 댓글 전에 도메인 점검표에 답하고 미확인이 있으면 확인 필요로 멈춘다. 개발은 바꾼 코드를 지시서의 점검표 답과 다시 대조한다. Task(운영 건)는 PM 하나로 끝낸다. Jira 댓글은 PM 만 쓴다.
3. **넘기기**: 역할끼리는 말을 못 하므로 각 역할은 답 끝에 `pokemate-next` 블록(`{"next": "tester|dev|pm|reviewer|push|stop", "note": "…"}`)을 붙이고, 앱이 읽어 다음 역할 세션을 `--resume` 으로 부른다. 앞 단계의 note·답 요약과 PM 개발 지시서가 다음 역할 프롬프트에 붙는다. 세션은 위 작업 세션 3의 규칙 하나로 정한다: 같은 티켓 안에서만 이어 쓰고(리뷰어도 같다), 다른 티켓이 오면 역할 세션을 모두 새로 시작하며(작업 탭에 "새 티켓 (앞: KEY)"), 같은 티켓이라도 55분·120k 를 넘긴 세션은 새로 시작해 "이번 건 인계" 블록과 진행 상태·노트·지도에서 이어 간다. note 는 다음 단계에 전달될 뿐 아니라 그 티켓의 인계 메모로도 남는다. 블록이 없거나 깨지거나 그 역할에 허용되지 않은 next 면 멈추고 blocked 알림을 보낸다.
4. **되돌림 상한**: 테스터→개발 2번, 리뷰어→개발 2번. 넘으면 PM 이 확인 필요(또는 개발 중단) 댓글을 남기고 멈춘다. 확인 필요 답이 달리면 PM 부터 다시 시작한다(되돌림 0).
5. **권한**: 역할마다 `--tools`·허용 명령을 따로 준다(`local/pipeline-tools.json`, 없으면 기본값으로 만든다). PM 은 Jira 읽기·쓰기·알림·git-read, 개발은 W 안 Edit/Write·run-tests·git-read, 테스터는 run-tests·git-read, 리뷰어는 `Skill(domain-review)`·`Skill(code-review)`·git-read·domain-note. PM 도 domain-note 를 쓴다. 노트 폴더는 PM·개발·리뷰어의 읽기 폴더에 앱이 더한다. `commit-push`·`create-branch`·`worktree-add` 는 어떤 역할에도 주지 않고 앱이 직접 실행한다.
6. **프롬프트**: `local/prompts/`(없으면 `shared/prompts/`)의 `jira-common`(공통 명령·규칙)과 `jira-pm`·`jira-dev`·`jira-tester`·`jira-reviewer`. `{{helper}}` 는 helper 명령으로 바뀐다.
7. **진행 상태**: 티켓마다 `automation/pipelines/{KEY}.json`(지금 단계, 되돌림 횟수, 레포·브랜치·W, 역할별 비용, 지나온 단계, 역할별 세션 `sessions`: `{"dev": {"id": "…", "lastUsed": "2026-11-20T09:34:25Z", "contextTokens": 100}}`, 인계 메모 `handoffs`: `[{"role": "reviewer", "at": "2026-11-20T09:40:00Z", "summary": "…", "findings": ["결함: …"]}]`. 옛 파일에는 없어도 읽는다)에 남겨 앱을 다시 켜도 이어 간다. 티켓이 끝나면(done·멈춤) 그 티켓의 역할 세션을 보관한다. "작업" 탭은 도메인마다 역할 4줄과 지금 단계(예: `ACME-101 · tester 2/2`)를 보여 준다.

알람 실행(시각·트리거·작업 세션)마다 입력·출력·캐시 토큰과 추정 비용을 `alarm-runs.json` 에 남기고, 알람 행의 마지막 결과 옆에 보여 준다.

## 자동 처리 helper

알람(명령 실행·트리거 쓰기 허용)과 자동 처리 프롬프트가 부르는 작은 명령 모음이다. Claude 없이 실행된다.

```bash
uv --directory <저장소>/mcp-server run --quiet pokemate auto <하위명령> [옵션]
```

| 하위 명령 | 하는 일 |
|---|---|
| `event` | 활동 기록 한 줄 남기기, `--notify` 면 Teams 카드 전송 (`--dry-run` 은 카드만 출력) |
| `github-check` | 새 리뷰 요청 PR 확인, 새로 생기면 Teams 카드 |
| `summary` | 하루 요약 카드 (예약 작업·Jira·GitHub) |
| `worklog-check` | 직전 근무일까지 이번 달 근무시간 기록이 모자란 날 확인, 있으면 Teams 카드 (`--hours`, `--today`, `--dry-run`) |
| `workday-check` | 직전 근무일의 HR 근무시간과 내 Jira worklog 합계가 다르면 차이를 출력, 같으면 출력 없음(알람이 조용히 넘어감) (`--today`, `--verbose`) |
| `poll` | Jira·GitHub 새 일감 확인 후 Claude 실행 (근무 시간에만, `--force`, `--only`, `--dry-run`) |
| `git-read` | 작업 폴더 바로 아래 레포에서 읽기 전용 git 명령만 |
| `create-branch` | `feature/...` 브랜치 만들고 push (이미 있으면 안 만듦, force 없음) |
| `jira-get` · `jira-search` · `jira-comments` · `jira-links` | Jira 읽기 (이슈, JQL 검색, 댓글, 연결 이슈·원격 링크, JSON) |
| `jira-comment` | 나에게 할당된 이슈에만 댓글. 첫 줄 `[자동 작성]`, 이슈당 최대 3건(확인 필요는 1건) |
| `jira-transition` | 나에게 할당된 이슈 상태 바꾸기 (`--to start`/`done`, 실제 상태 이름은 설정 `transitions`) |
| `worktree-add` | `feature/` 브랜치를 `작업 폴더/.jira-auto-worktrees/{KEY}` worktree 로 꺼냄 |
| `run-tests` | worktree 에서 빌드·테스트 (설정 `testCommands` 또는 gradlew·mvnw·npm·pytest·swift·go 감지, 20분), 결과 JSON |
| `commit-push` | worktree 의 `feature/` 브랜치에 커밋·push (메시지는 `feat:` 등 한 줄, Jira 키·Co-Authored-By 거절, force 없음) |
| `gh-get` · `gh-diff` | GitHub 조회 |
| `gh-file` | PR 저장소 파일 내용 읽기 (읽기 전용, 리뷰 중에는 그 PR 저장소만) |
| `domain-note` | 도메인 노트(`{노트 폴더}/{도메인}/notes.md`)의 `--kind` 절(용어집·업무 규칙·장애·함정·결정·동작)에 확인한 사실 한 줄 추가 (같은 줄은 건너뜀, 300줄 넘으면 알림). 도메인 폴더가 없으면 `checklist.md`·`notes.md` 빈 틀을 만든다(`--init` 은 틀만). `default` 는 거절 |
| `eval-score` | 리뷰 기준 문제 채점: 결과 폴더의 `{id}.md` 결과 문서에서 꼭 나와야 할 지적·판정·깊이를 보고 `score.md`·`score.json` 을 남김 (`POKEMATE_EVAL=1` 이면 읽기 명령과 이것만 실행됨) |
| `domain-map` | 도메인 지도(`{노트 폴더}/{도메인}/maps/{주제}.md`)를 본문 파일로 만들거나 통째로 갱신 (이전 것은 노트 저장소의 Git 이 보관, git 은 부르지 않음). 끝에 갱신 날짜·출처 한 줄 |
| `gh-review` | 리뷰 등록 (APPROVE·COMMENT 만, 내 PR·draft·지정 안 된 PR 거절) |
| `deploy-approve` | 내가 승인자인 승인 대기 배포를 승인 (`--id` 한 건만, `--dry-run` 대상만 출력). 내가 만든 배포는 건너뛰고, 승인되면 배포 시스템이 배치를 시작한다. 결과 JSON, 활동 기록에 남김 |
| `write-note` | 댓글·리뷰 본문 파일 쓰기 |
| `jira-state` · `review-state` | 처리 결과 저장 (다시 처리하지 않게). `jira-state --waiting` 은 "확인 필요" 답을 기다리는 표시 |

- 설정: `local/connections.json` 에 `"kind": "settings", "id": "automation"` 항목을 둔다. Jira 상태 이름(`transitions`: `start`·`done`), 레포별 테스트 명령(`testCommands`), GitHub 사용자(`githubUser`, 비우면 `gh api user`), 작업 폴더(`workDir`), 근무 캘린더 주소·이름, 공휴일 국가, 공수 기록용 반복 티켓 제목(`recurringTitles`), 도메인 노트 폴더(`domainNotesDir`, 기본 `~/.claude/references/notes`. 시험 모드(`POKEMATE_TRIAL`)에서는 시험 폴더의 `notes/`). GitHub 호스트와 Jira 주소는 같은 파일의 `github`·`jira` 항목 `baseUrl` 에서 읽고, Jira 는 연결 메뉴에서 연결한 계정(키체인)으로 직접 부른다. 예시는 `connections.example.json`.
- 데이터: `~/Library/Application Support/poke-mate/automation/` 아래 `state/`(처리 상태), `logs/`(Claude 실행 로그), `notes/`(본문 파일), `activity/`(월별 활동 기록 jsonl).
- Teams 웹훅 주소: 데이터 폴더 `teams-webhook-url` (권한 600). 이 파일은 열거나 출력하지 않는다.

## 준비물

**macOS**

- macOS 15 이상, Xcode Command Line Tools (`xcode-select --install`)
- [Claude Code](https://code.claude.com) CLI
- [uv](https://docs.astral.sh/uv/) (`brew install uv`)
- (선택) [GitHub CLI](https://cli.github.com) `gh` — GitHub 이벤트용
- (선택) claude.ai → 설정 → 커넥터에서 Microsoft 365 연결

**Windows** (11 기준)

- .NET SDK 7.0 이상과 .NET Desktop Runtime (`net7.0-windows`. 앱은 프레임워크 의존이라 런타임을 같이 내려받지 않는다)
- [Claude Code](https://code.claude.com) CLI (`%USERPROFILE%\.local\bin\claude.exe`)
- [uv](https://docs.astral.sh/uv/) (설치 스크립트 기본 위치 `%USERPROFILE%\.local\bin\uv.exe`). Python 3.12 는 uv 가 받는다.
- [Git for Windows](https://git-scm.com/download/win) — Claude Code 의 Bash 가 Git Bash 로 돌고, `{{helper}}` 명령과 `run-tests` 가 이것으로 실행된다
- (선택) GitHub CLI `gh`, claude.ai 의 Microsoft 365 커넥터는 맥과 같다

## 설치

```bash
git clone https://github.com/zlagusdbs/poke-mate-ai-agent.git ~/Workspace/poke-mate-ai-agent
cd ~/Workspace/poke-mate-ai-agent

# 1. 이 PC 의 설정 폴더 local/ (Git 에 올라가지 않는다. 다른 PC 에서 쓰던 local/ 이 있으면 그것을 복사)
cp -R local.example local
#    → local/connections.json 의 baseUrl 을 실제 주소로 바꾼다

# 2. MCP 서버
cd mcp-server && uv sync && uv run pytest && cd ..

# 3. 앱 빌드와 실행
./app-mac/scripts/bundle.sh
open app-mac/build/PokeMate.app
```

### Windows 설치

PowerShell 에서 (저장소는 길지 않은 경로에 두고 `git config core.longpaths true` 를 켜 둔다):

```powershell
git clone https://github.com/zlagusdbs/poke-mate-ai-agent.git $env:USERPROFILE\Workspace\poke-mate-ai-agent
cd $env:USERPROFILE\Workspace\poke-mate-ai-agent

# 1. 이 PC 의 설정 폴더 local\ (Git 에 올라가지 않는다. 다른 PC 에서 쓰던 local\ 이 있으면 그것을 복사)
Copy-Item -Recurse local.example local
#    → local\connections.json 의 baseUrl 을 실제 주소로 바꾼다

# 2. MCP 서버
cd mcp-server; uv sync; uv run pytest; cd ..

# 3. 앱 묶기와 설치
.\app-win\scripts\bundle.ps1
.\app-win\scripts\install.ps1
```

- `bundle.ps1`: `dotnet publish` 로 `app-win\build\PokeMate\` 를 만든다.
- `install.ps1`: 실행 중인 `PokeMate.exe` 를 끄고 `%LOCALAPPDATA%\Programs\PokeMate` 로 복사한 뒤 시작 메뉴에 `PokeMate` 바로 가기를 만든다. 앱은 켜지 않으므로 시작 메뉴에서 `PokeMate` 를 실행한다. 트레이 아이콘이 안 보이면 작업 표시줄의 숨겨진 아이콘(^)을 열어 끌어 올린다.
- 설정의 "Windows 시작 시 실행" 스위치를 켜면 로그인할 때 자동으로 켜진다(기본 꺼짐).
- 데이터 폴더는 `%LOCALAPPDATA%\poke-mate`(환경 변수 `POKEMATE_DATA_DIR` 가 있으면 그 폴더), 저장소 기본 위치는 `%USERPROFILE%\Workspace\poke-mate-ai-agent`, `claudePath`·`uvPath` 기본값은 `%USERPROFILE%\.local\bin` 의 `claude.exe`·`uv.exe`. 연결 계정은 Windows 자격 증명 관리자(서비스 이름 `poke-mate`)에 저장한다.
- 명령 알람은 `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command` 로 실행한다. 맥에서 가져온 `local\` 의 `./gradlew` 같은 명령은 Git Bash 로 도는 곳(`run-tests`)에서만 그대로 동작한다. `~/` 경로는 홈 폴더로 풀린다.
- 알림은 트레이 풍선 알림(Windows 알림 센터에 뜬다)이고, 기본 단축키는 `Ctrl+Alt+Shift+Space`.
- 앱의 Claude 로그인 정보는 데이터 폴더의 `claude-config` 에 따로 두며 `~\.claude` 의 로그인과 섞지 않는다.

### 설정은 어디에

설정은 레포 한 곳(`~/Workspace/poke-mate-ai-agent`)에서 찾는다. 세 층을 아래부터 차례로 덮어쓴다(같은 이름이면 위 층이 이김).

| 층 | 위치 | 내용 | Git |
|---|---|---|---|
| 기본 | `~/.claude/skills`, `~/.claude/rules` | 회사·개인 지식(Claude Code 와 같이 씀) | 레포 밖 |
| 일반 | `shared/CLAUDE.md`, `shared/skills/`, `shared/rules/`, `shared/prompts/` | 회사 값 없는 일반 지시·알람 프롬프트 뼈대. 앱이 직접 읽는다 | 올라감 |
| 회사·개인 | `local/` (CLAUDE.md, skills/, rules/, prompts/, alarms.json, alarm-groups.json, write-tools.json, pipeline-tools.json, domains.json, connections.json, bundle.json, banned-terms.txt) | 이 PC 의 회사 값과 알람 | 안 올라감 |
| 실행 기록 | `~/Library/Application Support/poke-mate/` (Windows: `%LOCALAPPDATA%\poke-mate`) | settings.json(레포 위치 등), 대화·세션·로그·캐시·로그인·웹훅 주소 | 레포 밖 |

- `shared/app/` 은 앱 자체의 시스템 안내(`system.md`, `daily-report.md`)라 설정 목록에 보이지 않는다.
- `local/` 이 없거나 ⚙︎ 설정의 "메이트 저장소"가 틀리면 알람·규칙을 읽기만 하고 저장하지 않는다(빈 알람으로 덮어쓰지 않게). 설정 화면에 경고와 [예시(local.example)로 local 만들기]가 뜬다.
- 다른 PC 로 옮길 때: `git clone` 으로 일반 설정을 받고, `local/` 폴더만 따로 복사한다.
- `local/` 은 Git 이 무시하는 폴더라 `git clean -x`·`git clean -X` 를 쓰면 통째로 지워진다. 이 레포에서는 쓰지 않는다. `git stash -u`, `git checkout` 은 무시된 파일을 건드리지 않는다.
- 저장소에 올라가는 코드·테스트·예시에는 일반 이름(hr, deploy, approval, permit, portal, groupware)과 example 값만 둔다. 회사 고유 값은 `local/` 에만 둔다.

- `local/connections.json`: 실제 주소와 화면·대화에서 부르는 이름. 항목마다 `name`(연결 화면과 알람에 보이는 이름)과 `aliases`(대화에서 부르는 다른 이름)를 둘 수 있고, 앱이 시스템 안내에 "이 이름으로 부르면 이 연결의 도구를 쓴다"를 덧붙인다. `home` 은 로그인 확인에 여는 첫 화면 경로(permit), 설정 항목의 `worklogCalendarTitle`·`worklogAliases` 는 worklog 캘린더를 부르는 이름이다.
- `local/bundle.json`: `{"bundleId": "…"}` 가 있으면 `bundle.sh` 가 그 번들 ID 로 묶는다(없으면 `Info.plist` 의 `io.pokemate.app`). 이미 쓰던 번들 ID 를 그대로 두면 알림 권한·메뉴바 위치가 유지된다.
- `local/banned-terms.txt`: push 전에 막을 금지어 한 줄에 하나. `.git/hooks/pre-push` 가 push 되는 커밋의 파일 내용·파일 이름·커밋 메시지를 검사해 하나라도 있으면 파일과 줄 번호를 보여 주고 멈춘다. 지금 올릴 상태를 미리 보려면 `git add -A && .git/hooks/pre-push --check --index`.

저장소를 `~/Workspace/poke-mate-ai-agent`(Windows: `%USERPROFILE%\Workspace\poke-mate-ai-agent`)가 아닌 곳에 두었다면 ⚙︎ 설정의 "메이트 저장소"를 바꾼다.

터미널에서 `uv --directory …` 없이 `pokemate` 로 바로 부르려면 한 번 설치한다. 저장소 코드를 고치면 바로 반영된다(`--editable`).

```bash
uv tool install --editable ./mcp-server
```

```bash
pokemate --help
```

```bash
pokemate auto --help
```

설치하지 않았다면 `uv --directory <저장소>/mcp-server run pokemate …` 로 부른다. 지우려면 `uv tool uninstall pokemate`.

## 규칙과 기억

레포의 `shared/`(일반)와 `local/`(이 PC)에서 md 로 관리한다. 두 곳의 구조는 같고, 같은 이름 파일은 `local/` 이 이긴다. CLAUDE.md 는 shared 다음 local 순서로 둘 다 읽는다. 설정 화면에서 새 파일을 만들 때 local(기본)과 shared 중에 고르고, shared 파일을 열면 Git 에 올라가는 파일이라고 알려 준다.

```
shared/ 또는 local/
 ├ CLAUDE.md        늘 적용하는 기본 지시
 ├ rules/           질문마다 모두 읽음 (memory.md = 기억)
 ├ skills/          주제가 나올 때만 읽음 (머리말의 keywords 로 고름)
 └ prompts/         알람 내용에서 {{prompt:이름}} 으로 불러 씀 (대화에서는 읽지 않음)
```

## 개발

```bash
cd mcp-server && uv run pytest                     # Python 테스트
cd app-mac && swift run PokeMateCoreChecks       # Swift 로직 검증 (XCTest 대신 실행형)
cd app-mac && ./scripts/bundle.sh                  # 앱 묶기
cd app-mac && ./scripts/pipeline-trial.sh /tmp/pm-trial   # Jira 역할 처리 시험 (가짜 Jira·로컬 원격, 실제 Claude 실행)
cd app-mac && ./scripts/review-eval.sh                    # 리뷰 기준 문제 묶음 (local/eval/reviews.json, 쓰기 막고 실제 Claude 실행)
```

Windows (PowerShell, 저장소 루트에서):

```powershell
cd mcp-server; uv run pytest; cd ..                                              # Python 테스트
dotnet run --project app-win\src\PokeMate.CoreChecks                             # C# 로직 검증 (마지막 줄 failed: 0)
.\app-win\scripts\bundle.ps1                                                     # 앱 묶기 → app-win\build\PokeMate
.\app-win\scripts\install.ps1                                                    # %LOCALAPPDATA%\Programs\PokeMate 에 설치
.\app-win\scripts\pipeline-trial.ps1 -Root $env:TEMP\pm-trial                    # Jira 역할 처리 시험 (가짜 Jira·로컬 원격, 실제 Claude 실행)
.\app-win\scripts\pipeline-trial.ps1 -Root $env:TEMP\pm-trial -FakeClaude        # 같은 시험을 가짜 claude(PokeMate.Trial 자신)로: -Key ACME-102 -Keep 로 이어서, -Review <PR 주소> 로 GitHub 리뷰 한 차례
.\app-win\scripts\review-eval.ps1                                                # 리뷰 기준 문제 묶음
```

연결 계정은 macOS 키체인(Windows: 자격 증명 관리자)에 서비스 이름 `poke-mate` 로 저장한다.

## 참고한 프로젝트

PokeMate 는 [PokeTokenBar](https://github.com/chattymin/PokeTokenBar) (chattymin, MIT 라이선스)에서 아이디어를 얻었다.

- 메뉴바에 Claude Code 토큰 사용량을 보여 주고, 토큰을 쓸수록 포켓몬이 알에서 깨어나 진화하는 구성
- Claude 모델별 단가표 (`app-mac/Sources/PokeMateCore/TokenUsage.swift` 의 `ClaudePricing`)

라이선스 전문은 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 에 있다.
