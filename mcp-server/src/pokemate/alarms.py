"""메이트 알람 목록: 앱 데이터 폴더의 alarms.json (앱의 AlarmStore 와 같은 파일). 읽기만 한다."""

import json

from . import credentials
from .connections import local_dir

WEEKDAY = {1: "일", 2: "월", 3: "화", 4: "수", 5: "목", 6: "금", 7: "토"}
EVENTS = {
    "github.review_requested": "리뷰어로 지정됐을 때",
    "github.my_pr_approved": "내 PR 이 승인됐을 때",
    "github.my_pr_feedback": "내 PR 에 변경 요청이나 댓글이 달렸을 때",
    "deploy.approval_requested": "내가 승인해야 할 배포가 생겼을 때",
    "deploy.my_release_status": "내 배포 상태가 바뀌었을 때",
    "jira.assigned_to_me": "나에게 새 이슈가 할당됐을 때",
    "jira.my_issue_status": "내 이슈 상태가 바뀌었을 때",
}


def _repeat(rule) -> str:
    if not isinstance(rule, dict):
        return ""
    if "daily" in rule:
        return "매일"
    if "weekdays" in rule:
        return "평일"
    if "days" in rule:
        days = rule["days"].get("_0", []) if isinstance(rule["days"], dict) else rule["days"]
        return "·".join(WEEKDAY.get(d, str(d)) for d in sorted(days))
    if "once" in rule:
        day = rule["once"].get("_0") if isinstance(rule["once"], dict) else rule["once"]
        return f"{day} 한 번"
    return ""


def _summary(a: dict) -> dict:
    trigger = a.get("trigger")
    prompt = (a.get("prompt") or "").strip()
    if trigger:
        when = f"트리거 · {EVENTS.get(trigger.get('event'), trigger.get('event'))} · {trigger.get('intervalMinutes') or 5}분마다 확인"
    else:
        when = f"{a.get('hour', 0):02d}:{a.get('minute', 0):02d} · {_repeat(a.get('repeatRule'))}"
    if a.get("command") and not trigger:
        task = f"명령 실행: {a['command']}"
    elif not prompt:
        task = "알림만"
    elif prompt.startswith("{{prompt:"):
        task = f"프롬프트 파일 prompts/{prompt[9:-2].strip()}.md"
    elif prompt.startswith("{{file:"):
        task = f"파일에 적힌 지시 ({prompt[7:-2].strip()})"
    elif prompt == "{{daily-report}}":
        task = "내 미완료 Jira 정리 보고"
    else:
        task = prompt[:200]
    return {
        "title": a.get("title") or "(제목 없음)",
        "enabled": a.get("enabled", True),
        "kind": "트리거" if trigger else "시각",
        "when": when,
        "task": task,
        "group": a.get("group"),
        "allowWrite": bool(a.get("allowWrite")) if trigger else False,
        "lastRunDay": a.get("lastRunDay"),
        "lastRun": None,
    }


def _runs() -> dict:
    path = credentials.data_dir() / "alarm-runs.json"
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def _today() -> str:
    import datetime as dt
    return dt.date.today().isoformat()


def _sessions_raw() -> tuple[dict, dict]:
    base = credentials.data_dir() / "automation"
    try:
        book = json.loads((base / "sessions.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        book = {}
    try:
        live = json.loads((base / "sessions-live.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        live = {}
    return book, live


def _session_row(s: dict, live: dict, archived: bool) -> dict:
    repo = s.get("repo", "")
    usage = s.get("usage") or {}
    total = sum(int(usage.get(k, 0) or 0) for k in ("input", "output", "cacheRead", "cacheWrite"))
    window = s.get("contextWindow")
    percent = min(100, int(s.get("contextTokens", 0) or 0) * 100 // window) if window else None
    return {
        "repo": repo,
        "sessionId": str(s.get("id", ""))[:8],
        "createdAt": s.get("createdAt"),
        "lastUsed": s.get("lastUsed"),
        "keys": s.get("keys") or [],
        "running": None if archived else (live.get("running") or {}).get(repo),
        "queued": [] if archived else (live.get("queued") or {}).get(repo, []),
        "contextPercent": percent,
        "tokens": total,
        "costUSD": round(float(usage.get("costUSD", 0) or 0), 4),
        "archived": archived,
    }


def _project_label(folder: str) -> str:
    if folder.endswith("-poke-mate-work"):
        return "PokeMate 대화·알람 실행"
    if "scratchpad" in folder or folder.startswith("-private-tmp") or folder.startswith("-tmp"):
        return "임시 폴더 실행"
    if "Application-Support" in folder and folder.endswith("-work"):
        return "예전 데이터 폴더 실행 기록"
    return "기타 폴더 실행"


def other_sessions(work_ids: set[str]) -> dict:
    import datetime as dt
    root = credentials.data_dir() / "claude-config" / "projects"
    groups: dict[str, dict] = {}
    for folder in sorted(root.glob("*")) if root.exists() else []:
        if not folder.is_dir():
            continue
        files = [f for f in folder.glob("*.jsonl") if f.stem not in work_ids]
        if not files:
            continue
        label = _project_label(folder.name)
        g = groups.setdefault(label, {"label": label, "count": 0, "lastUsed": None})
        g["count"] += len(files)
        last = dt.datetime.fromtimestamp(max(f.stat().st_mtime for f in files)).astimezone().isoformat(timespec="seconds")
        g["lastUsed"] = max(filter(None, [g["lastUsed"], last]))
    rows = sorted(groups.values(), key=lambda g: g["lastUsed"] or "", reverse=True)
    return {"total": sum(g["count"] for g in rows), "groups": rows,
            "note": "PokeMate 전용 로그인 폴더의 대화·알람 실행 기록이다. 사용자 본인의 터미널·데스크톱 Claude Code 세션(~/.claude/projects)은 별도 위치라 세지 않는다."}


def work_sessions(repo: str = "", key: str = "") -> dict:
    book, live = _sessions_raw()
    rows = [_session_row(s, live, False) for s in (book.get("sessions") or {}).values()]
    rows += [_session_row(s, live, True) for s in book.get("archived") or []]
    if repo:
        rows = [r for r in rows if repo.lower() in r["repo"].lower()]
    if key:
        rows = [r for r in rows if key in r["keys"] or r["running"] == key or key in r["queued"]]
    rows.sort(key=lambda r: str(r["lastUsed"] or ""), reverse=True)
    rows.sort(key=lambda r: r["archived"])
    routing = (live.get("running") or {}).get("(레포 정하는 중)")
    ids = {str(s.get("id", "")) for s in list((book.get("sessions") or {}).values()) + list(book.get("archived") or [])}
    return {"sessions": rows, "routing": routing, "liveUpdatedAt": live.get("updatedAt"),
            "activeCount": sum(1 for r in rows if not r["archived"]), "archivedCount": sum(1 for r in rows if r["archived"]),
            "otherSessions": other_sessions(ids),
            "screen": "위쪽 '작업' 탭 → 세션을 누르면 기록·입력창"}


def _session_by_key() -> dict:
    book, _ = _sessions_raw()
    found = {}
    for s in list((book.get("sessions") or {}).values()) + list(book.get("archived") or []):
        for k in s.get("keys") or []:
            found[k] = {"repo": s.get("repo"), "sessionId": str(s.get("id", ""))[:8]}
    return found


def history(key: str = "", limit: int = 30) -> dict:
    base = credentials.data_dir() / "automation"
    sessions = _session_by_key()
    events = []
    for path in sorted((base / "activity").glob("*.jsonl"), reverse=True):
        try:
            lines = path.read_text(encoding="utf-8").splitlines()
        except OSError:
            continue
        for line in reversed(lines):
            try:
                e = json.loads(line)
            except ValueError:
                continue
            if key and key not in json.dumps(e, ensure_ascii=False):
                continue
            session = sessions.get(e.get("key") or "")
            if session:
                e = {**e, "workSession": session}
            events.append(e)
            if len(events) >= limit:
                break
        if len(events) >= limit:
            break
    states = {}
    for name, field in (("jira-state.json", "processed"), ("github-review-state.json", "reviewed")):
        try:
            data = json.loads((base / "state" / name).read_text(encoding="utf-8")).get(field, {})
        except (OSError, ValueError):
            data = {}
        rows = {k: ({**v, "workSession": sessions[k]} if k in sessions else v) for k, v in data.items() if not key or key in k}
        states[name.replace(".json", "")] = dict(sorted(rows.items(), key=lambda kv: str(kv[1].get("processedAt") or kv[1].get("at") or ""), reverse=True)[:limit])
    return {"activity": events, **states}


def list_alarms() -> dict:
    path = local_dir() / "alarms.json"
    if not path.exists():
        return {"alarms": [], "count": 0}
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {"error": "READ_FAILED", "alarms": []}
    runs = _runs()
    alarms = []
    for a in raw:
        if not isinstance(a, dict):
            continue
        row = _summary(a)
        run = runs.get(str(a.get("id", "")))
        if isinstance(run, dict):
            row["lastRun"] = {"at": run.get("lastAt"), "result": run.get("result"), "failed": run.get("failed"),
                              "todayCount": run.get("todayCount") if run.get("day") == _today() else 0,
                              "output": run.get("output")}
        alarms.append(row)
    return {"alarms": alarms, "count": len(alarms), "enabled": sum(1 for a in alarms if a["enabled"])}
