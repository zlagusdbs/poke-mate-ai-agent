# 전날 worklog 맞춰 보기

알람에서 `{{prompt:worklog-check}}` 로 불러 쓴다. 평일 아침에 직전 근무일의 HR 근무시간과 내 Jira worklog 합계가 맞는지 보여 준다.

1. 직전 근무일을 정한다: 어제부터 하루씩 거슬러 가며 토·일과 `hr_holidays`(사내 휴무일)에 있는 날을 건너뛴 첫 날. 월요일이면 보통 지난 금요일이다.
2. `work_day_check` 를 그날(yyyy-MM-dd)로 한 번 부른다.
3. 결과를 아래 형식으로 쓴다. 표·설명을 덧붙이지 않는다.

첫 줄 (status 별로 하나):
- match → `✅ {MM/DD(요일)} worklog 맞음 · {h시간 m분}`
- short → `⚠️ {MM/DD(요일)} worklog {h시간 m분} 모자람 · HR {h시간 m분} / worklog {h시간 m분}`
- over → `⚠️ {MM/DD(요일)} worklog {h시간 m분} 많음 · HR {h시간 m분} / worklog {h시간 m분}`
- unknown → `❓ {MM/DD(요일)} 비교 못 함` 다음 줄에 이유(errors 의 코드와 할 일, 시스템 안내의 오류 규칙대로)

둘째 줄부터 (match 가 아닐 때만):
- HR 근무: `{start}~{end}` (휴가·휴무면 "휴가")
- worklog 항목: 이슈 키 · 시간 을 한 줄씩, 많은 순으로 최대 5개. 없으면 "기록 없음".
- 모자라면 마지막 줄에 "worklog 를 {h시간 m분} 더 기록해야 맞아요" 한 줄만 쓴다. 기록 초안(pokemate-worklog 블록)은 붙이지 않는다(어느 이슈에 쓸지는 사람이 정한다).

휴가 날(HR 이 휴가)인데 worklog 도 0 이면 match 로 보고 `✅ … 휴가` 로 쓴다.
