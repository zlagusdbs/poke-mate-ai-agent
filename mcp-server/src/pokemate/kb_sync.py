"""팀 KB 스냅샷 갱신 (`pokemate auto kb-sync`).

사내 GitHub 의 팀 KB 저장소가 스냅샷을 만든 뒤 바뀌었으면, 개인 second-brain 저장소의 스냅샷 폴더
(예: `resources/pricing/`)를 원본에 맞춰 고치고 갱신 PR 을 올린다. 설정은 `local/kb-sync.json`.

- 원본은 마지막으로 가져온 커밋(스냅샷 폴더의 `.sync.json`, 없으면 설정의 `base`) 이후 바뀐 파일만 반영한다.
  예전에 손으로 가린 파일을 다시 덮어쓰지 않기 위해서다.
- 반영하는 파일은 가린다: IP, 메일 주소, 전화번호, `PWD=`·`password:` 같은 비밀번호 값.
  자동으로 못 가렸을 수 있는 파일(예전 스냅샷에 손으로 가린 곳이 있던 파일, 비밀번호·계정 낱말이 남은 파일)은
  PR 본문에 "사람이 확인할 파일" 로 적는다. 내용은 적지 않는다.
- 브랜치는 `hotfix/{다음 버전}/kb-{이름}` (main 에서), PR 은 main 으로. 같은 KB 의 갱신 PR 이 열려 있으면
  새로 만들지 않고 그 브랜치에 이어 올린다. 머지는 사람이 한다.
"""

import datetime as dt
import json
import os
import re
import subprocess
from pathlib import Path

from .connections import local_dir
from .credentials import data_dir

KST = dt.timezone(dt.timedelta(hours=9))
SYNC_FILE = ".sync.json"
VERSION_BRANCH = re.compile(r"^(?:release|hotfix)/v(\d+)-(\d+)-(\d+)(?:/.*)?$")

IP = re.compile(r"(?<![\w.])(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}(?![\w.])")
MAIL = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")
# 줄 번호 범위(`Foo.cs:1619-1684`, `L1551-1567`)와 헷갈리지 않게 앞이 글자·`:`·`-`·`.` 이면 전화번호로 보지 않는다
PHONE = re.compile(r"(?<![\w:.\-])(?:0\d{1,2}-\d{3,4}-\d{4}|1[5-8]\d\d-\d{4})(?![\w\-])")
# 값이 변수·자리표시(`@new_pwd`, `{비밀번호}`, `$X`, `<...>`, `%X%`)면 가리지 않는다
SECRET_ASSIGN = re.compile(r"(?i)\b((?:[A-Z_]*PASS(?:WORD|WD)|PWD|UID|SECRET|API_?KEY|ACCESS_?TOKEN)(\s*)=(\s*))(?!\[가림\]|[@{$<%])([^\s;\"'`,|)<>]+)")
CODE_VALUE = re.compile(r"^[A-Za-z_][\w.]*\(?$")
SECRET_LABEL = re.compile(r"(?i)((?:password|passwd|비밀번호|패스워드)\s*[:：]\s*`?)(?!\[가림\]|[@{$<%])([^\s`|,;)]+)")
SUSPECT = re.compile(r"(?i)password|passwd|\bpwd\b|\buid\s*=|비밀번호|패스워드|secret|credential|계정|자격증명")
MASKS = [(MAIL, "[메일 가림]"), (PHONE, "[전화번호 가림]")]


def mask_ip(match):
    """루프백·0.x 와 버전 번호처럼 보이는 것(네 자리 모두 한 자리 수, 예: 1.0.0.0)은 두고 나머지 IPv4 를 가린다."""
    parts = match.group(0).split(".")
    if parts[0] in ("127", "0") or all(len(p) == 1 for p in parts):
        return match.group(0)
    return "[IP 가림]"


def now():
    return dt.datetime.now(KST)


def config_file():
    return Path(local_dir()) / "kb-sync.json"


def load_config(path=None):
    path = Path(path or config_file())
    if not path.exists():
        raise SystemExit(f"kb-sync: 설정 파일이 없어요: {path}")
    return json.loads(path.read_text(encoding="utf-8"))


def cache_dir():
    path = Path(data_dir()) / "automation" / "kb-sync"
    path.mkdir(parents=True, exist_ok=True)
    return path


# ---------- 가리기 ----------

def mask_text(text):
    """가린 글과 종류별 개수."""
    counts = {}
    masked_ip = IP.sub(mask_ip, text)
    n_ip = masked_ip.count("[IP 가림]") - text.count("[IP 가림]")
    text = masked_ip
    if n_ip:
        counts["[IP 가림]"] = n_ip
    for pattern, label in MASKS:
        text, n = pattern.subn(label, text)
        if n:
            counts[label] = counts.get(label, 0) + n
    n_total = 0

    def assign(m):
        nonlocal n_total
        # `Password = TestSeller.Password` 처럼 = 양쪽이 띄어 있고 값이 코드 이름이면 코드라서 둔다. 연결 문자열(`PWD=x;`)은 붙어 있다
        if (m.group(2) or m.group(3)) and CODE_VALUE.match(m.group(4)):
            return m.group(0)
        n_total += 1
        return m.group(1) + "[가림]"

    text = SECRET_ASSIGN.sub(assign, text)
    text, n = SECRET_LABEL.subn(lambda m: m.group(1) + "[가림]", text)
    n_total += n
    if n_total:
        counts["[가림]"] = n_total
    return text, counts


def suspect_lines(text):
    """가린 뒤에도 비밀번호·계정 낱말이 남은 줄 번호. 가린 줄도 센다(같은 줄의 계정 ID 등은 못 가렸을 수 있다)."""
    return [i for i, line in enumerate(text.splitlines(), 1) if SUSPECT.search(line)]


# ---------- 원본 파일 고르기 ----------

def selected(path, source):
    """원본 저장소 경로를 스냅샷에 넣을지."""
    def under(p):
        return p == "" or path == p.rstrip("/") or path.startswith(p if p.endswith("/") else p + "/")

    if not any(under(p) for p in source.get("paths") or [""]):
        return False
    for ex in source.get("exclude") or []:
        if path == ex.rstrip("/") or (ex.endswith("/") and path.startswith(ex)):
            return False
    parts = path.split("/")
    if set(parts[:-1]) & set(source.get("excludeDirs") or []):
        return False
    exts = source.get("onlyExt")
    if exts and not any(path.lower().endswith(e.lower()) for e in exts):
        return False
    return True


def dest_path(path, source):
    into = (source.get("into") or "").strip("/")
    return f"{into}/{path}" if into else path


# ---------- git·gh ----------

def git(cwd, *args, check=True, input_bytes=None):
    cmd = ["git", "-c", "credential.helper=", "-c", "credential.helper=!gh auth git-credential", "-c", "core.autocrlf=false", "-c", "core.quotepath=off", "-c", "core.longpaths=true"]
    out = subprocess.run([*cmd, *args], cwd=cwd, capture_output=True, input=input_bytes, timeout=600)
    if check and out.returncode != 0:
        raise RuntimeError(f"git {' '.join(args[:3])}: {out.stderr.decode(errors='replace').strip()[:400]}")
    return out


def git_text(cwd, *args):
    return git(cwd, *args).stdout.decode("utf-8", errors="replace").strip()


def gh(host, *args, check=True):
    env = {**os.environ, "GH_HOST": host}
    out = subprocess.run(["gh", *args], capture_output=True, env=env, timeout=300)
    if check and out.returncode != 0:
        raise RuntimeError(f"gh {' '.join(args[:2])}: {out.stderr.decode(errors='replace').strip()[:400]}")
    return out.stdout.decode("utf-8", errors="replace")


def ensure_clone(host, repo, folder, blobless=False):
    """캐시 폴더에 저장소를 두고 최신으로 가져온다. 기본 브랜치의 원격 ref 를 돌려준다."""
    folder = Path(folder)
    url = f"https://{host}/{repo}.git"
    if not (folder / ".git").exists() and not (folder / "HEAD").exists():
        folder.parent.mkdir(parents=True, exist_ok=True)
        extra = ["--filter=blob:none", "--no-checkout"] if blobless else []
        git(folder.parent, "clone", "--quiet", *extra, url, folder.name)
    git(folder, "fetch", "--quiet", "--prune", "origin")
    git(folder, "remote", "set-head", "origin", "--auto", check=False)
    head = git(folder, "symbolic-ref", "--short", "refs/remotes/origin/HEAD", check=False).stdout.decode().strip()
    return head or "origin/main"


# ---------- 버전·브랜치 ----------

def parse_version(branch):
    m = VERSION_BRANCH.match(branch or "")
    return tuple(int(x) for x in m.groups()) if m else None


def next_hotfix(merged_heads, taken_heads):
    """main 에 들어간 마지막 버전의 minor+1. 이미 쓰인(열린 브랜치) 버전은 건너뛴다."""
    versions = [v for v in (parse_version(b) for b in merged_heads) if v]
    major, middle, minor = max(versions) if versions else (1, 0, 0)
    taken = {v for v in (parse_version(b) for b in taken_heads) if v}
    minor += 1
    while (major, middle, minor) in taken:
        minor += 1
    return f"v{major}-{middle}-{minor}"


def sync_branch_pattern(name):
    return re.compile(rf"^hotfix/v\d+-\d+-\d+/kb-{re.escape(name)}$")


# ---------- 반영 ----------

def read_sync(dest_dir):
    path = Path(dest_dir) / SYNC_FILE
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def apply_source(source, src_repo, src_ref, base, dest_dir, keep=()):
    """원본 base..src_ref 에서 바뀐 파일을 스냅샷 폴더에 반영한다. 반영 결과 dict."""
    dest_dir = Path(dest_dir)
    head = git_text(src_repo, "rev-parse", src_ref)
    result = {"name": source["name"], "repo": source["repo"], "from": base, "to": head,
              "added": [], "modified": [], "deleted": [], "masked": {}, "check": []}
    if base and git(src_repo, "cat-file", "-e", f"{base}^{{commit}}", check=False).returncode == 0:
        if git_text(src_repo, "rev-parse", base) == head:
            return result
        raw = git(src_repo, "diff", "--no-renames", "--name-status", "-z", base, head).stdout.decode("utf-8", errors="replace")
        fields = [f for f in raw.split("\0") if f]
        changes = list(zip(fields[0::2], fields[1::2]))
    else:
        raw = git(src_repo, "ls-tree", "-r", "-z", "--name-only", head).stdout.decode("utf-8", errors="replace")
        changes = [("A", p) for p in raw.split("\0") if p]
    keep = set(keep)
    for status, path in changes:
        if not selected(path, source):
            continue
        rel = dest_path(path, source)
        if rel in keep:
            continue
        target = dest_dir / rel
        if status.startswith("D"):
            if target.exists():
                target.unlink()
                result["deleted"].append(rel)
            continue
        blob = git(src_repo, "show", f"{head}:{path}").stdout
        previous = target.read_bytes() if target.exists() else None
        try:
            text = blob.decode("utf-8")
        except UnicodeDecodeError:
            text = None
        if text is not None:
            masked, counts = mask_text(text)
            for k, v in counts.items():
                result["masked"][k] = result["masked"].get(k, 0) + v
            hand_masked = previous is not None and b"[\xea\xb0\x80\xeb\xa6\xbc]" in previous  # "[가림]"
            lines = suspect_lines(masked)
            if hand_masked or lines:
                why = "예전 스냅샷에 손으로 가린 곳이 있던 파일" if hand_masked else f"비밀번호·계정 낱말이 남은 줄 {len(lines)}개"
                result["check"].append({"path": rel, "why": why})
            data = masked.encode("utf-8")
        else:
            data = blob
        if previous == data:
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        result["modified" if previous is not None else "added"].append(rel)
    return result


def has_changes(result):
    return bool(result["added"] or result["modified"] or result["deleted"])


def pr_body(kb, dest, results, today):
    lines = [f"## 무엇을 바꿨나",
             f"사내 팀 KB 원본이 스냅샷을 만든 뒤 바뀌어서 `{dest}/` 를 원본에 맞췄다. PokeMate 자동 리뷰가 `kb-sync` 로 올린 PR 이다.", "",
             "## 어떻게 동작하나",
             f"- 원본마다 마지막으로 가져온 커밋(`{dest}/{SYNC_FILE}`) 이후 바뀐 파일만 반영했다. 예전에 손으로 가린 파일은 바뀌지 않았으면 그대로 둔다.",
             "- 반영한 파일은 자동으로 가렸다: IP, 메일 주소, 전화번호, `PWD=`·`password:` 형식의 비밀번호 값.",
             f"- `{dest}/README.md` 는 고치지 않았다. 기준 커밋은 `{SYNC_FILE}` 에 남겼다.", "",
             "## 바뀐 파일"]
    check = []
    for r in results:
        if not has_changes(r):
            continue
        lines.append(f"- {r['name']} `{r['from'][:7] if r['from'] else '처음'}` → `{r['to'][:7]}`: 추가 {len(r['added'])} · 수정 {len(r['modified'])} · 삭제 {len(r['deleted'])}")
        for label, paths in (("추가", r["added"]), ("수정", r["modified"]), ("삭제", r["deleted"])):
            for p in paths[:30]:
                lines.append(f"  - {label} `{dest}/{p}`")
            if len(paths) > 30:
                lines.append(f"  - {label} 외 {len(paths) - 30}개")
        if r["masked"]:
            lines.append("  - 가림: " + ", ".join(f"{k} {v}" for k, v in sorted(r["masked"].items())))
        check += r["check"]
    lines += ["", "## 확인 결과", "- 원본 diff 기준으로 파일을 반영하고 자동 가리기를 거쳤다. 빌드·테스트 대상은 없다(문서 스냅샷)."]
    if check:
        lines.append("- **머지 전에 사람이 확인할 파일** (자동 가리기가 못 잡았을 수 있음):")
        for c in check[:50]:
            lines.append(f"  - `{dest}/{c['path']}` — {c['why']}")
        if len(check) > 50:
            lines.append(f"  - 외 {len(check) - 50}개")
    else:
        lines.append("- 자동 가리기 밖에서 따로 확인할 파일은 없었다. 그래도 머지 전에 diff 를 한 번 훑어본다.")
    lines += ["", f"main 에 머지한 뒤에는 진행 중인 release base 에도 main 을 합친다. ({today})"]
    return "\n".join(lines)


def run(name, config, dry_run=False):
    kbs = config.get("kbs") or {}
    if name not in kbs:
        return {"kb": name, "status": "unknown-kb"}
    kb = kbs[name]
    target = config["target"]
    host, repo, base_branch = target.get("host", "github.com"), target["repo"], target.get("base", "main")
    dest = kb["dest"].strip("/")
    cache = cache_dir()
    work = cache / "target" / repo.replace("/", "__")
    ensure_clone(host, repo, work)

    open_prs = json.loads(gh(host, "pr", "list", "-R", repo, "--state", "open", "--json", "number,url,headRefName", "--limit", "100") or "[]")
    pattern = sync_branch_pattern(name)
    existing = next((p for p in open_prs if pattern.match(p["headRefName"])), None)
    if existing:
        branch = existing["headRefName"]
        git(work, "checkout", "--quiet", "--force", "-B", branch, f"origin/{branch}")
    else:
        merged = json.loads(gh(host, "pr", "list", "-R", repo, "--state", "merged", "--base", base_branch, "--json", "headRefName", "--limit", "200") or "[]")
        remote = git_text(work, "for-each-ref", "--format=%(refname:lstrip=3)", "refs/remotes/origin").splitlines()
        version = next_hotfix([p["headRefName"] for p in merged], remote + [p["headRefName"] for p in open_prs])
        branch = f"hotfix/{version}/kb-{name}"
        git(work, "checkout", "--quiet", "--force", "-B", branch, f"origin/{base_branch}")
    git(work, "clean", "-fdq")

    dest_dir = work / dest
    synced = read_sync(dest_dir)
    results = []
    for source in kb["sources"]:
        src_host = source.get("host", "github.com")
        src_dir = cache / "sources" / src_host / source["repo"].replace("/", "__")
        ref = ensure_clone(src_host, source["repo"], src_dir, blobless=True)
        base = (synced.get("sources") or {}).get(source["name"], {}).get("commit") or source.get("base")
        results.append(apply_source(source, src_dir, ref, base, dest_dir, keep=kb.get("keep") or ["README.md", SYNC_FILE]))

    changed = [r for r in results if has_changes(r)]
    summary = {"kb": name, "branch": branch, "sources": [{"name": r["name"], "from": (r["from"] or "")[:7], "to": r["to"][:7],
                                                         "files": len(r["added"]) + len(r["modified"]) + len(r["deleted"]), "check": len(r["check"])} for r in results]}
    if not changed:
        git(work, "checkout", "--quiet", "--force", f"origin/{base_branch}")
        return {**summary, "status": "up-to-date", "pr": existing["url"] if existing else None}

    today = now().strftime("%Y-%m-%d")
    sync = {"syncedAt": today, "sources": {r["name"]: {"repo": r["repo"], "commit": r["to"]} for r in results}}
    dest_dir.mkdir(parents=True, exist_ok=True)
    (dest_dir / SYNC_FILE).write_text(json.dumps(sync, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    body = pr_body(name, dest, results, today)
    if dry_run:
        status = git_text(work, "status", "--porcelain")
        git(work, "checkout", "--quiet", "--force", f"origin/{base_branch}")
        git(work, "clean", "-fdq")
        return {**summary, "status": "dry-run", "changes": len(status.splitlines()), "body": body}

    git(work, "add", "-A", "--", dest)
    git(work, "commit", "--quiet", "-m", f"docs: refresh {name} KB snapshot")
    git(work, "push", "--quiet", "-u", "origin", branch)
    if existing:
        gh(host, "pr", "comment", existing["url"], "--body", body)
        url, status = existing["url"], "updated"
    else:
        version = branch.split("/")[1]
        url = gh(host, "pr", "create", "-R", repo, "--base", base_branch, "--head", branch,
                 "--title", f"docs: {version} {name} KB 스냅샷 갱신", "--body", body, "--assignee", "@me").strip().splitlines()[-1]
        status = "created"
    git(work, "checkout", "--quiet", "--force", f"origin/{base_branch}")
    return {**summary, "status": status, "pr": url, "check": sum(len(r["check"]) for r in results)}


def cmd_kb_sync(args):
    config = load_config(args.config)
    names = []
    if args.kb:
        names = [args.kb]
    elif args.domain:
        mapped = (config.get("domains") or {}).get(args.domain)
        if not mapped:
            print(json.dumps({"domain": args.domain, "status": "no-kb", "note": "이 도메인은 갱신할 팀 KB 가 없어요"}, ensure_ascii=False))
            return 0
        names = mapped if isinstance(mapped, list) else [mapped]
    else:
        names = list((config.get("kbs") or {}).keys())
    failed = False
    for name in names:
        try:
            out = run(name, config, dry_run=args.dry_run)
        except (RuntimeError, OSError, subprocess.TimeoutExpired) as e:
            out, failed = {"kb": name, "status": "failed", "error": str(e)[:400]}, True
        print(json.dumps(out, ensure_ascii=False))
    return 1 if failed else 0
