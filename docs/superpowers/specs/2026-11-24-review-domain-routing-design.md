# 리뷰 도메인 정하기: 팀 KB 기준 설계

날짜: 2026-11-24 · 대상: app-win, app-mac (같은 동작), `shared/prompts/github-review.md`

이 문서는 공개 저장소에 올라간다. 회사·사내 시스템·저장소의 실제 이름은 쓰지 않는다. 실제 이름은 Git 에 올라가지 않는 `local/` 설정에만 둔다. 아래에서 쓰는 이름은 이렇게 바꿔 부른다.

| 문서에서 | 뜻 |
|---|---|
| pricing 팀 | 할인·쿠폰 일을 하는 팀 |
| product 팀 | 상품 일을 하는 팀 |
| 팀 KB | 팀이 관리하는 지식 저장소 (팀마다 하나) |
| 공용 관리자 레포 | 여러 팀이 화면을 나눠 개발·배포하는 관리자 시스템 레포 |
| 전사 레포 지도 | 레포마다 짧은 안내 문서(`maps/{owner}/{repo}.md`)를 모아 둔 사내 저장소. 도메인 칸은 없다 |

## 1. 왜 바꾸나

PR 리뷰 요청이 오면 PokeMate 는 먼저 도메인을 정한다. 도메인에 따라 읽을 지식(팀 KB, 점검표·노트, 위키 키워드)이 달라지기 때문이다. 지금 방식에는 문제가 세 가지 있다.

1. **레포 하나 = 도메인 하나로 저장한다.** `domains.json` 의 `repos` 에 없는 레포는 Claude 가 PR 을 보고 도메인을 정한다. 그 결과를 `automation/state/review-domains.json` 에 레포 단위로 저장하고, 다음부터는 다시 묻지 않는다.
   - 공용 관리자 레포는 PR 마다 도메인이 다르다. 그런데 한 번 정한 도메인이 계속 쓰인다.
   - 조회가 실패한 상태에서 레포 이름만 보고 추정한 도메인도 저장됐다.
   - 2026-11-24 에 공용 관리자 레포가 coupon 으로 저장된 채 리뷰됐다. 그날 자동 리뷰 5건을 모두 거둬들였다.
2. **팀과 도메인이 섞여 있다.**
   - pricing·product 는 팀이고, coupon·discount·product 는 도메인이다.
   - 팀마다 팀 KB 가 따로 있다.
   - 지금 `domains.json` 에는 이 구분이 없다.
3. **팀이 바뀌면 처음부터 다시 적어야 한다.**
   - 사용자는 지금 pricing 팀 소속이면서 product 팀 일도 한다.
   - 2026-12-07 에 조직개편이 있다.
   - 지금은 레포 목록·지식 경로·학습 기록을 손으로 다시 맞춰야 한다.

## 2. 목표 · 하지 않을 것 · 실패로 볼 것

**목표**
- 공용 관리자 레포의 PR 도 PR 마다 맞는 도메인으로 리뷰한다.
- **여러 팀의 일을 한 번에 다룬다.** 지금은 pricing·product 두 팀이다.
- 레포→도메인 지도는 가능한 한 팀 KB 에서 읽는다. 사용자가 따로 들고 있지 않는다.
- 팀이 바뀌면 설정 목록 한 줄만 고친다.
- 어느 단계에서 어떤 근거로 정했는지 리뷰 본문에 남긴다.

**하지 않을 것** (이번 범위 밖)
- LLM 과 승인을 떼어 내는 결정론 승인 게이트. 따로 다룬다.
- 팀 KB 자체에 올라오는 지식 문서 PR 을 그 팀의 리뷰 스킬로 넘기는 처리. 따로 다룬다.
- `kb-sync.json` 의 팀 정의와 합치기.
- Jira 파이프라인(`DomainMap.Domain(repo)`)의 도메인 결정 방식. 지금 그대로 둔다.

**실패로 볼 것** (동작해도 실패)
- 추측한 도메인을 레포 단위로 다시 저장하는 것.
- 공용 레포 하나를 도메인 하나에 묶는 것.
- 도메인을 정한 근거가 리뷰 본문에 안 보이는 것.
- 조회 실패(네트워크·권한)인데 레포 이름만으로 도메인을 정하는 것. 이때는 default 로 간다.
- 공개 저장소의 코드·문서·예시에 회사·사내 시스템 이름이 들어가는 것.

## 3. 방식 비교 (고른 것: 1)

| | 방식 | 대가 |
|---|---|---|
| **1 (고름)** | **팀 KB 기준.** 설정에는 내 팀들과 각 팀 KB 위치만 둔다. 레포→도메인은 팀 KB 에서 읽고, 없으면 경로 규칙, 그래도 없으면 PR 마다 판단한다 | 팀마다 KB 구조가 달라서 팀마다 읽는 방식을 정해야 한다. product 팀 KB 에는 레포 지도가 없다 |
| 2 | 직접 관리. `domains.json` 에 팀·레포·경로 규칙을 손으로 적는다 | 팀 문서와 따로 놀아 낡는다. 팀이 바뀌면 다시 적어야 한다 |
| 3 | 매번 판단. 지도 없이 PR 마다 Claude 가 정한다 | 느리고 비싸다. 같은 레포를 두고 판단이 흔들린다 |

전사 레포 지도를 별도 단계로 두는 안도 검토했다. 이 지도에는 도메인 칸이 없어서, 결국 Claude 가 설명 글을 읽고 판단해야 한다. 그래서 3단계(PR 판단)의 읽을거리로 넣었다.

## 4. 설계

### 4-1. 도메인을 정하는 순서

```
PR 리뷰 요청
 ├ 1. 팀 KB 레포 지도        내 팀들의 지도·domains.json repos 에 이 레포가 한 도메인으로 있나 → 확정 (파일 조회 없음)
 ├ 0. 바뀐 파일 목록 조회    1단계가 못 정했을 때만. 실패하면 → 바로 default ("파일 조회 실패")
 ├ 2. 경로 규칙              바뀐 파일 경로가 규칙상 한 도메인인가        → 확정
 └ 3. PR 판단 (저장 안 함)    diff 요약·전사 레포 지도·팀 KB 공용 문서를 읽고 Claude 가 고름
      └ 못 고르면 → default
```

- 앞 단계에서 정해지면 뒤 단계는 하지 않는다.
- 도메인 후보는 **내 팀들의 도메인 목록을 합친 것**(4-2)과 default 다.
- 결과는 이번 PR 리뷰에만 쓴다. 다음 PR 이 오면 다시 1단계부터 한다.
- `review-domains.json` 에 저장하던 동작과, 그 파일을 읽던 `Known()` 의 Learned 경로를 없앤다. 파일을 지울지 남기고 무시할지는 구현자가 정한다.
- 1단계는 바뀐 파일이 필요 없으므로 파일을 조회하기 전에 한다. 1단계가 정하면 파일을 조회하지 않는다.
- 1단계가 못 정해 0단계 조회를 했는데 실패하면 2·3단계를 모두 하지 않고 default 로 리뷰한다. 앱 조회가 실패하면 Claude 쪽 조회도 실패할 가능성이 커서, 최대 600초짜리 실행을 돌릴 이유가 없다. (설계자 판단)

### 4-2. 설정: `local/teams.json` (새 파일, Git 에 안 올라감)

공개 저장소의 `local.example/teams.json` 에는 아래처럼 일반 이름으로만 둔다.

```json
{
  "myTeams": ["pricing", "product"],
  "repoGuide": "example-org/repo-guide",
  "teams": {
    "pricing": {
      "kb": "kb/pricing-kb",
      "repoMap": "raw/{domain}/repos/{repo}.md",
      "domains": ["coupon", "discount"]
    },
    "product": {
      "kb": "kb/product-kb",
      "repoMap": null,
      "domains": ["product"]
    }
  }
}
```

- `myTeams`: 지금 일하는 팀들. **여러 팀을 넣을 수 있다.** 조직개편 뒤에는 이 목록만 고친다.
- `repoGuide`: 전사 레포 지도 저장소(`{owner}/{repo}`). 없으면 3단계에서 읽지 않는다. 코드에 실제 이름을 박지 않는다.
- `teams.{팀}.kb`: 팀 KB 로컬 경로. `domains.json` 의 knowledge 와 같은 규칙으로 해석한다(`~/`, 절대 경로, 나머지는 `~/Workspace/` 아래).
- `teams.{팀}.repoMap`: 팀 KB 안에서 레포 문서가 놓인 경로 패턴. `{domain}`·`{repo}` 자리를 채워 파일이 있는지 본다. 지도가 없는 팀은 null 이다.
- `teams.{팀}.domains`: 이 팀의 도메인 목록.
  - 각 도메인의 지식·설명·키워드는 지금처럼 `domains.json` 에서 읽는다.
  - 팀 도메인은 `domains.json` 에도 있어야 한다. 없으면 후보에서 뺀다.
- `myTeams` 의 팀이 `teams` 에 없으면 그 팀은 무시한다.
- `teams.json` 이 없거나 깨졌거나, 쓸 수 있는 팀이 하나도 없으면 지금처럼 `domains.json` 의 모든 도메인을 후보로 둔다. 이때 1단계의 팀 KB 지도는 건너뛰고 `domains.json` repos 만 본다.

**1단계 규칙**
- 내 팀마다 `repoMap` 의 `{domain}` 자리에 그 팀 도메인 목록에 있는 것만 넣어 본다.
  - 예: pricing 팀이면 `raw/coupon/repos/{repo}.md`, `raw/discount/repos/{repo}.md` 를 확인한다.
- 도메인 목록에 없는 폴더(예: `raw/common/repos/{repo}.md`)의 문서는 1단계에서 쓰지 않는다. 대신 3단계 읽을거리("팀 KB 공용 문서")로 넘긴다.
- `domains.json` 의 `repos` 목록도 1단계에 포함한다(지금의 Explicit). 단, 후보 도메인일 때만이다. product 팀처럼 지도가 없는 팀은 이 목록이 1단계 역할을 한다.
- 모든 출처(내 팀들의 팀 KB 지도 + `domains.json` repos)에서 나온 도메인을 모은다.
  - 정확히 하나면 확정한다.
  - 둘 이상이면 2단계로 넘긴다. 두 팀이 같은 레포를 다루는 경우도 여기에 해당한다. (설계자 판단)
- 레포 이름은 대소문자를 무시하고 맞춘다.

### 4-3. 경로 규칙: `local/path-rules.json` (새 파일, Git 에 안 올라감)

공개 예시(`local.example/path-rules.json`):

```json
{
  "example-org/shared-admin": [
    { "path": "src/Areas/Coupon/**", "domain": "coupon" },
    { "path": "src/Areas/Discount/**", "domain": "discount" },
    { "path": "src/Areas/Item/**", "domain": "product" }
  ]
}
```

- 키는 `{owner}/{repo}` 이다. `path` 는 glob 이고 `**`(폴더 경계 넘음), `*`(폴더 안)를 쓴다. 대소문자는 무시한다.
- 팀과 무관한 한 파일에 둔다. 규칙의 도메인이 지금 후보가 아니면 그 규칙은 무시한다. 그래서 팀이 바뀌어도 지울 필요가 없다.
- 판정 방법: PR 의 바뀐 파일마다 처음 맞는 규칙의 도메인을 모은다.
  - 모인 도메인이 정확히 하나면 확정한다. 규칙에 안 맞는 파일이 섞여 있어도 확정한다.
  - 둘 이상이면 3단계로 넘기고, "경로 규칙 후보: coupon 3개, product 2개" 를 읽을거리로 준다.
  - 하나도 없으면 3단계로 넘긴다.
- 바뀐 파일 목록(0단계)은 앱이 helper 의 `gh-get repos/{o}/{r}/pulls/{n}/files --paginate` 로 받는다.
- 팀 KB 의 공용 관리자 레포 문서는 글로 된 정리라 자동으로 규칙으로 바꾸지 않는다. 처음 규칙은 그 문서를 보고 손으로 적는다(7절).

### 4-4. 3단계: PR 판단 (지금의 ChoosePrompt 를 고침)

- 후보: 내 팀들의 도메인 + default. 각각 설명·키워드를 붙이고, 어느 팀 도메인인지도 적는다.
- 읽을거리:
  - PR 제목·본문, 바뀐 파일 목록(앱이 받은 것), Jira 요약(지금과 같음)
  - 전사 레포 지도의 이 레포 문서: `gh-get repos/{repoGuide}/contents/maps/{owner}/{repo}.md --jq '.content | @base64d'`. 없으면 건너뛴다.
  - 1단계에서 넘긴 팀 KB 공용 문서 경로(있으면)
  - 2단계 후보(있으면)
  - 1단계에서 둘 이상 나온 도메인(있으면)
- 읽기 전용 권한은 지금과 같다(`gh-get`, `jira-get`). 넘긴 공용 문서를 읽을 수 있게 내 팀 KB 폴더를 dirs 에 더한다.
- **결과는 저장하지 않는다.**
- 3단계 안에서 조회가 실패하면 레포 이름만으로 고르지 않고 default 로 답하라고 프롬프트에 적는다.

### 4-5. 근거 남기기

- `DomainReview.Source` 를 바꾼다: `TeamMap`(팀 KB 지도) · `Listed`(domains.json repos) · `PathRule` · `Chosen`(PR 판단) · `Fallback`. `Learned` 는 없앤다.
- 결과 문서 "확인한 범위" 첫 줄 형식(GitHub 본문에서는 끝의 Reference 절로 옮긴다): `도메인: {domain} — {단계 이름}: {근거}`.
  - 예: `도메인: coupon — 경로 규칙: src/Areas/Coupon 아래 4개 파일`
  - 예: `도메인: default — 기본값: 파일 조회 실패`
- 작업 탭의 "리뷰 중" 표시에도 같은 줄을 쓴다(`SetReviewing`).

## 5. 바뀌는 곳 (구현자가 찾을 위치)

| 위치 | 바뀜 |
|---|---|
| `app-win/src/PokeMate.Core/DomainReview.cs` | `Known()` 을 1·2단계로 바꾼다. `ReviewDomainBook` 사용을 없앤다. `ChoosePrompt` 의 후보·읽을거리를 고친다. `Source` 를 바꾼다 |
| 새 Core 파일(예: `TeamConfig.cs`, `PathRules.cs`) | `teams.json`·`path-rules.json` 을 읽는다. 깨지면 없는 것으로 본다 |
| `app-win/src/PokeMate.Core/MateCLI.cs` | 바뀐 파일 목록 조회 함수를 더한다 |
| `app-win/src/PokeMate/WorkSessionCenter.cs` `HandleReview` | 1단계 → (못 정하면) 파일 목록 조회 → 2·3단계 순서로 바꾼다. 3단계 결과를 저장하는 코드를 없앤다 |
| `app-win/src/PokeMate.Core/ReviewEval.cs`, `PokeMate.Eval` | 평가는 네트워크·PC 상태에 따라 달라지면 안 되므로 1단계 중 `domains.json` repos(Listed)만 쓴다. `Learned` 제거에 맞춰 고친다. (설계자 판단) |
| `app-mac/Sources/PokeMateCore/{DomainReview,MateCLI,ReviewEval}.swift`, `app-mac/Sources/PokeMate/WorkSessionCenter.swift` | 위와 같은 동작 |
| `PokeMate.CoreChecks` / `PokeMateCoreChecks` | 단계별 판정 테스트 |
| `local.example/` | `teams.json`, `path-rules.json` 예시(일반 이름만) |
| `README.md` | 팀이 바뀔 때 고칠 것 한 단락 |

## 6. 구현자에게 맡기는 것

- glob 구현 방식(직접 구현 또는 라이브러리). 두 앱의 결과가 같아야 한다.
- 3단계 프롬프트 문장.
- `review-domains.json` 을 지울지, 남기고 무시할지.

## 7. 사용자가 정한 것 (2026-11-24)

- **내 팀은 pricing, product 두 팀이다.** 2026-12-07 조직개편 뒤에는 `myTeams` 만 고친다.
- **pricing 팀 도메인은 coupon, discount 두 개다.** 팀 KB 의 다른 도메인 폴더는 넣지 않는다. 그 레포의 PR 은 3단계로 간다.
- **product 팀 도메인은 product 하나다.** 팀 KB 에 레포 지도가 없어서 `domains.json` 의 product repos 목록이 1단계 역할을 한다.
- **discount 를 별도 도메인으로 둔다.**
  - `local/domains.json` 에 discount 를 더하고, coupon 의 설명·키워드를 쿠폰 쪽과 할인 쪽으로 나눈다.
    - coupon: 쿠폰 발급·사용·정책, 쿠폰적용가
    - discount: 즉시할인·할인 정책·타겟팅, dc
  - discount 의 knowledge 는 coupon 과 같은 팀 KB 를 쓰고, 노트 경로만 `notes/discount` 로 한다.
  - 점검표·노트는 `notes/discount` 를 새로 시작한다(첫 리뷰 때 `domain-note --init`).
  - `local/kb-sync.json` 의 `domains` 에 `"discount": "pricing"` 을 더한다.
  - 이 파일들은 로컬 설정이라 구현 작업의 한 단계로 직접 고친다.
- **공용 관리자 레포의 첫 경로 규칙은 설계자가 초안을 쓰고 사용자가 확인한다.**
  - 초안은 팀 KB 의 공용 관리자 레포 문서를 보고 적는다. pricing·product 두 팀 도메인을 모두 다룬다.
  - 확인 전까지 `local/path-rules.json` 은 비워 둔다. 비어 있으면 그 레포 PR 은 3단계로 가고, 결과는 저장하지 않는다.
