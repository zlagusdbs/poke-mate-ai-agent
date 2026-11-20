"""자동 처리 helper. `pokemate auto <하위명령>` 으로 실행한다.

알람(트리거·명령 실행)과 자동 처리 프롬프트가 부르는 작은 명령 모음이다. 회사 주소·사내 CLI 이름은
shared/connections.local.json 에서 읽고, 상태·기록은 PokeMate 데이터 폴더 automation/ 아래에 둔다.
"""

import argparse
import datetime as dt
import glob
import json
import os
import re
import shutil
import subprocess
import sys
from urllib.parse import urlparse

from . import credentials
from .connections import find_system, load_settings, local_dir
from .credentials import data_dir
from .deploy_client import DeployClient
from .errors import PokeMateError
from .jira_client import AUTO_COMMENT_PREFIX, QUESTION_PREFIX, JiraClient
from .trial import FakeJira, overlay, trial_dir

HOME = os.path.expanduser("~")
SCHEDULE_GLOB = os.path.join(HOME, "Library", "Application Support", "Claude", "claude-code-sessions", "*", "*", "scheduled-tasks.json")
KST = dt.timezone(dt.timedelta(hours=9))
DEFAULT_RECURRING = ["회의", "휴가"]


def settings():
    return {**load_settings("automation"), **overlay("automation")}


def automation_dir():
    return os.path.join(str(data_dir()), "automation")


def activity_dir():
    return os.path.join(automation_dir(), "activity")


def state_dir():
    return os.path.join(automation_dir(), "state")


def log_dir():
    return os.path.join(automation_dir(), "logs")


def note_dir():
    return os.path.join(automation_dir(), "notes")


def prompt_dir():
    return os.path.join(str(local_dir()), "prompts")


def webhook_file():
    return os.path.join(str(data_dir()), "teams-webhook-url")


def work_dir():
    return os.path.expanduser(settings().get("workDir") or "~/Workspace")


def gh_host():
    system = find_system("github")
    host = urlparse(system.base_url).hostname if system and system.base_url else None
    return host or "github.com"


def gh_user():
    configured = settings().get("githubUser")
    if configured:
        return configured
    out = subprocess.run(["gh", "api", "user", "--jq", ".login"], capture_output=True, env=gh_env(), timeout=60)
    if out.returncode != 0:
        raise RuntimeError(out.stderr.decode().strip()[:300])
    return out.stdout.decode().strip()


def jira_browse():
    system = find_system("jira")
    base = (system.base_url or "").rstrip("/") if system else ""
    return base + "/browse/" if base else ""


def recurring_titles():
    return tuple(settings().get("recurringTitles") or DEFAULT_RECURRING)


def self_command(*args):
    return [sys.executable, "-m", "pokemate.automation", *args]


def now():
    return dt.datetime.now(KST)


def log_path(day):
    return os.path.join(activity_dir(), day.strftime("%Y-%m") + ".jsonl")


def append_event(event):
    os.makedirs(activity_dir(), exist_ok=True)
    event = {"time": now().isoformat(timespec="seconds"), **{k: v for k, v in event.items() if v not in (None, "")}}
    with open(log_path(now()), "a", encoding="utf-8") as f:
        f.write(json.dumps(event, ensure_ascii=False) + "\n")
    return event


def read_events(day):
    path = log_path(day)
    if not os.path.exists(path):
        return []
    prefix = day.strftime("%Y-%m-%d")
    events = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                e = json.loads(line)
            except ValueError:
                continue
            if str(e.get("time", "")).startswith(prefix):
                events.append(e)
    return events


def post_card(body, actions=None, dry_run=False):
    content = {
        "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
        "type": "AdaptiveCard",
        "version": "1.4",
        "msteams": {"width": "Full"},
        "body": body,
    }
    if actions:
        content["actions"] = actions[:6]
    payload = {"type": "message", "attachments": [{"contentType": "application/vnd.microsoft.card.adaptive", "contentUrl": None, "content": content}]}
    data = json.dumps(payload, ensure_ascii=False)
    if trial_dir() and not dry_run:
        print("trial: card not sent")
        return 0
    if dry_run:
        print(json.dumps(payload, ensure_ascii=False, indent=1))
        return 0
    with open(webhook_file(), encoding="utf-8") as f:
        url = f.read().strip()
    result = subprocess.run(
        ["curl", "-sS", "-o", "/dev/null", "-w", "%{http_code}", "-X", "POST", "-H", "Content-Type: application/json", "--data-binary", "@-", url],
        input=data.encode("utf-8"), capture_output=True, timeout=30,
    )
    code = result.stdout.decode().strip()
    if code not in ("200", "202"):
        print(f"teams post failed: HTTP {code} {result.stderr.decode().strip()}", file=sys.stderr)
        return 1
    print(f"teams post ok: HTTP {code}")
    return 0


def text(value, **kw):
    return {"type": "TextBlock", "text": value, "wrap": True, **kw}


def facts(pairs):
    return {"type": "FactSet", "facts": [{"title": k, "value": str(v)} for k, v in pairs if v not in (None, "")]}


def open_url(title, url):
    return {"type": "Action.OpenUrl", "title": title, "url": url}


ONCE_PER_TICKET = {"detected", "processed"}


def duplicate_event(args):
    if not args.notify or not args.key:
        return False
    window = dt.timedelta(hours=24 if args.event in ONCE_PER_TICKET else 1)
    since = now() - window
    for day in {now().date(), since.date()}:
        for e in read_events(dt.datetime.combine(day, dt.time(0, 0), KST)):
            if (e.get("task"), e.get("key"), e.get("event")) != (args.task, args.key, args.event) or not e.get("notified"):
                continue
            if args.event not in ONCE_PER_TICKET and e.get("result") != args.result:
                continue
            try:
                when = dt.datetime.fromisoformat(e["time"])
            except (KeyError, ValueError):
                continue
            if when >= since:
                return True
    return False


def cmd_event(args):
    if duplicate_event(args):
        print(f"skipped duplicate: {args.task} {args.key} {args.event}")
        return 0
    event = append_event({
        "source": args.source, "task": args.task, "event": args.event, "key": args.key, "title": args.title,
        "kind": args.kind, "result": args.result, "needs": args.needs, "url": args.url, "report": args.report,
        "notified": True if args.notify and not args.dry_run else None,
    })
    print("logged:", json.dumps(event, ensure_ascii=False))
    if not args.notify:
        return 0
    label = {"detected": "처리 시작", "processed": "처리 완료", "approved": "승인 완료", "commented": "댓글 리뷰 완료", "changed": "변경 알림", "blocked": "확인 필요", "failed": "실패"}.get(args.event, args.event)
    icon = {"detected": "⏳", "processed": "✅", "approved": "✅", "commented": "💬", "changed": "🔄", "blocked": "⚠️", "failed": "❌"}.get(args.event, "📌")
    head = f"{icon} {args.source.upper()} {label}" + (f" · {args.key}" if args.key else "")
    body = [text(head, weight="Bolder", size="Medium")]
    if args.title:
        body.append(text(args.title, isSubtle=True))
    body.append(facts([("유형", args.kind), ("결과", args.result), ("승인 필요", args.needs), ("보고서", args.report)]))
    url = args.url or (jira_browse() + args.key if args.source == "jira" and args.key and jira_browse() else None)
    return post_card(body, [open_url("열기", url)] if url else None, args.dry_run)


def gh_api(path, params=None):
    cmd = ["gh", "api", "-X", "GET", path]
    for k, v in (params or {}).items():
        cmd += ["-f", f"{k}={v}"]
    out = subprocess.run(cmd, capture_output=True, env=gh_env(), timeout=60)
    if out.returncode != 0:
        raise RuntimeError(out.stderr.decode().strip()[:300])
    return json.loads(out.stdout.decode() or "null")


def gh_search(query, per_page=20):
    return gh_api("search/issues", {"q": query, "per_page": str(per_page)})


def repo_of(item):
    return item["repository_url"].rsplit("/repos/", 1)[-1]


def my_comment_count(day):
    start = day.strftime("%Y-%m-%d")
    since = dt.datetime.combine(day, dt.time(0, 0), KST).astimezone(dt.timezone.utc)
    found = gh_search(f"is:pr commenter:{gh_user()} updated:>={start}", 50)
    total = 0
    touched = []
    for item in found.get("items", []):
        repo, number = repo_of(item), item["number"]
        count = 0
        for path in (f"repos/{repo}/issues/{number}/comments", f"repos/{repo}/pulls/{number}/comments"):
            for c in gh_api(path, {"since": since.strftime("%Y-%m-%dT%H:%M:%SZ"), "per_page": "100"}) or []:
                if c.get("user", {}).get("login") == gh_user():
                    count += 1
        for r in gh_api(f"repos/{repo}/pulls/{number}/reviews", {"per_page": "100"}) or []:
            submitted = r.get("submitted_at")
            if r.get("user", {}).get("login") == gh_user() and submitted and r.get("body") and submitted >= since.strftime("%Y-%m-%dT%H:%M:%SZ"):
                count += 1
        if count:
            total += count
            touched.append((repo, number, item["title"], item["html_url"], count))
    return total, touched


def github_snapshot():
    review = gh_search(f"is:pr is:open review-requested:{gh_user()}")
    mine = gh_search(f"is:pr is:open author:{gh_user()}")
    return review, mine


def cmd_github_check(args):
    os.makedirs(state_dir(), exist_ok=True)
    state_file = os.path.join(state_dir(), "github-review-requests.json")
    seen = set()
    first_run = not os.path.exists(state_file)
    if not first_run:
        with open(state_file, encoding="utf-8") as f:
            seen = set(json.load(f))
    review = gh_search(f"is:pr is:open review-requested:{gh_user()}", 50)
    current = {item["html_url"]: item for item in review.get("items", [])}
    new = [current[u] for u in current if u not in seen]
    with open(state_file, "w", encoding="utf-8") as f:
        json.dump(sorted(current), f)
    if first_run:
        print(f"baseline saved: {len(current)} open review requests, no alert on first run")
        return 0
    if not new:
        print("no new review requests")
        return 0
    for item in new:
        append_event({"source": "github", "event": "review_requested", "key": f"{repo_of(item)}#{item['number']}", "title": item["title"], "url": item["html_url"]})
    body = [text(f"👀 리뷰 요청 {len(new)}건", weight="Bolder", size="Medium")]
    for item in new[:10]:
        body.append(text(f"[{repo_of(item)}#{item['number']}]({item['html_url']}) {item['title']} — {item['user']['login']}"))
    return post_card(body, None, args.dry_run)


def scheduled_tasks():
    tasks = {}
    for path in glob.glob(SCHEDULE_GLOB):
        try:
            with open(path, encoding="utf-8") as f:
                for t in json.load(f).get("scheduledTasks", []):
                    tasks[t["id"]] = t
        except (OSError, ValueError, KeyError):
            continue
    return list(tasks.values())


def cmd_summary(args):
    day = dt.date.fromisoformat(args.date) if args.date else now().date()
    events = read_events(dt.datetime.combine(day, dt.time(), KST))
    body = [text(f"🗓 하루 요약 · {day.strftime('%m/%d')} ({'월화수목금토일'[day.weekday()]})", weight="Bolder", size="Large")]

    tasks = scheduled_tasks()
    runs = {}
    for e in events:
        if e.get("event") == "run" and e.get("task"):
            runs[e["task"]] = runs.get(e["task"], 0) + 1
    body.append(text("예약 작업", weight="Bolder", spacing="Medium"))
    if tasks:
        body.append(facts([(t.get("displayName") or t["id"], f"{'켜짐' if t.get('enabled', True) else '꺼짐'} · {t.get('cronExpression') or '수동/1회'} · 오늘 실행 {runs.get(t['id'], 0)}회") for t in tasks]))
    else:
        body.append(text("없음", isSubtle=True))

    jira = [e for e in events if e.get("source") == "jira"]
    processed = [e for e in jira if e.get("event") == "processed"]
    changed = [e for e in jira if e.get("event") == "changed"]
    needs = [e for e in processed if e.get("needs")]
    body.append(text("Jira", weight="Bolder", spacing="Medium"))
    body.append(facts([("처리", f"{len(processed)}건"), ("변경 알림", f"{len(changed)}건"), ("승인 대기", f"{len(needs)}건")]))
    for e in processed[:10]:
        line = f"[{e.get('key')}]({jira_browse()}{e.get('key')}) {e.get('title', '')} — {e.get('result', '')}"
        if e.get("needs"):
            line += f" · 승인 필요: {e['needs']}"
        body.append(text(line, size="Small"))

    body.append(text("GitHub", weight="Bolder", spacing="Medium"))
    actions = []
    try:
        review, mine = github_snapshot()
        comments, touched = my_comment_count(day)
        gh_events = [e for e in events if e.get("source") == "github"]
        new_requests = [e for e in gh_events if e.get("event") in ("review_requested", "detected")]
        approved = [e for e in gh_events if e.get("event") == "approved"]
        commented = [e for e in gh_events if e.get("event") == "commented"]
        body.append(facts([
            ("리뷰할 PR", f"{review.get('total_count', 0)}개 (오늘 새로 {len(new_requests)}개)"),
            ("자동 리뷰", f"승인 {len(approved)}개 · 댓글 {len(commented)}개"),
            ("내가 연 PR", f"{mine.get('total_count', 0)}개"),
            ("오늘 단 댓글", f"{comments}개 · PR {len(touched)}개"),
        ]))
        for item in review.get("items", [])[:5]:
            body.append(text(f"리뷰: [{repo_of(item)}#{item['number']}]({item['html_url']}) {item['title']}", size="Small"))
        actions.append(open_url("리뷰 요청 PR", f"https://{gh_host()}/pulls/review-requested"))
    except Exception as exc:
        body.append(text(f"조회 실패: {exc}", isSubtle=True))

    return post_card(body, actions, args.dry_run)


def curl_json(url):
    out = subprocess.run(["curl", "-sS", "-m", "20", "-f", url], capture_output=True, timeout=30)
    if out.returncode != 0:
        raise RuntimeError(out.stderr.decode().strip()[:200])
    return json.loads(out.stdout.decode())


def kr_holidays(years):
    days = {}
    for year in sorted(years):
        for h in curl_json(f"https://date.nager.at/api/v3/PublicHolidays/{year}/{settings().get('holidayCountry') or 'KR'}"):
            days[h["date"]] = h.get("localName") or h.get("name")
    return days


def jira():
    if trial_dir():
        return FakeJira(trial_dir())
    system = find_system("jira")
    cred = credentials.load("jira")
    if system is None or not system.base_url or cred is None:
        raise SystemExit("Jira 가 연결돼 있지 않아요. PokeMate 연결 메뉴에서 연결해 주세요")
    return JiraClient(system.base_url, cred)


def person(p):
    if not isinstance(p, dict):
        return None
    return {"name": p.get("name"), "key": p.get("key"), "displayName": p.get("displayName")}


def link_key(value):
    if isinstance(value, dict):
        inner = value.get("data") if isinstance(value.get("data"), dict) else value
        return inner.get("key") or inner.get("issueKey") or None
    return value or None


def flat_issue(raw, base_url="", epic_field=None, parent_field=None):
    f = raw.get("fields") or {}
    links = []
    for link in f.get("issuelinks") or []:
        other = link.get("outwardIssue") or link.get("inwardIssue") or {}
        kind = (link.get("type") or {}).get("outward" if link.get("outwardIssue") else "inward")
        of = other.get("fields") or {}
        links.append({"type": kind, "key": other.get("key"), "summary": of.get("summary"), "status": (of.get("status") or {}).get("name")})
    return {
        "key": raw.get("key"),
        "summary": f.get("summary") or "",
        "type": (f.get("issuetype") or {}).get("name"),
        "status": (f.get("status") or {}).get("name"),
        "assignee": person(f.get("assignee")),
        "reporter": person(f.get("reporter")),
        "priority": (f.get("priority") or {}).get("name"),
        "epicLink": f.get(epic_field) if epic_field else None,
        "parentLink": link_key(f.get(parent_field)) if parent_field else None,
        "labels": f.get("labels") or [],
        "components": [c.get("name") for c in f.get("components") or []],
        "created": f.get("created"),
        "updated": f.get("updated"),
        "duedate": f.get("duedate"),
        "description": f.get("description") or "",
        "issueLinks": links,
        "url": f"{base_url}/browse/{raw.get('key')}" if base_url else None,
    }


def is_me(user, me):
    if not isinstance(user, dict):
        return False
    names = {(me.get("name") or "").lower(), (me.get("key") or "").lower()} - {""}
    return (user.get("name") or "").lower() in names or (user.get("key") or "").lower() in names


def my_worklog_seconds(start, end):
    client = jira()
    jql = f'worklogAuthor = currentUser() AND worklogDate >= "{start}" AND worklogDate <= "{end}"'
    issues = client.search_raw(jql, 200, "summary")
    me = client.me()
    me_name = (me.get("name") or me.get("key") or "").lower()
    per_day = {}
    detail = {}
    for issue in issues:
        key = issue.get("key")
        logs = client.worklogs(key)
        for w in logs:
            author = w.get("author") or {}
            who = (author.get("name") or author.get("key") or "").lower()
            if me_name and who != me_name:
                continue
            day = str(w.get("started", ""))[:10]
            if start <= day <= end:
                sec = int(w.get("timeSpentSeconds") or 0)
                per_day[day] = per_day.get(day, 0) + sec
                detail.setdefault(day, []).append((key, sec))
    return per_day, detail


def previous_business_day(today, holidays):
    d = today - dt.timedelta(days=1)
    while d.weekday() >= 5 or d.isoformat() in holidays:
        d -= dt.timedelta(days=1)
    return d


def cmd_worklog_check(args):
    today = dt.date.fromisoformat(args.today) if args.today else now().date()
    notes = []
    try:
        holidays = kr_holidays({today.year, today.year - 1 if today.month == 1 else today.year})
    except Exception as exc:
        holidays = {}
        notes.append(f"공휴일 조회 실패({exc}) — 주말만 제외함")
    target = previous_business_day(today, holidays)
    start = target.replace(day=1)
    per_day, detail = my_worklog_seconds(start.isoformat(), target.isoformat())
    need = int(args.hours * 3600)
    short = []
    d = start
    while d <= target:
        iso = d.isoformat()
        if d.weekday() < 5 and iso not in holidays:
            got = per_day.get(iso, 0)
            if got < need:
                short.append((iso, got))
        d += dt.timedelta(days=1)
    target_short = [x for x in short if x[0] == target.isoformat()]
    print(json.dumps({"target": target.isoformat(), "short": short, "notes": notes}, ensure_ascii=False))
    if not short:
        print("no gaps")
        return 0
    append_event({"source": "worklog", "event": "short", "key": target.isoformat(), "result": f"{len(short)}일 부족"})
    wd = "월화수목금토일"
    def fmt(iso, sec):
        dd = dt.date.fromisoformat(iso)
        return f"{dd.strftime('%m/%d')}({wd[dd.weekday()]})", f"{sec / 3600:g}h / {args.hours:g}h" + (" · 기록 없음" if sec == 0 else f" · 부족 {(need - sec) / 3600:g}h")
    body = [text("🕘 근무시간 기록 확인", weight="Bolder", size="Medium")]
    if target_short:
        body.append(text(f"직전 근무일({fmt(*target_short[0])[0]}) 기록이 모자랍니다.", weight="Bolder"))
    else:
        body.append(text(f"직전 근무일({fmt(target.isoformat(), per_day.get(target.isoformat(), 0))[0]})은 채워졌고, 이번 달에 모자란 날이 남아 있습니다."))
    body.append(facts([fmt(iso, sec) for iso, sec in short]))
    for iso, sec in target_short:
        items = ", ".join(f"{k} {s / 3600:g}h" for k, s in detail.get(iso, []))
        if items:
            body.append(text(f"{iso} 기록: {items}", size="Small", isSubtle=True))
    for n in notes:
        body.append(text(n, size="Small", isSubtle=True))
    calendar = settings().get("worklogCalendarUrl")
    title = settings().get("worklogCalendarTitle") or "근무 캘린더"
    return post_card(body, [open_url(title, calendar)] if calendar else None, args.dry_run)

if sys.platform == "win32":
    _PROGRAM_FILES = os.environ.get("ProgramFiles", r"C:\Program Files")
    EXTRA_PATH = [os.path.join(HOME, ".local", "bin"), os.path.join(_PROGRAM_FILES, "Git", "cmd"), os.path.join(_PROGRAM_FILES, "GitHub CLI")]
else:
    EXTRA_PATH = [os.path.join(HOME, ".local", "bin"), "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"]


def _shell_command(command: str) -> list[str]:
    """테스트 명령을 돌릴 셸 argv. macOS 는 zsh, Windows 는 Git Bash 가 있으면 bash, 없으면 cmd."""
    if sys.platform == "win32":
        bash = shutil.which("bash")
        if bash and "system32" not in bash.lower():
            return [bash, "-lc", command]
        return ["cmd", "/c", command]
    return ["/bin/zsh", "-lc", command]


def jira_state_file():
    return os.path.join(state_dir(), "jira-state.json")


def gh_review_state_file():
    return os.path.join(state_dir(), "github-review-state.json")


def poll_state_file():
    return os.path.join(state_dir(), "poll-state.json")

MAX_ATTEMPTS = 3
RETRY_GAP_SECONDS = 300


def load_json(path, default):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return default


def save_json(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
    os.replace(tmp, path)


def pid_alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except (OSError, TypeError):
        return False


def jira_candidates():
    jql = "assignee = currentUser() AND resolution = Unresolved ORDER BY updated DESC"
    issues = [flat_issue(raw) for raw in jira().search_raw(jql, 50, "summary,updated")]
    processed = load_json(jira_state_file(), {}).get("processed", {})
    todo = []
    for issue in issues:
        key = issue.get("key")
        title = issue.get("summary") or ""
        if not key or any(title.strip().startswith(t) for t in recurring_titles()):
            continue
        seen = processed.get(key)
        if seen is None:
            todo.append(f"{key}:new")
        elif str(seen.get("updated")) != str(issue.get("updated")):
            todo.append(f"{key}:changed")
    return todo


def github_candidates():
    q = f"is:pr is:open draft:false user-review-requested:{gh_user()} -author:{gh_user()}"
    items = gh_search(q, 50).get("items", [])
    state = load_json(gh_review_state_file(), {"baseline": [], "reviewed": {}})
    baseline = set(state.get("baseline", []))
    reviewed = state.get("reviewed", {})
    todo = []
    for item in items:
        url = item["html_url"]
        if url in baseline:
            continue
        done = reviewed.get(url)
        if done is None:
            todo.append(f"{url}:new")
            continue
        sha = gh_api(f"repos/{repo_of(item)}/pulls/{item['number']}").get("head", {}).get("sha")
        if sha and sha != done.get("sha"):
            todo.append(f"{url}:{sha[:12]}")
    return todo


def launch_claude(job, prompt_file, found):
    with open(os.path.join(prompt_dir(), prompt_file), encoding="utf-8") as f:
        prompt = f.read()
    prompt += "\n\n## 이번 실행\n확인기가 찾은 대상: " + ", ".join(found) + "\n위 1번 절차로 다시 조회해 확인한 뒤 처리한다."
    os.makedirs(log_dir(), exist_ok=True)
    stamp = now().strftime("%Y%m%d-%H%M%S")
    log_file = os.path.join(log_dir(), f"{job}-{stamp}.json")
    runner = (
        "import json,subprocess,sys\n"
        "cmd,log,job,notify=json.loads(sys.argv[1])\n"
        "r=subprocess.run(cmd,capture_output=True)\n"
        "open(log,'wb').write(r.stdout+b'\\n---stderr---\\n'+r.stderr)\n"
        "err=r.returncode!=0\n"
        "try:\n"
        " err=err or json.loads(r.stdout.decode()).get('is_error',False)\n"
        "except Exception:\n"
        " err=True\n"
        "if err:\n"
        " subprocess.run(notify+['event','--notify','--source','poll','--task',job,'--event','failed','--result','claude 실행 실패 (로그 '+log+')'])\n"
    )
    cmd = ["claude", "-p", "--permission-mode", "auto", "--output-format", "json", prompt]
    proc = subprocess.Popen(
        [sys.executable, "-c", runner, json.dumps([cmd, log_file, job, self_command()])],
        cwd=work_dir(), stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True,
    )
    return proc.pid, log_file


GIT_READ_SUBCOMMANDS = {"fetch", "show", "log", "diff", "symbolic-ref", "rev-parse", "ls-tree", "cat-file", "merge-base", "grep", "ls-remote", "status"}
GIT_BRANCH_READ_FLAGS = {"-r", "-a", "--list", "--contains", "--remotes", "--all", "-v", "-vv", "--format", "--merged", "--no-merged", "--sort", "--points-at"}


def repo_path(name):
    base = os.path.realpath(work_dir())
    path = os.path.realpath(os.path.join(base, name))
    if not path.startswith(base + os.sep) or not os.path.isdir(os.path.join(path, ".git")):
        raise SystemExit(f"repo not allowed: {name} (must be a git repo directly under {base})")
    return path


def run_checked(cmd, cwd=None, env=None, input_bytes=None):
    out = subprocess.run(cmd, cwd=cwd, env=env, input=input_bytes, capture_output=True, timeout=300)
    sys.stdout.write(out.stdout.decode(errors="replace"))
    sys.stderr.write(out.stderr.decode(errors="replace"))
    return out.returncode


def cmd_git_read(args):
    path = repo_path(args.repo)
    rest = [a for a in args.rest if a != "--"]
    if not rest:
        raise SystemExit("git-read needs a subcommand")
    sub = rest[0]
    if sub == "branch":
        flags = [a for a in rest[1:] if a.startswith("-")]
        if not flags or any(f.split("=")[0] not in GIT_BRANCH_READ_FLAGS for f in flags):
            raise SystemExit("git-read branch: only listing flags are allowed (-r, -a, --list, --contains ...)")
    elif sub not in GIT_READ_SUBCOMMANDS:
        raise SystemExit(f"git-read: subcommand not allowed: {sub}")
    if sub == "fetch" and any(a.startswith("--") and a not in ("--prune", "--quiet", "--all") for a in rest[1:]):
        raise SystemExit("git-read fetch: only --prune/--quiet/--all allowed")
    return run_checked(["git", "-C", path, *rest])


def git_ref_exists(path, ref):
    return subprocess.run(["git", "-C", path, "rev-parse", "--verify", "--quiet", ref], capture_output=True).returncode == 0


def cmd_create_branch(args):
    path = repo_path(args.repo)
    branch = args.branch
    if not branch.startswith("feature/") or ".." in branch or branch.endswith("/") or " " in branch:
        raise SystemExit("branch must look like feature/...")
    subprocess.run(["git", "-C", path, "fetch", "--quiet", "origin"], check=False)
    if git_ref_exists(path, f"refs/remotes/origin/{branch}"):
        print(f"exists: origin/{branch} (not created)")
        return 0
    start = args.start
    if not git_ref_exists(path, start):
        raise SystemExit(f"start ref not found: {start}")
    if not git_ref_exists(path, f"refs/heads/{branch}"):
        rc = run_checked(["git", "-C", path, "branch", "--no-track", branch, start])
        if rc != 0:
            return rc
    rc = run_checked(["git", "-C", path, "push", "origin", f"refs/heads/{branch}:refs/heads/{branch}"])
    if rc == 0:
        print(f"created and pushed: {branch} from {start}")
    return rc


AUTO_PREFIX = AUTO_COMMENT_PREFIX
MAX_AUTO_COMMENTS = 3


def require_mine(client, key):
    issue = client.get_issue(key)
    me = client.me()
    if not is_me((issue.get("fields") or {}).get("assignee"), me):
        raise SystemExit(f"{key} is not assigned to me; refusing")
    return issue, me


def print_json(data):
    print(json.dumps(data, ensure_ascii=False, indent=1))
    return 0


def cmd_jira_get(args):
    client = jira()
    epic = client.field_id("Epic Link")
    parent = client.field_id("Parent Link")
    return print_json(flat_issue(client.get_issue(args.key), client.base_url, epic, parent))


def cmd_jira_search(args):
    client = jira()
    return print_json([flat_issue(raw, client.base_url) for raw in client.search_raw(args.jql, args.max)])


def cmd_jira_comments(args):
    rows = [{"id": c.get("id"), "author": person(c.get("author")), "created": c.get("created"), "body": c.get("body") or ""}
            for c in jira().comments(args.key)]
    return print_json(rows)


def cmd_jira_links(args):
    client = jira()
    remote = [{"title": (r.get("object") or {}).get("title"), "url": (r.get("object") or {}).get("url"),
               "relationship": r.get("relationship")} for r in client.remote_links(args.key)]
    return print_json({"issueLinks": flat_issue(client.get_issue(args.key))["issueLinks"], "remoteLinks": remote})


COMMENT_KINDS = {"plan": ("처리 계획", "실행 절차"), "question": ("확인 필요",), "result": ("처리 결과", "개발 중단")}


def comment_kind(body):
    head = body[len(AUTO_PREFIX):].strip().splitlines()[0] if body.startswith(AUTO_PREFIX) and body[len(AUTO_PREFIX):].strip() else ""
    return next((kind for kind, words in COMMENT_KINDS.items() if any(head.startswith(w) for w in words)), None)


def cmd_jira_comment(args):
    with open(os.path.expanduser(args.body_file), encoding="utf-8") as f:
        body = f.read().strip()
    if not body:
        raise SystemExit("empty comment body")
    if not body.startswith(AUTO_PREFIX):
        raise SystemExit(f"comment must start with {AUTO_PREFIX}")
    client = jira()
    require_mine(client, args.key)
    me = client.me()
    mine = [c for c in client.comments(args.key) if is_me(c.get("author"), me) and (c.get("body") or "").startswith(AUTO_PREFIX)]
    kind = comment_kind(body)
    if kind and any(comment_kind((c.get("body") or "").strip()) == kind for c in mine):
        print(f"skipped: {args.key} already has a {kind} comment (not posted again)")
        return 0
    if len(mine) >= MAX_AUTO_COMMENTS:
        raise SystemExit(f"{args.key} already has {len(mine)} automatic comments; refusing")
    if body.startswith(QUESTION_PREFIX) and any((c.get("body") or "").startswith(QUESTION_PREFIX) for c in mine):
        raise SystemExit(f"{args.key} already has a question comment; refusing to ask again")
    data = client.add_comment(args.key, body)
    print("commented", args.key, data.get("id"))
    return 0


def cmd_jira_transition(args):
    targets = settings().get("transitions") or {}
    target = targets.get(args.to)
    if not target:
        raise SystemExit(f"automation.transitions.{args.to} 가 설정되지 않았어요")
    client = jira()
    issue, _ = require_mine(client, args.key)
    current = ((issue.get("fields") or {}).get("status") or {}).get("name")
    if (current or "").lower() == target.lower():
        print(f"{args.key} already {current}")
        return 0
    found = next((t for t in client.transitions(args.key)
                  if (t.get("to") or {}).get("name", "").lower() == target.lower() or (t.get("name") or "").lower() == target.lower()), None)
    if found is None:
        raise SystemExit(f"{args.key}: '{target}' 로 바꿀 수 없어요 (지금 {current})")
    client.transition(args.key, found["id"])
    print(f"{args.key}: {current} -> {(found.get('to') or {}).get('name') or target}")
    return 0


def deploy():
    system = find_system("deploy")
    cred = credentials.load("deploy")
    if system is None or not system.enabled or not system.base_url or cred is None:
        raise SystemExit("배포 시스템이 연결돼 있지 않아요. PokeMate 연결 메뉴에서 연결해 주세요")
    return system, cred, DeployClient(system.base_url, cred)


def cmd_deploy_approve(args):
    """내가 승인자인 승인 대기 배포를 승인한다. 대상은 '내가 승인할 배포' 목록에 있는 것뿐이다."""
    system, cred, client = deploy()
    me = (cred.username or "").split("\\")[-1].split("@")[0].lower()
    web = (system.web_url or system.base_url or "").rstrip("/")
    try:
        pending = client.approvals()
    except PokeMateError as e:
        raise SystemExit(f"승인 대기 목록을 읽지 못했어요: {e}")
    approved, skipped, failed = [], [], []
    for r in pending:
        if args.id and str(r["id"]) != str(args.id):
            continue
        row = {"id": r["id"], "summary": r["summary"], "requester": r["creatingUser"]}
        if (r.get("status") or "").lower() != "open":
            skipped.append({**row, "reason": f"승인 대기가 아님 ({r.get('status')})"})
            continue
        if (r.get("creatingUser") or "").lower() == me:
            skipped.append({**row, "reason": "내가 만든 배포"})
            continue
        try:
            name = client.domain_name(r["domainId"])
            if not name:
                raise SystemExit(f"도메인 {r['domainId']} 이름을 찾지 못했어요")
            row.update(domain=name, url=f"{web}/{name}/releases/{r['id']}")
            if not args.dry_run:
                client.approve(name, r["id"])
                append_event({"source": "deploy", "task": "deploy-approve", "event": "approved", "key": str(r["id"]),
                              "title": r["summary"], "result": f"{name} · 요청자 {r['creatingUser']}", "url": row["url"]})
            approved.append(row)
        except (PokeMateError, SystemExit) as e:
            failed.append({**row, "reason": str(e)})
    if args.id and not (approved or skipped or failed):
        failed.append({"id": args.id, "reason": "내가 승인할 배포 목록에 없음"})
    print_json({"dryRun": bool(args.dry_run), "approved": approved, "skipped": skipped, "failed": failed})
    return 1 if failed else 0


def gh_env():
    return {**os.environ, "GH_HOST": gh_host()}


def cmd_gh_get(args):
    cmd = ["gh", "api", "-X", "GET", args.path]
    for kv in args.field or []:
        cmd += ["-f", kv]
    if args.jq:
        cmd += ["--jq", args.jq]
    if args.paginate:
        cmd.append("--paginate")
    return run_checked(cmd, env=gh_env())


def cmd_gh_diff(args):
    return run_checked(["gh", "pr", "diff", str(args.pr), "-R", args.repo], env=gh_env())


REPO_NAME = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")


def cmd_gh_file(args):
    if not REPO_NAME.match(args.repo):
        raise SystemExit("repo must look like owner/name")
    allowed = os.environ.get("POKEMATE_REVIEW_REPO")
    if allowed and args.repo.lower() != allowed.lower():
        raise SystemExit(f"gh-file: 이번 리뷰 저장소({allowed})만 읽을 수 있어요")
    path = args.path.strip().lstrip("/")
    if not path or ".." in path.split("/"):
        raise SystemExit("bad path")
    data = gh_api(f"repos/{args.repo}/contents/{path}", {"ref": args.ref})
    if isinstance(data, list):
        print("\n".join(sorted(f"{x.get('type')}\t{x.get('path')}" for x in data)))
        return 0
    if (data or {}).get("encoding") != "base64":
        raise SystemExit("gh-file: 파일 내용을 받지 못했어요")
    import base64
    sys.stdout.write(base64.b64decode(data.get("content") or "").decode("utf-8", "replace"))
    return 0


DOMAIN_NAME = re.compile(r"^[^/\\:\s.][^/\\:\s]*$")
MEMO_WARN_LINES = 300
NOTE_KINDS = ("용어집", "업무 규칙", "장애", "함정", "결정", "동작")
NOTES_FRAME = """# {domain} — 확인한 사실

쓰는 규칙은 `../README.md`. 한 줄에 사실 하나, 끝에 근거와 날짜.

## 용어집 (업무 용어 ↔ 코드 이름 ↔ 별칭)

## 업무 규칙 (규칙, 예외, 근거)

## 장애 (증상, 원인, 조치, 재발 방지)

## 함정 (이렇게 하면 터진다, 왜, 대신 이렇게)

## 결정 (무엇을, 왜, 버린 대안)

## 동작 (코드로 확인한 구조·흐름)
"""
CHECKLIST_FRAME = """# {domain} — 점검표 (초안, 사람 확인 전)

이 도메인을 건드리는 **개발과 리뷰 모두** 이 질문에 하나씩 답한다.
답은 셋 중 하나: `해당 없음` / `확인함 (근거)` / `미확인 (무엇을 누구에게 물어야 하는지)`.
미확인이 하나라도 있으면 리뷰는 승인하지 않고, 개발은 멈추고 묻는다.

| # | 질문 | 왜 묻나 |
|---|---|---|

## 사람이 확인할 것
- 아직 질문이 없다. 이 도메인을 건드릴 때 반드시 답할 질문을 채워 달라.
- 확정되면 맨 위의 "(초안, 사람 확인 전)"을 지운다.
"""


def notes_root():
    from pathlib import Path
    configured = os.environ.get("POKEMATE_NOTES_DIR") or settings().get("domainNotesDir")
    if configured:
        return Path(os.path.expanduser(configured))
    if trial_dir():
        return Path(trial_dir()) / "notes"
    return Path(HOME) / ".claude" / "references" / "notes"


def ensure_domain_notes(domain):
    folder = notes_root() / domain
    created = []
    folder.mkdir(parents=True, exist_ok=True)
    for name, frame in (("checklist.md", CHECKLIST_FRAME), ("notes.md", NOTES_FRAME)):
        path = folder / name
        if not path.exists():
            path.write_text(frame.format(domain=domain), encoding="utf-8")
            created.append(name)
    if created:
        print(f"created {folder}: {'·'.join(created)} 빈 틀. 이 도메인의 점검표가 아직 없어요(사람이 채울 때까지 초안). 결과와 알림에 알린다")
    return folder


def insert_note(lines, kind, entry):
    head = next((i for i, line in enumerate(lines) if line == f"## {kind}" or line.startswith(f"## {kind} ")), None)
    if head is None:
        while lines and not lines[-1].strip():
            lines.pop()
        lines += ["", f"## {kind}"]
        head = len(lines) - 1
    end = next((i for i in range(head + 1, len(lines)) if lines[i].startswith("## ")), len(lines))
    pos = next((i + 1 for i in range(end - 1, head, -1) if lines[i].strip()), head + 1)
    lines.insert(pos, entry)
    if pos + 1 < len(lines) and lines[pos + 1].startswith("## "):
        lines.insert(pos + 1, "")
    return lines


def cmd_domain_note(args):
    if not DOMAIN_NAME.match(args.domain):
        raise SystemExit("bad domain name")
    if args.domain == "default":
        raise SystemExit("도메인을 정하지 못한 건(default)은 노트를 남기지 않아요")
    text = " ".join((args.text or "").split())
    if not text and not args.init:
        raise SystemExit("empty note")
    if text and not (args.kind and args.source):
        raise SystemExit("--kind 와 --source 가 필요해요")
    folder = ensure_domain_notes(args.domain)
    if not text:
        print(f"ready {folder}")
        return 0
    path = folder / "notes.md"
    lines = path.read_text(encoding="utf-8").splitlines()
    if any(line.startswith(f"- {text} (") for line in lines):
        print(f"skipped: already noted ({path})")
        return 0
    insert_note(lines, args.kind, f"- {text} (출처: {args.source}, {now().strftime('%Y-%m-%d')})")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    count = len(lines)
    print(f"noted {path} [{args.kind}] ({count}줄)" + (f" · {MEMO_WARN_LINES}줄이 넘었어요. 사람에게 알린다(주제별로 나누거나 팀 KB 로)" if count > MEMO_WARN_LINES else ""))
    return 0


def cmd_domain_map(args):
    args.topic = "-".join(args.topic.split())
    for value, label in ((args.domain, "domain"), (args.topic, "topic")):
        if not DOMAIN_NAME.match(value):
            raise SystemExit(f"bad {label} name")
    if args.domain == "default":
        raise SystemExit("도메인을 정하지 못한 건(default)은 지도를 남기지 않아요")
    with open(os.path.expanduser(args.body_file), encoding="utf-8") as f:
        body = f.read().strip()
    if not body:
        raise SystemExit("empty map")
    folder = ensure_domain_notes(args.domain) / "maps"
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / f"{args.topic}.md"
    existed = path.exists()
    if not body.startswith("# "):
        body = f"# {args.topic}\n\n{body}"
    path.write_text(f"{body}\n\n갱신: {now().strftime('%Y-%m-%d')} · 출처: {args.source}\n", encoding="utf-8")
    lines = path.read_text(encoding="utf-8").count("\n")
    print(f"{'updated' if existed else 'created'} {path} ({lines}줄)")
    return 0


EVAL_READ_ONLY = {"gh-get", "gh-diff", "gh-file", "git-read", "jira-get", "jira-search", "jira-comments", "jira-links", "eval-score"}
EVAL_SECTIONS = ("확인한 범위", "참고한 근거", "점검표", "결함", "미확인(질문)", "미확인", "버린 후보와 이유", "제안", "판정", "점검표 추가 제안")
EVAL_BLOCK = re.compile(r"```pokemate-eval[ \t]*\n(.*?)```", re.S)
EVAL_HEAD = re.compile(r"^[\s#*>\-]*(" + "|".join(re.escape(n) for n in sorted(EVAL_SECTIONS, key=len, reverse=True)) + r")\s*[*]*\s*(?::|：|$)(.*)$")


def eval_blocked(args):
    if not os.environ.get("POKEMATE_EVAL"):
        return None
    if args.cmd in EVAL_READ_ONLY or (args.cmd == "review-state" and getattr(args, "check", False)):
        return None
    return f"평가 실행이라 쓰기 명령은 막혀 있어요: {args.cmd}"


def eval_sections(text):
    blocks = EVAL_BLOCK.findall(text)
    body = blocks[-1] if blocks else text
    sections, current = {}, None
    for line in body.splitlines():
        m = EVAL_HEAD.match(line)
        if m:
            current = "미확인(질문)" if m.group(1) == "미확인" else m.group(1)
            rest = m.group(2).strip()
            sections.setdefault(current, [])
            if rest:
                sections[current].append(rest)
        elif current:
            sections[current].append(line)
    return {k: "\n".join(v).strip() for k, v in sections.items()}


def eval_verdict(text):
    patterns = {"확신할 수 없음": r"확신할 수 없음", "댓글": r"댓글", "승인": r"승인(?!\s*하지|\s*안\s|\s*불가)"}
    hits = [(m.start(), name) for name, pat in patterns.items() for m in [re.search(pat, text)] if m]
    return min(hits)[1] if hits else None


def eval_score_case(case, text):
    sec = eval_sections(text)
    unsure = "\n".join(line for line in sec.get("점검표", "").splitlines() if "미확인" in line)
    parts = {"결함": sec.get("결함", ""), "미확인": sec.get("미확인(질문)", "") + "\n" + unsure}
    found, missed = [], []
    for item in case.get("mustFind") or []:
        scope = "\n".join(parts.get(name, "") for name in (item.get("in") or ["결함", "미확인"])).lower()
        groups = item.get("all") or []
        hit = bool(groups) and all(any(w.lower() in scope for w in group) for group in groups)
        (found if hit else missed).append(item.get("name") or "/".join(groups[0] if groups else []))
    verdict = eval_verdict(sec.get("판정", ""))
    depth = next((d for d in ("가볍게", "보통", "깊게") if d in sec.get("확인한 범위", "")), None)
    bad = [v for v in case.get("mustNotVerdict") or [] if v == verdict]
    if case.get("expectVerdict") and verdict != case["expectVerdict"]:
        bad.append(f"기대 {case['expectVerdict']}")
    if case.get("expectDepth") and depth != case["expectDepth"]:
        bad.append(f"깊이 기대 {case['expectDepth']}")
    return {"verdict": verdict, "depth": depth, "found": found, "missed": missed, "violations": bad,
            "sections": sorted(sec), "pass": not missed and not bad and verdict is not None}


def eval_session_text(row):
    if "resumed" not in row:
        return "-"
    if row["resumed"]:
        return "이어 쓰기"
    return "새로" + (f" ({row['freshReason']})" if row.get("freshReason") else "")


def cmd_eval_score(args):
    with open(os.path.expanduser(args.cases), encoding="utf-8") as f:
        cases = json.load(f)
    results = os.path.expanduser(args.results)
    rows = []
    for case in cases:
        if args.case and case.get("id") not in args.case:
            continue
        path = os.path.join(results, f"{case['id']}.md")
        meta_path = os.path.join(results, f"{case['id']}.json")
        meta = {}
        if os.path.exists(meta_path):
            with open(meta_path, encoding="utf-8") as f:
                meta = json.load(f)
        if not os.path.exists(path):
            rows.append({"id": case["id"], "result": None, "pass": False, "note": "결과 없음", **meta})
            continue
        with open(path, encoding="utf-8") as f:
            score = eval_score_case(case, f.read())
        rows.append({"id": case["id"], "result": path, **meta, **score})
    label = args.label or os.path.basename(results.rstrip("/"))
    head = ["문제", "통과", "판정", "깊이", "맞힘", "놓침", "위반", "비용", "시간", "세션", "끝 문맥", "스킬", "결과 원문"]
    lines = [f"# 리뷰 기준 문제 채점 · {label}", "", "| " + " | ".join(head) + " |", "|" + "---|" * len(head)]
    for r in rows:
        total = len(r.get("found", [])) + len(r.get("missed", []))
        cells = [r["id"], "O" if r.get("pass") else "X", r.get("verdict") or r.get("note") or "-", r.get("depth") or "-",
                 f"{len(r.get('found', []))}/{total}", ", ".join(r.get("missed", [])) or "-", ", ".join(r.get("violations", [])) or "-",
                 f"${r['costUSD']:.2f}" if isinstance(r.get("costUSD"), (int, float)) else "-",
                 f"{r['seconds'] / 60:.1f}분" if isinstance(r.get("seconds"), (int, float)) else "-",
                 eval_session_text(r), f"{r['contextAfter'] // 1000}k" if isinstance(r.get("contextAfter"), int) else "-",
                 ",".join(r.get("skills") or []) or "-", r.get("result") or "-"]
        lines.append("| " + " | ".join(str(c).replace("|", "/") for c in cells) + " |")
    passed = sum(1 for r in rows if r.get("pass"))
    cost = sum(r.get("costUSD") or 0 for r in rows if isinstance(r.get("costUSD"), (int, float)))
    lines += ["", f"통과 {passed}/{len(rows)} · 비용 합계 ${cost:.2f}"]
    out = os.path.expanduser(args.out or results)
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, "score.json"), "w", encoding="utf-8") as f:
        json.dump({"label": label, "rows": rows}, f, ensure_ascii=False, indent=1)
    with open(os.path.join(out, "score.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    print("\n".join(lines))
    return 0 if passed == len(rows) else 1


def cmd_gh_review(args):
    if args.event not in ("APPROVE", "COMMENT"):
        raise SystemExit("event must be APPROVE or COMMENT")
    pr = gh_api(f"repos/{args.repo}/pulls/{args.pr}")
    if pr.get("user", {}).get("login") == gh_user():
        raise SystemExit("my own PR; refusing")
    if pr.get("state") != "open" or pr.get("draft"):
        raise SystemExit("PR is not open or is draft; refusing")
    requested = {u.get("login") for u in pr.get("requested_reviewers", [])}
    if gh_user() not in requested and not args.allow_unrequested:
        raise SystemExit("I am not a requested reviewer on this PR; refusing")
    with open(os.path.expanduser(args.body_file), encoding="utf-8") as f:
        body = f.read().strip()
    payload = {"event": args.event, "body": body, "commit_id": args.commit or pr["head"]["sha"]}
    if args.comments_file:
        with open(os.path.expanduser(args.comments_file), encoding="utf-8") as f:
            payload["comments"] = json.load(f)
    if args.event == "APPROVE" and payload.get("comments"):
        raise SystemExit("APPROVE must not carry inline defect comments; use COMMENT")
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    return run_checked(["gh", "api", "-X", "POST", f"repos/{args.repo}/pulls/{args.pr}/reviews", "--input", "-"], env=gh_env(), input_bytes=data)


def cmd_write_note(args):
    name = os.path.basename(args.name)
    if not name or name.startswith("."):
        raise SystemExit("bad note name")
    os.makedirs(note_dir(), exist_ok=True)
    path = os.path.join(note_dir(), name)
    text = args.text if args.text is not None else sys.stdin.read()
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)
    print(path)
    return 0


def cmd_jira_state(args):
    state = load_json(jira_state_file(), {"processed": {}})
    client = jira()
    found = flat_issue(client.get_issue(args.key))
    entry = {
        "processedAt": now().isoformat(timespec="seconds"),
        "updated": found.get("updated"),
        "type": args.type,
        "result": args.result,
    }
    if args.waiting:
        me = client.me()
        questions = [c for c in client.comments(args.key) if is_me(c.get("author"), me) and (c.get("body") or "").startswith(QUESTION_PREFIX)]
        if not questions:
            raise SystemExit(f"{args.key}: '{QUESTION_PREFIX}' 댓글을 먼저 남겨 주세요")
        entry["waiting"] = True
        entry["questionId"] = str(questions[-1].get("id"))
    state.setdefault("processed", {})[args.key] = entry
    save_json(jira_state_file(), state)
    print("saved", args.key)
    return 0


def review_skip(url, sha=None, state=None):
    state = state if state is not None else load_json(gh_review_state_file(), {"baseline": [], "reviewed": {}})
    if url in set(state.get("baseline") or []):
        return True, "baseline"
    done = (state.get("reviewed") or {}).get(url)
    if done is None:
        return False, "new"
    if sha and done.get("sha") == sha:
        return True, "reviewed-same-sha"
    return False, "reviewed-other-sha" if sha else "reviewed-sha-unknown"


def cmd_review_state(args):
    if args.check:
        skip, reason = review_skip(args.url, args.sha)
        return print_json({"skip": skip, "reason": reason})
    if not args.sha or not args.result:
        raise SystemExit("review-state: --sha 와 --result 가 필요해요 (확인만 하려면 --check)")
    state = load_json(gh_review_state_file(), {"baseline": [], "reviewed": {}})
    state.setdefault("reviewed", {})[args.url] = {"sha": args.sha, "at": now().isoformat(timespec="seconds"), "result": args.result}
    save_json(gh_review_state_file(), state)
    print("saved", args.url)
    return 0


WORKTREE_DIR = ".jira-auto-worktrees"
TEST_TIMEOUT_SECONDS = 1200
COMMIT_MESSAGE = re.compile(r"^(feat|fix|refactor|test|docs): \S.*$")
JIRA_KEY = re.compile(r"\b[A-Z][A-Z0-9]+-\d+\b")


def worktree_base():
    return os.path.join(work_dir(), WORKTREE_DIR)


def git_out(path, *args):
    out = subprocess.run(["git", "-C", path, *args], capture_output=True, timeout=300)
    if out.returncode != 0:
        raise SystemExit(out.stderr.decode(errors="replace").strip()[:300] or f"git {' '.join(args)} failed")
    return out.stdout.decode(errors="replace").strip()


def worktree_path(path):
    base = os.path.realpath(worktree_base())
    real = os.path.realpath(os.path.expanduser(path))
    if os.path.dirname(real) != base or not os.path.isfile(os.path.join(real, ".git")):
        raise SystemExit(f"worktree not allowed: {path} (must be a worktree directly under {base})")
    return real


def feature_branch(branch):
    if not branch.startswith("feature/") or ".." in branch or branch.endswith("/") or " " in branch:
        raise SystemExit("branch must look like feature/...")
    return branch


def worktree_map_file():
    return os.path.join(state_dir(), "worktrees.json")


def checked_out_elsewhere(repo, branch):
    listing = git_out(repo, "worktree", "list", "--porcelain")
    return f"branch refs/heads/{branch}" in listing.splitlines()


def worktree_branch(path):
    current = git_out(path, "rev-parse", "--abbrev-ref", "HEAD")
    if current != "HEAD":
        return current
    return load_json(worktree_map_file(), {}).get(os.path.realpath(path), "HEAD")


def cmd_worktree_add(args):
    repo = repo_path(args.repo)
    branch = feature_branch(args.branch)
    name = branch.rstrip("/").split("/")[-1]
    os.makedirs(worktree_base(), exist_ok=True)
    target = os.path.join(worktree_base(), name)
    if os.path.exists(target):
        current = worktree_branch(worktree_path(target))
        if current != branch:
            raise SystemExit(f"{target} already holds {current}")
        print(target)
        return 0
    subprocess.run(["git", "-C", repo, "fetch", "--quiet", "origin"], check=False)
    if checked_out_elsewhere(repo, branch):
        if not git_ref_exists(repo, f"refs/remotes/origin/{branch}"):
            raise SystemExit(f"{branch} is checked out elsewhere and not on origin")
        rc = run_checked(["git", "-C", repo, "worktree", "add", "--detach", target, f"origin/{branch}"])
        if rc == 0:
            mapping = load_json(worktree_map_file(), {})
            mapping[os.path.realpath(target)] = branch
            save_json(worktree_map_file(), mapping)
    elif git_ref_exists(repo, f"refs/heads/{branch}"):
        rc = run_checked(["git", "-C", repo, "worktree", "add", target, branch])
    elif git_ref_exists(repo, f"refs/remotes/origin/{branch}"):
        rc = run_checked(["git", "-C", repo, "worktree", "add", "-b", branch, target, f"origin/{branch}"])
    else:
        raise SystemExit(f"{branch} not found; make it with create-branch first")
    if rc == 0:
        print(target)
    return rc


def detect_test_command(path):
    if os.path.exists(os.path.join(path, "gradlew")):
        return "./gradlew test"
    if os.path.exists(os.path.join(path, "mvnw")):
        return "./mvnw -q test"
    if os.path.exists(os.path.join(path, "package.json")):
        return "npm test"
    if os.path.exists(os.path.join(path, "pyproject.toml")):
        return "uv run pytest" if os.path.exists(os.path.join(path, "uv.lock")) else "pytest"
    if os.path.exists(os.path.join(path, "Package.swift")):
        return "swift test"
    if os.path.exists(os.path.join(path, "go.mod")):
        return "go test ./..."
    return None


def repo_name_of(worktree):
    common = git_out(worktree, "rev-parse", "--path-format=absolute", "--git-common-dir")
    return os.path.basename(os.path.dirname(common.rstrip("/")))


def count_results(text):
    ran = re.findall(r"^Ran (\d+) tests? in ", text, re.M)
    if ran:
        total = sum(int(n) for n in ran)
        failed = sum(int(n) for n in re.findall(r"(?:failures|errors)=(\d+)", text))
        skipped = sum(int(n) for n in re.findall(r"(?:skipped|expected failures)=(\d+)", text))
        return max(0, total - failed - skipped), failed
    gradle = re.findall(r"(\d+) tests? completed(?:, (\d+) failed)?", text)
    if gradle:
        total = sum(int(t) for t, _ in gradle)
        failed = sum(int(f or 0) for _, f in gradle)
        return total - failed, failed
    passed = sum(int(n) for n in re.findall(r"(\d+) (?:tests? )?passed", text))
    failed = sum(int(n) for n in re.findall(r"(\d+) (?:tests? )?failed", text))
    return passed, failed


def junit_counts(path, since):
    import xml.etree.ElementTree as ET
    passed = failed = 0
    found = False
    for pattern in ("**/build/test-results/**/TEST-*.xml", "**/target/surefire-reports/TEST-*.xml"):
        for file in glob.glob(os.path.join(path, pattern), recursive=True):
            if os.path.getmtime(file) < since:
                continue
            try:
                root = ET.parse(file).getroot()
            except (ET.ParseError, OSError):
                continue
            suites = [root] if root.tag == "testsuite" else root.findall("testsuite")
            for suite in suites:
                total = int(suite.get("tests", 0))
                bad = int(suite.get("failures", 0)) + int(suite.get("errors", 0))
                skipped = int(suite.get("skipped", 0))
                passed += total - bad - skipped
                failed += bad
                found = True
    return (passed, failed) if found else None


def cmd_run_tests(args):
    path = worktree_path(args.worktree)
    configured = (settings().get("testCommands") or {}).get(repo_name_of(path))
    command = configured or detect_test_command(path)
    if not command:
        raise SystemExit("테스트 명령을 찾지 못했어요. automation.testCommands 에 레포별 명령을 적어 주세요")
    started = dt.datetime.now().timestamp() - 1
    try:
        out = subprocess.run(_shell_command(command), cwd=path, capture_output=True, timeout=args.timeout)
        code, timed_out = out.returncode, False
        text = out.stdout.decode(errors="replace") + "\n" + out.stderr.decode(errors="replace")
    except subprocess.TimeoutExpired as exc:
        code, timed_out = -1, True
        text = ((exc.stdout or b"") + b"\n" + (exc.stderr or b"")).decode(errors="replace")
    passed, failed = junit_counts(path, started) or count_results(text)
    tail = "\n".join([line for line in text.splitlines() if line.strip()][-15:])
    ok = code == 0 and not timed_out
    print_json({"command": command, "ok": ok, "exitCode": code, "timedOut": timed_out, "passed": passed, "failed": failed, "tail": tail})
    return 0 if ok else 1


def push_without_new_commit(path, branch, message):
    subprocess.run(["git", "-C", path, "fetch", "--quiet", "origin"], check=False)
    remote = f"refs/remotes/origin/{branch}"
    head = git_out(path, "rev-parse", "HEAD")
    files = git_out(path, "diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD").splitlines()
    if git_ref_exists(path, remote):
        ahead = int(git_out(path, "rev-list", "--count", f"{remote}..HEAD") or "0")
        if ahead == 0 and git_out(path, "rev-parse", remote) == head and git_out(path, "log", "-1", "--format=%s") == message:
            return print_json({"branch": branch, "commit": head, "files": files, "already": True})
        if ahead > 0 and git_out(path, "log", "-1", "--format=%s") == message:
            rc = run_checked(["git", "-C", path, "push", "origin", f"HEAD:refs/heads/{branch}"])
            if rc == 0:
                print_json({"branch": branch, "commit": head, "files": files})
            return rc
    raise SystemExit("nothing to commit")


def cmd_commit_push(args):
    path = worktree_path(args.worktree)
    message = args.message.strip()
    if "\n" in message or not COMMIT_MESSAGE.match(message):
        raise SystemExit("message must be one line starting with feat:|fix:|refactor:|test:|docs:")
    if JIRA_KEY.search(message) or "co-authored-by" in message.lower():
        raise SystemExit("message must not carry a Jira key or Co-Authored-By")
    branch = feature_branch(worktree_branch(path))
    git_out(path, "add", "-A")
    if subprocess.run(["git", "-C", path, "diff", "--cached", "--quiet"]).returncode == 0:
        return push_without_new_commit(path, branch, message)
    files = git_out(path, "diff", "--cached", "--name-only").splitlines()
    rc = run_checked(["git", "-C", path, "commit", "-q", "-m", message])
    if rc != 0:
        return rc
    sha = git_out(path, "rev-parse", "HEAD")
    rc = run_checked(["git", "-C", path, "push", "origin", f"HEAD:refs/heads/{branch}"])
    if rc == 0:
        print_json({"branch": branch, "commit": sha, "files": files})
    return rc


def in_window(t, start_hour, end_hour):
    return t.weekday() < 5 and start_hour <= t.hour <= end_hour


def cmd_poll(args):
    os.environ["PATH"] = os.pathsep.join(EXTRA_PATH + [p for p in os.environ.get("PATH", "").split(os.pathsep) if p and p not in EXTRA_PATH])
    t = now()
    if not args.force and not in_window(t, 9, 19):
        return 0
    state = load_json(poll_state_file(), {})
    jobs = [("jira", "jira.md", jira_candidates), ("github", "github-review.md", github_candidates)]
    for job, prompt_file, finder in jobs:
        if args.only and job != args.only:
            continue
        js = state.setdefault(job, {})
        if pid_alive(js.get("pid")):
            print(f"{job}: running pid {js.get('pid')}, skip")
            continue
        try:
            found = finder()
        except Exception as exc:
            print(f"{job}: check failed: {exc}", file=sys.stderr)
            continue
        if not found:
            print(f"{job}: nothing new")
            js.pop("pid", None)
            continue
        signature = "|".join(sorted(found))
        attempts = js.setdefault("attempts", {})
        a = attempts.get(signature, {"count": 0, "last": 0})
        if a["count"] >= MAX_ATTEMPTS:
            if not a.get("gaveUp"):
                a["gaveUp"] = True
                attempts[signature] = a
                subprocess.run(self_command("event", "--notify", "--source", "poll", "--task", job,
                                "--event", "blocked", "--result", f"{MAX_ATTEMPTS}회 처리해도 상태가 안 바뀜, 자동 재시도 중단: {', '.join(found)[:150]}"))
            print(f"{job}: gave up on {signature}")
            continue
        if t.timestamp() - a["last"] < RETRY_GAP_SECONDS:
            print(f"{job}: waiting retry gap for {signature}")
            continue
        if args.dry_run:
            print(f"{job}: would launch claude for {found}")
            continue
        pid, log_file = launch_claude(job, prompt_file, found)
        a["count"] += 1
        a["last"] = t.timestamp()
        attempts[signature] = a
        js["pid"] = pid
        js["lastLaunch"] = t.isoformat(timespec="seconds")
        js["lastLog"] = log_file
        print(f"{job}: launched pid {pid} for {found} (log {log_file})")
    for js in state.values():
        cutoff = t.timestamp() - 86400 * 3
        js["attempts"] = {k: v for k, v in js.get("attempts", {}).items() if v.get("last", 0) >= cutoff}
    save_json(poll_state_file(), state)
    return 0


def main(argv=None):
    p = argparse.ArgumentParser(prog="pokemate auto", description="자동 처리 helper. 알람(명령 실행·트리거 쓰기 허용)과 자동 처리 프롬프트가 부른다. 하위 명령마다 --help 로 옵션을 본다.")
    sub = p.add_subparsers(dest="cmd", required=True)

    e = sub.add_parser("event", help="활동 기록 한 줄 남기기. --notify 면 Teams 카드 전송 (--dry-run 은 카드만 출력)", description="활동 기록 한 줄 남기기. --notify 면 Teams 카드 전송 (--dry-run 은 카드만 출력)")
    e.add_argument("--source", required=True, help="출처. 예: jira, github, poll, worklog")
    e.add_argument("--event", required=True, help="종류. detected, processed, approved, commented, changed, blocked, failed")
    e.add_argument("--task", help="작업 이름")
    e.add_argument("--key", help="이슈 키나 repo#번호")
    e.add_argument("--title", help="제목")
    e.add_argument("--kind", help="유형. 예: Story, Task, 리뷰")
    e.add_argument("--result", help="결과 한 줄")
    e.add_argument("--needs", help="사람 승인이 필요한 일")
    e.add_argument("--url", help="카드의 [열기] 주소 (jira 는 비우면 이슈 주소)")
    e.add_argument("--report", help="보고서 위치")
    e.add_argument("--notify", action="store_true", help="Teams 카드도 보냄")
    e.add_argument("--dry-run", action="store_true", help="보내지 않고 카드만 출력")
    e.set_defaults(func=cmd_event)

    g = sub.add_parser("github-check", help="새 리뷰 요청 PR 확인. 새로 생기면 Teams 카드", description="새 리뷰 요청 PR 확인. 새로 생기면 Teams 카드")
    g.add_argument("--dry-run", action="store_true", help="보내지 않고 카드만 출력")
    g.set_defaults(func=cmd_github_check)

    s = sub.add_parser("summary", help="하루 요약 카드 (예약 작업·Jira·GitHub)", description="하루 요약 카드 (예약 작업·Jira·GitHub)")
    s.add_argument("--date", help="날짜 YYYY-MM-DD (기본 오늘)")
    s.add_argument("--dry-run", action="store_true", help="보내지 않고 카드만 출력")
    s.set_defaults(func=cmd_summary)

    w = sub.add_parser("worklog-check", help="직전 근무일까지 이번 달 근무시간 기록이 모자란 날 확인. 있으면 Teams 카드", description="직전 근무일까지 이번 달 근무시간 기록이 모자란 날 확인. 있으면 Teams 카드")
    w.add_argument("--today", help="기준 날짜 YYYY-MM-DD (기본 오늘)")
    w.add_argument("--hours", type=float, default=8.0, help="하루 채울 시간 (기본 8)")
    w.add_argument("--dry-run", action="store_true", help="보내지 않고 카드만 출력")
    w.set_defaults(func=cmd_worklog_check)

    q = sub.add_parser("poll", help="Jira·GitHub 새 일감 확인 후 Claude 실행 (평일 9~19시에만)", description="Jira·GitHub 새 일감 확인 후 Claude 실행 (평일 9~19시에만)")
    q.add_argument("--force", action="store_true", help="근무 시간이 아니어도 확인")
    q.add_argument("--only", choices=["jira", "github"], help="한쪽만 확인")
    q.add_argument("--dry-run", action="store_true", help="Claude 를 띄우지 않고 찾은 것만 출력")
    q.set_defaults(func=cmd_poll)

    r = sub.add_parser("git-read", help="작업 폴더 바로 아래 레포에서 읽기 전용 git 명령만 실행", description="작업 폴더 바로 아래 레포에서 읽기 전용 git 명령만 실행")
    r.add_argument("--repo", required=True, help="작업 폴더 바로 아래 레포 폴더 이름")
    r.add_argument("rest", nargs=argparse.REMAINDER, help="-- 뒤에 git 하위 명령과 인자 (fetch, show, log, diff, branch -r …)")
    r.set_defaults(func=cmd_git_read)

    b = sub.add_parser("create-branch", help="feature/... 브랜치를 만들고 push (이미 있으면 안 만듦, force 없음)", description="feature/... 브랜치를 만들고 push (이미 있으면 안 만듦, force 없음)")
    b.add_argument("--repo", required=True, help="레포 폴더 이름")
    b.add_argument("--branch", required=True, help="만들 브랜치 (feature/… 만)")
    b.add_argument("--from", dest="start", required=True, help="시작 ref. 예: origin/main")
    b.set_defaults(func=cmd_create_branch)

    c = sub.add_parser("jira-comment", help="나에게 할당된 이슈에만 댓글 ([자동 작성] 으로 시작, 이슈당 최대 3건, 확인 필요는 1건)", description="나에게 할당된 이슈에만 댓글 ([자동 작성] 으로 시작, 이슈당 최대 3건, 확인 필요는 1건)")
    c.add_argument("--key", required=True, help="이슈 키")
    c.add_argument("--body-file", required=True, help="댓글 본문 파일")
    c.set_defaults(func=cmd_jira_comment)

    gg = sub.add_parser("gh-get", help="GitHub API 조회 (GET)", description="GitHub API 조회 (GET)")
    gg.add_argument("path", help="API 경로. 예: repos/owner/repo/pulls/1")
    gg.add_argument("-f", "--field", action="append", help="쿼리 값 key=value (여러 번)")
    gg.add_argument("--jq", help="jq 필터")
    gg.add_argument("--paginate", action="store_true", help="모든 쪽")
    gg.set_defaults(func=cmd_gh_get)

    gd = sub.add_parser("gh-diff", help="PR 변경 내용 보기", description="PR 변경 내용 보기")
    gd.add_argument("--repo", required=True, help="owner/repo")
    gd.add_argument("--pr", required=True, type=int, help="PR 번호")
    gd.set_defaults(func=cmd_gh_diff)

    gf = sub.add_parser("gh-file", help="PR 저장소의 파일 내용 읽기 (읽기 전용, 리뷰 중이면 그 저장소만)", description="PR 저장소의 파일 내용 읽기 (읽기 전용, 리뷰 중이면 그 저장소만)")
    gf.add_argument("--repo", required=True, help="owner/repo")
    gf.add_argument("--ref", required=True, help="커밋 sha 또는 브랜치")
    gf.add_argument("--path", required=True, help="파일 또는 폴더 경로")
    gf.set_defaults(func=cmd_gh_file)

    dn = sub.add_parser("domain-note", help="도메인 노트(노트 폴더/{도메인}/notes.md)의 종류별 절에 확인한 사실 한 줄 추가", description="도메인 노트(노트 폴더/{도메인}/notes.md)의 종류별 절에 확인한 사실 한 줄 추가. 노트 폴더는 automation 설정 domainNotesDir(기본 ~/.claude/references/notes). 도메인 폴더가 없으면 checklist.md·notes.md 빈 틀을 만든다")
    dn.add_argument("--domain", required=True, help="도메인 이름")
    dn.add_argument("--kind", choices=NOTE_KINDS, help="종류: " + "|".join(NOTE_KINDS))
    dn.add_argument("--source", help="출처 (예: sample-web#123, ACME-1)")
    dn.add_argument("--text", help="코드·문서로 확인한 사실과 근거")
    dn.add_argument("--init", action="store_true", help="쓰지 않고 도메인 폴더 빈 틀만 준비")
    dn.set_defaults(func=cmd_domain_note)

    dm = sub.add_parser("domain-map", help="도메인 지도(노트 폴더/{도메인}/maps/{주제}.md)를 본문 파일로 만들거나 갱신본으로 바꿈 (git 은 부르지 않음)", description="도메인 지도(노트 폴더/{도메인}/maps/{주제}.md)를 본문 파일로 만들거나 갱신본으로 바꾼다. 같은 주제가 있으면 통째로 바꾸고 이전 것은 노트 저장소의 Git 이 보관한다. git 은 부르지 않는다")
    dm.add_argument("--domain", required=True, help="도메인 이름")
    dm.add_argument("--topic", required=True, help="주제 (파일 이름, 슬래시·점으로 시작 금지)")
    dm.add_argument("--body-file", required=True, help="지도 본문 파일 (write-note 로 쓴 경로)")
    dm.add_argument("--source", required=True, help="출처 (예: ACME-1)")
    dm.set_defaults(func=cmd_domain_map)

    gr = sub.add_parser("gh-review", help="리뷰 등록 (APPROVE·COMMENT 만, 내 PR·draft·지정 안 된 PR 거절)", description="리뷰 등록 (APPROVE·COMMENT 만, 내 PR·draft·지정 안 된 PR 거절)")
    gr.add_argument("--repo", required=True, help="owner/repo")
    gr.add_argument("--pr", required=True, type=int, help="PR 번호")
    gr.add_argument("--event", required=True, help="APPROVE 또는 COMMENT")
    gr.add_argument("--body-file", required=True, help="리뷰 본문 파일")
    gr.add_argument("--comments-file", help="줄 댓글 JSON 배열 파일 (COMMENT 만)")
    gr.add_argument("--commit", help="리뷰할 커밋 (기본 PR head)")
    gr.add_argument("--allow-unrequested", action="store_true", help="리뷰어로 지정되지 않아도 허용")
    gr.set_defaults(func=cmd_gh_review)

    wn = sub.add_parser("write-note", help="댓글·리뷰 본문 파일 쓰기 (데이터 폴더 automation/notes). 쓴 경로를 출력", description="댓글·리뷰 본문 파일 쓰기 (데이터 폴더 automation/notes). 쓴 경로를 출력")
    wn.add_argument("--name", required=True, help="파일 이름")
    wn.add_argument("--text", help="내용 (없으면 표준 입력)")
    wn.set_defaults(func=cmd_write_note)

    js = sub.add_parser("jira-state", help="Jira 처리 결과 저장 (다시 처리하지 않게)", description="Jira 처리 결과 저장 (다시 처리하지 않게)")
    js.add_argument("--key", required=True, help="이슈 키")
    js.add_argument("--type", required=True, help="Story, Task, 기타")
    js.add_argument("--result", required=True, help="처리 결과 한 줄")
    js.add_argument("--waiting", action="store_true", help="확인 필요 댓글을 남기고 답을 기다리는 중 (내 답 댓글이 달리면 트리거가 다시 잡음)")
    js.set_defaults(func=cmd_jira_state)

    rs = sub.add_parser("review-state", help="PR 리뷰 결과 저장 (다시 리뷰하지 않게). --check 는 건너뛸지만 확인", description="PR 리뷰 결과 저장 (다시 리뷰하지 않게). --check 는 건너뛸지만 확인")
    rs.add_argument("--url", required=True, help="PR 주소")
    rs.add_argument("--sha", help="리뷰한 head sha")
    rs.add_argument("--result", help="결과 한 줄")
    rs.add_argument("--check", action="store_true", help="저장하지 않고 건너뛸지만 JSON 으로 출력 (baseline·같은 sha 로 이미 리뷰)")
    rs.set_defaults(func=cmd_review_state)

    jg = sub.add_parser("jira-get", help="이슈 한 건 조회 (본문·타입·Epic Link·담당자·연결 이슈, JSON)", description="이슈 한 건 조회 (본문·타입·Epic Link·담당자·연결 이슈, JSON)")
    jg.add_argument("--key", required=True, help="이슈 키")
    jg.set_defaults(func=cmd_jira_get)

    jq = sub.add_parser("jira-search", help="JQL 검색 (JSON)", description="JQL 검색 (JSON)")
    jq.add_argument("--jql", required=True, help="JQL")
    jq.add_argument("--max", type=int, default=50, help="최대 개수 (기본 50)")
    jq.set_defaults(func=cmd_jira_search)

    jc = sub.add_parser("jira-comments", help="이슈 댓글 전부 (JSON)", description="이슈 댓글 전부 (JSON)")
    jc.add_argument("--key", required=True, help="이슈 키")
    jc.set_defaults(func=cmd_jira_comments)

    jl = sub.add_parser("jira-links", help="연결된 이슈와 원격 링크 (JSON)", description="연결된 이슈와 원격 링크 (JSON)")
    jl.add_argument("--key", required=True, help="이슈 키")
    jl.set_defaults(func=cmd_jira_links)

    jt = sub.add_parser("jira-transition", help="나에게 할당된 이슈 상태 바꾸기 (start·done 은 설정 automation.transitions 의 상태)", description="나에게 할당된 이슈 상태 바꾸기 (start·done 은 설정 automation.transitions 의 상태)")
    jt.add_argument("--key", required=True, help="이슈 키")
    jt.add_argument("--to", required=True, choices=["start", "done"], help="start: 작업 시작 상태, done: 개발 끝난 상태")
    jt.set_defaults(func=cmd_jira_transition)

    wa = sub.add_parser("worktree-add", help="feature 브랜치를 작업 폴더/.jira-auto-worktrees/{KEY} worktree 로 꺼냄", description="feature 브랜치를 작업 폴더/.jira-auto-worktrees/{KEY} worktree 로 꺼냄")
    wa.add_argument("--repo", required=True, help="작업 폴더 바로 아래 레포 폴더 이름")
    wa.add_argument("--branch", required=True, help="create-branch 로 만든 feature/… 브랜치")
    wa.set_defaults(func=cmd_worktree_add)

    rt = sub.add_parser("run-tests", help="worktree 에서 레포의 빌드·테스트 실행, 결과 요약 JSON (실패면 종료 코드 1)", description="worktree 에서 레포의 빌드·테스트 실행, 결과 요약 JSON (실패면 종료 코드 1)")
    rt.add_argument("--worktree", required=True, help="worktree-add 가 출력한 경로")
    rt.add_argument("--timeout", type=int, default=TEST_TIMEOUT_SECONDS, help="제한 시간(초, 기본 1200)")
    rt.set_defaults(func=cmd_run_tests)

    cp = sub.add_parser("commit-push", help="worktree 의 feature 브랜치에 커밋하고 push (force 없음)", description="worktree 의 feature 브랜치에 커밋하고 push (force 없음)")
    cp.add_argument("--worktree", required=True, help="worktree-add 가 출력한 경로")
    cp.add_argument("--message", required=True, help="feat:|fix:|refactor:|test:|docs: 로 시작하는 한 줄 (Jira 키·Co-Authored-By 불가)")
    cp.set_defaults(func=cmd_commit_push)

    da = sub.add_parser("deploy-approve", help="내가 승인자인 승인 대기 배포를 승인 (내가 만든 배포는 건너뜀, 승인되면 배치가 시작됨), 결과 JSON", description="내가 승인자인 승인 대기 배포를 승인 (내가 만든 배포는 건너뜀, 승인되면 배치가 시작됨), 결과 JSON")
    da.add_argument("--id", help="이 배포 id 한 건만. 비우면 승인 대기 전부")
    da.add_argument("--dry-run", action="store_true", help="승인하지 않고 대상만 출력")
    da.set_defaults(func=cmd_deploy_approve)

    es = sub.add_parser("eval-score", help="리뷰 기준 문제 채점: 결과 문서(스킬 6절 형식)에서 꼭 나와야 할 지적·판정을 보고 표로 남김", description="리뷰 기준 문제 채점: 결과 문서(스킬 6절 형식)에서 꼭 나와야 할 지적·판정을 보고 표로 남김. 결과 폴더의 {문제 id}.md(와 실행 정보 {문제 id}.json)를 읽어 score.md·score.json 을 쓴다")
    es.add_argument("--cases", required=True, help="문제 묶음 JSON (예: local/eval/reviews.json)")
    es.add_argument("--results", required=True, help="결과 폴더 ({문제 id}.md)")
    es.add_argument("--case", action="append", help="이 문제만 (여러 번 가능)")
    es.add_argument("--label", help="표 제목에 붙일 이름 (예: 데스크톱)")
    es.add_argument("--out", help="score 파일을 쓸 폴더 (기본: 결과 폴더)")
    es.set_defaults(func=cmd_eval_score)

    args = p.parse_args(argv)
    blocked = eval_blocked(args)
    if blocked:
        raise SystemExit(blocked)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
