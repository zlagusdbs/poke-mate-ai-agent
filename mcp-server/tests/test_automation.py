import json
import os
import subprocess
from pathlib import Path

import pytest

from pokemate import automation, cli


class FakeRun:
    def __init__(self):
        self.calls = []
        self.handlers = []

    def on(self, match, stdout=b"", returncode=0, stderr=b""):
        self.handlers.append((match, stdout, returncode, stderr))

    def __call__(self, cmd, **kw):
        self.calls.append((list(cmd), kw))
        for match, stdout, rc, err in self.handlers:
            if match(list(cmd)):
                out = stdout(cmd, kw) if callable(stdout) else stdout
                return subprocess.CompletedProcess(cmd, rc, out, err)
        return subprocess.CompletedProcess(cmd, 0, b"", b"")


@pytest.fixture
def env(tmp_path, monkeypatch):
    shared = tmp_path / "shared"
    shared.mkdir()
    (shared / "connections.local.json").write_text(json.dumps([
        {"id": "jira", "name": "Jira", "group": "ad", "enabled": True, "baseUrl": "https://jira.example.com"},
        {"id": "github", "name": "GitHub", "group": "github", "enabled": True, "baseUrl": "https://git.example.com", "auth": "gh"},
        {"kind": "settings", "id": "automation", "githubUser": "me", "workDir": str(tmp_path / "ws"),
         "worklogCalendarUrl": "https://cal.example.com", "worklogCalendarTitle": "캘린더", "recurringTitles": ["회의"],
         "transitions": {"start": "In Progress", "done": "Review"}},
    ]), encoding="utf-8")
    monkeypatch.setenv("POKEMATE_SHARED_DIR", str(shared))
    (tmp_path / "ws").mkdir()
    fake = FakeRun()
    monkeypatch.setattr(automation.subprocess, "run", fake)
    data = Path(str(automation.data_dir()))
    return fake, data, tmp_path


def run(*args):
    return automation.main(list(args))


class FakeJira:
    base_url = "https://jira.example.com"

    def __init__(self):
        self.issues = {}
        self.comment_list = {}
        self.posted = []
        self.moved = []
        self.searched = []
        self.logs = {}
        self.me_info = {"name": "me", "key": "me", "displayName": "Me"}
        self.trans = [{"id": "11", "name": "Start", "to": {"name": "In Progress"}}, {"id": "21", "name": "Send", "to": {"name": "Review"}}]

    def add(self, key, summary="t", status="Open", assignee="me", kind="Story", updated="2026-11-17T10:00:00.000+0900"):
        self.issues[key] = {"key": key, "fields": {"summary": summary, "status": {"name": status}, "issuetype": {"name": kind},
                                                   "assignee": {"name": assignee, "key": assignee}, "updated": updated,
                                                   "customfield_1": "EPIC-1", "issuelinks": []}}

    def me(self):
        return self.me_info

    def field_id(self, name):
        return "customfield_1"

    def get_issue(self, key):
        return self.issues[key]

    def search_raw(self, jql, max_results=50, fields="*navigable"):
        self.searched.append(jql)
        return list(self.issues.values())

    def worklogs(self, key):
        return self.logs.get(key, [])

    def comments(self, key):
        return self.comment_list.get(key, [])

    def add_comment(self, key, body):
        self.posted.append((key, body))
        self.comment_list.setdefault(key, []).append({"id": str(len(self.posted)), "author": self.me_info, "body": body})
        return {"id": str(len(self.posted))}

    def remote_links(self, key):
        return [{"object": {"title": "PR", "url": "https://git.example.com/pr/1"}, "relationship": "links to"}]

    def transitions(self, key):
        return self.trans

    def transition(self, key, transition_id):
        self.moved.append((key, transition_id))
        target = next(t["to"]["name"] for t in self.trans if t["id"] == transition_id)
        self.issues[key]["fields"]["status"] = {"name": target}


@pytest.fixture
def fake_jira(env, monkeypatch):
    jira = FakeJira()
    monkeypatch.setattr(automation, "jira", lambda: jira)
    return jira


def test_settings_come_from_connections_file(env):
    fake, data, tmp = env
    assert automation.gh_host() == "git.example.com"
    assert automation.jira_browse() == "https://jira.example.com/browse/"
    assert automation.gh_user() == "me"
    assert automation.automation_dir() == str(data / "automation")
    from pokemate.connections import load_systems
    assert [s.id for s in load_systems()] == ["jira", "github"]


def test_cli_dispatches_auto(env, capsys):
    assert cli.main(["auto", "write-note", "--name", "a.md", "--text", "hi"]) == 0
    assert capsys.readouterr().out.strip().endswith("automation/notes/a.md")


def test_event_logs_and_dry_run_card(env, capsys):
    fake, data, _ = env
    assert run("event", "--source", "jira", "--event", "processed", "--key", "ABC-1", "--title", "t", "--notify", "--dry-run") == 0
    out = capsys.readouterr().out
    assert "https://jira.example.com/browse/ABC-1" in out
    log = list((data / "automation" / "activity").glob("*.jsonl"))
    assert len(log) == 1 and '"key": "ABC-1"' in log[0].read_text(encoding="utf-8")
    assert fake.calls == []


def test_event_posts_to_webhook_file(env, capsys):
    fake, data, _ = env
    (data / "teams-webhook-url").write_text("https://hook.example.com/x\n", encoding="utf-8")
    fake.on(lambda c: c[0] == "curl", b"202")
    assert run("event", "--source", "poll", "--event", "failed", "--notify") == 0
    cmd, kw = fake.calls[-1]
    assert cmd[-1] == "https://hook.example.com/x" and b"AdaptiveCard" in kw["input"]
    assert "teams post ok" in capsys.readouterr().out


def test_github_check_baseline_then_new(env, capsys):
    fake, data, _ = env
    items = [{"html_url": "https://git.example.com/o/r/pull/1", "repository_url": "https://git.example.com/api/v3/repos/o/r",
              "number": 1, "title": "first", "user": {"login": "x"}}]
    fake.on(lambda c: c[:2] == ["gh", "api"], lambda c, kw: json.dumps({"items": items}).encode())
    assert run("github-check", "--dry-run") == 0
    assert "baseline saved" in capsys.readouterr().out
    items.append({**items[0], "html_url": "https://git.example.com/o/r/pull/2", "number": 2, "title": "second"})
    assert run("github-check", "--dry-run") == 0
    out = capsys.readouterr().out
    assert "리뷰 요청 1건" in out and "o/r#2" in out
    assert fake.calls[0][1]["env"]["GH_HOST"] == "git.example.com"
    assert "review-requested:me" in " ".join(fake.calls[0][0])


def test_summary_dry_run(env, capsys, monkeypatch):
    fake, data, _ = env
    monkeypatch.setattr(automation, "SCHEDULE_GLOB", str(data / "none" / "*.json"))
    automation.append_event({"source": "jira", "event": "processed", "key": "ABC-2", "title": "t", "result": "ok"})
    fake.on(lambda c: c[:2] == ["gh", "api"], b'{"items": [], "total_count": 0}')
    assert run("summary", "--dry-run") == 0
    out = capsys.readouterr().out
    assert "하루 요약" in out and "ABC-2" in out and "git.example.com/pulls/review-requested" in out


def test_worklog_check_dry_run_uses_jira_client(env, fake_jira, capsys):
    fake, data, _ = env
    fake.on(lambda c: c[0] == "curl", b"[]")
    fake_jira.add("W-1")
    fake_jira.logs["W-1"] = [{"author": {"name": "me"}, "started": "2026-11-16T09:00", "timeSpentSeconds": 3600},
                             {"author": {"name": "other"}, "started": "2026-11-16T09:00", "timeSpentSeconds": 9999}]
    assert run("worklog-check", "--today", "2026-11-17", "--dry-run") == 0
    out = capsys.readouterr().out
    first = json.loads(out.splitlines()[0])
    assert first["target"] == "2026-11-16" and ["2026-11-16", 3600] in first["short"]
    assert "https://cal.example.com" in out and "캘린더" in out
    assert "worklogAuthor = currentUser()" in fake_jira.searched[0]


def test_poll_dry_run_finds_candidates(env, fake_jira, capsys):
    fake, data, _ = env
    fake_jira.add("J-1", "기능")
    fake_jira.add("J-2", "회의 기록")
    fake.on(lambda c: c[:2] == ["gh", "api"], b'{"items": []}')
    assert run("poll", "--force", "--dry-run") == 0
    out = capsys.readouterr().out
    assert "jira: would launch claude for ['J-1:new']" in out and "github: nothing new" in out


def test_git_read_allows_only_reads(env, capsys):
    fake, data, tmp = env
    (tmp / "ws" / "repo" / ".git").mkdir(parents=True)
    assert run("git-read", "--repo", "repo", "--", "log", "-1") == 0
    assert fake.calls[-1][0][:4] == ["git", "-C", str((tmp / "ws" / "repo").resolve()), "log"]
    with pytest.raises(SystemExit):
        run("git-read", "--repo", "repo", "--", "push")
    with pytest.raises(SystemExit):
        run("git-read", "--repo", "../etc", "--", "log")


def test_create_branch_rules(env, capsys):
    fake, data, tmp = env
    (tmp / "ws" / "repo" / ".git").mkdir(parents=True)
    with pytest.raises(SystemExit):
        run("create-branch", "--repo", "repo", "--branch", "main", "--from", "origin/main")
    fake.on(lambda c: "rev-parse" in c and "refs/remotes/origin/feature/X-1" in c, b"", 0)
    assert run("create-branch", "--repo", "repo", "--branch", "feature/X-1", "--from", "origin/main") == 0
    assert "exists" in capsys.readouterr().out
    assert not any("push" in c[0] for c in fake.calls)


def test_jira_comment_only_mine_prefixed_three_and_one_question(env, fake_jira, tmp_path):
    fake_jira.add("J-1", assignee="other")
    fake_jira.add("J-2")
    plan = tmp_path / "plan.md"
    plan.write_text("[자동 작성] 처리 계획\n- a", encoding="utf-8")
    question = tmp_path / "q.md"
    question.write_text("[자동 작성] 확인 필요\n1. ?", encoding="utf-8")
    with pytest.raises(SystemExit):
        run("jira-comment", "--key", "J-1", "--body-file", str(plan))
    plain = tmp_path / "p.md"
    plain.write_text("hello", encoding="utf-8")
    with pytest.raises(SystemExit):
        run("jira-comment", "--key", "J-2", "--body-file", str(plain))
    assert run("jira-comment", "--key", "J-2", "--body-file", str(plan)) == 0
    assert run("jira-comment", "--key", "J-2", "--body-file", str(question)) == 0
    assert run("jira-comment", "--key", "J-2", "--body-file", str(question)) == 0
    assert run("jira-comment", "--key", "J-2", "--body-file", str(plan)) == 0
    assert len(fake_jira.posted) == 2
    result = tmp_path / "r.md"
    result.write_text("[자동 작성] 처리 결과\n- done", encoding="utf-8")
    stop = tmp_path / "s.md"
    stop.write_text("[자동 작성] 개발 중단: x", encoding="utf-8")
    other = tmp_path / "o.md"
    other.write_text("[자동 작성] 메모", encoding="utf-8")
    assert run("jira-comment", "--key", "J-2", "--body-file", str(result)) == 0
    assert run("jira-comment", "--key", "J-2", "--body-file", str(stop)) == 0
    with pytest.raises(SystemExit):
        run("jira-comment", "--key", "J-2", "--body-file", str(other))
    assert len(fake_jira.posted) == 3 and all(k == "J-2" for k, _ in fake_jira.posted)


def test_jira_state_waiting_and_answer_event(env, fake_jira, tmp_path):
    fake, data, _ = env
    fake_jira.add("J-3")
    with pytest.raises(SystemExit):
        run("jira-state", "--key", "J-3", "--type", "Story", "--result", "확인 필요", "--waiting")
    fake_jira.comment_list["J-3"] = [
        {"id": "10", "author": {"name": "me"}, "body": "[자동 작성] 처리 계획"},
        {"id": "11", "author": {"name": "me"}, "body": "[자동 작성] 확인 필요\n1. A 인가 B 인가"},
    ]
    assert run("jira-state", "--key", "J-3", "--type", "Story", "--result", "확인 필요", "--waiting") == 0
    state = json.loads((data / "automation" / "state" / "jira-state.json").read_text(encoding="utf-8"))
    assert state["processed"]["J-3"]["waiting"] is True and state["processed"]["J-3"]["questionId"] == "11"
    from pokemate import events
    assert events._waiting_keys() == {"J-3": "11"}
    assert events._answers(fake_jira, "J-3", "11", "u") == []
    fake_jira.comment_list["J-3"] += [{"id": "12", "author": {"name": "other"}, "body": "남의 말"},
                                      {"id": "13", "author": {"name": "me"}, "body": "1번은 A 로 해 주세요"}]
    found = events._answers(fake_jira, "J-3", "11", "u")
    assert [i.key for i in found] == ["J-3:answer:13"] and "A 로" in found[0].summary
    assert run("jira-state", "--key", "J-3", "--type", "Story", "--result", "완료") == 0
    assert events._waiting_keys() == {}


def test_jira_transition_rules(env, fake_jira, capsys):
    fake_jira.add("J-1", status="Open")
    fake_jira.add("J-2", assignee="other")
    with pytest.raises(SystemExit):
        run("jira-transition", "--key", "J-2", "--to", "start")
    assert run("jira-transition", "--key", "J-1", "--to", "start") == 0
    assert fake_jira.moved == [("J-1", "11")]
    assert run("jira-transition", "--key", "J-1", "--to", "start") == 0
    assert fake_jira.moved == [("J-1", "11")] and "already" in capsys.readouterr().out
    fake_jira.trans = [t for t in fake_jira.trans if t["id"] != "21"]
    with pytest.raises(SystemExit):
        run("jira-transition", "--key", "J-1", "--to", "done")


def test_jira_read_helpers(env, fake_jira, capsys):
    fake_jira.add("J-1", "제목", kind="Story")
    fake_jira.comment_list["J-1"] = [{"id": "1", "author": {"name": "x"}, "created": "c", "body": "hi"}]
    assert run("jira-get", "--key", "J-1") == 0
    got = json.loads(capsys.readouterr().out)
    assert got["type"] == "Story" and got["epicLink"] == "EPIC-1" and got["assignee"]["name"] == "me" and got["url"].endswith("/browse/J-1")
    assert run("jira-search", "--jql", "assignee = currentUser()") == 0
    assert json.loads(capsys.readouterr().out)[0]["key"] == "J-1"
    assert run("jira-comments", "--key", "J-1") == 0
    assert json.loads(capsys.readouterr().out)[0]["body"] == "hi"
    assert run("jira-links", "--key", "J-1") == 0
    assert json.loads(capsys.readouterr().out)["remoteLinks"][0]["url"] == "https://git.example.com/pr/1"


def test_gh_get_and_diff(env):
    fake, data, _ = env
    assert run("gh-get", "repos/o/r/pulls/1", "-f", "a=b", "--jq", ".x") == 0
    cmd, kw = fake.calls[-1]
    assert cmd == ["gh", "api", "-X", "GET", "repos/o/r/pulls/1", "-f", "a=b", "--jq", ".x"] and kw["env"]["GH_HOST"] == "git.example.com"
    assert run("gh-diff", "--repo", "o/r", "--pr", "3") == 0
    assert fake.calls[-1][0] == ["gh", "pr", "diff", "3", "-R", "o/r"]


def test_gh_review_guards(env, tmp_path):
    fake, data, _ = env
    body = tmp_path / "b.md"
    body.write_text("LGTM", encoding="utf-8")
    pr = {"user": {"login": "me"}, "state": "open", "draft": False, "requested_reviewers": [{"login": "me"}], "head": {"sha": "abc"}}
    fake.on(lambda c: c[:4] == ["gh", "api", "-X", "GET"], lambda c, kw: json.dumps(pr).encode())
    with pytest.raises(SystemExit):
        run("gh-review", "--repo", "o/r", "--pr", "1", "--event", "APPROVE", "--body-file", str(body))
    pr["user"] = {"login": "someone"}
    with pytest.raises(SystemExit):
        run("gh-review", "--repo", "o/r", "--pr", "1", "--event", "REQUEST_CHANGES", "--body-file", str(body))
    assert run("gh-review", "--repo", "o/r", "--pr", "1", "--event", "APPROVE", "--body-file", str(body)) == 0
    cmd, kw = fake.calls[-1]
    assert cmd[:4] == ["gh", "api", "-X", "POST"] and json.loads(kw["input"])["commit_id"] == "abc"


def test_write_note_rejects_bad_name(env, capsys):
    fake, data, _ = env
    with pytest.raises(SystemExit):
        run("write-note", "--name", ".hidden", "--text", "x")
    assert run("write-note", "--name", "../../x.md", "--text", "body") == 0
    assert (data / "automation" / "notes" / "x.md").read_text(encoding="utf-8") == "body"


def test_jira_and_review_state(env, fake_jira):
    fake, data, _ = env
    fake_jira.add("J-1", updated="2026-11-17T10:00")
    assert run("jira-state", "--key", "J-1", "--type", "Story", "--result", "branch") == 0
    state = json.loads((data / "automation" / "state" / "jira-state.json").read_text(encoding="utf-8"))
    assert state["processed"]["J-1"]["updated"] == "2026-11-17T10:00"
    assert run("review-state", "--url", "u", "--sha", "s", "--result", "ok") == 0
    review = json.loads((data / "automation" / "state" / "github-review-state.json").read_text(encoding="utf-8"))
    assert review["reviewed"]["u"]["sha"] == "s"


def test_module_has_no_company_values():
    source = Path(automation.__file__).read_text(encoding="utf-8").lower()
    for word in ("swmg", "tekramg", "allahlav", "mikuynuyh", "strela_mik", "strela-mik", "oesibmik"):
        assert word[::-1] not in source


@pytest.fixture
def git_env(tmp_path, monkeypatch):
    shared = tmp_path / "shared"
    shared.mkdir()
    ws = tmp_path / "ws"
    ws.mkdir()
    (shared / "connections.local.json").write_text(json.dumps([
        {"kind": "settings", "id": "automation", "workDir": str(ws), "testCommands": {"app": "echo '3 passed'"}},
    ]), encoding="utf-8")
    monkeypatch.setenv("POKEMATE_SHARED_DIR", str(shared))
    gitconfig = tmp_path / "gitconfig"
    gitconfig.write_text("[init]\n\tdefaultBranch = main\n", encoding="utf-8")
    for k, v in {"GIT_CONFIG_GLOBAL": str(gitconfig), "GIT_CONFIG_NOSYSTEM": "1", "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@example.com",
                 "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@example.com"}.items():
        monkeypatch.setenv(k, v)
    origin = tmp_path / "origin.git"
    subprocess.run(["git", "init", "-q", "--bare", str(origin)], check=True)
    repo = ws / "app"
    subprocess.run(["git", "clone", "-q", str(origin), str(repo)], check=True)
    (repo / "a.txt").write_text("one\n", encoding="utf-8")
    subprocess.run(["git", "-C", str(repo), "add", "."], check=True)
    subprocess.run(["git", "-C", str(repo), "commit", "-q", "-m", "init"], check=True)
    subprocess.run(["git", "-C", str(repo), "push", "-q", "origin", "HEAD:refs/heads/main"], check=True)
    return ws, repo, origin


def remote_branches(origin):
    out = subprocess.run(["git", "--git-dir", str(origin), "branch", "--format=%(refname:short)"], capture_output=True, check=True)
    return out.stdout.decode().split()


def test_worktree_tests_commit_push_end_to_end(git_env, capsys):
    ws, repo, origin = git_env
    assert run("create-branch", "--repo", "app", "--branch", "feature/EPIC-1/ABC-7", "--from", "origin/main") == 0
    capsys.readouterr()
    assert run("worktree-add", "--repo", "app", "--branch", "feature/EPIC-1/ABC-7") == 0
    wt = capsys.readouterr().out.strip().splitlines()[-1]
    assert wt == str((ws / ".jira-auto-worktrees" / "ABC-7").resolve()) or wt.endswith("/.jira-auto-worktrees/ABC-7")
    assert run("worktree-add", "--repo", "app", "--branch", "feature/EPIC-1/ABC-7") == 0
    capsys.readouterr()
    assert run("run-tests", "--worktree", wt) == 0
    result = json.loads(capsys.readouterr().out)
    assert result["ok"] and result["passed"] == 3 and result["command"] == "echo '3 passed'"
    with pytest.raises(SystemExit):
        run("commit-push", "--worktree", wt, "--message", "feat: nothing changed")
    (Path(wt) / "a.txt").write_text("two\n", encoding="utf-8")
    for bad in ("update a", "feat: ABC-7 update", "feat: a\nbody", "feat: a Co-Authored-By: x"):
        with pytest.raises(SystemExit):
            run("commit-push", "--worktree", wt, "--message", bad)
    assert run("commit-push", "--worktree", wt, "--message", "feat: update a") == 0
    assert "feature/EPIC-1/ABC-7" in remote_branches(origin)
    log = subprocess.run(["git", "--git-dir", str(origin), "log", "-1", "--format=%s", "feature/EPIC-1/ABC-7"], capture_output=True, check=True)
    assert log.stdout.decode().strip() == "feat: update a"
    assert remote_branches(origin).count("main") == 1
    capsys.readouterr()
    assert run("commit-push", "--worktree", wt, "--message", "feat: update a") == 0
    assert json.loads(capsys.readouterr().out)["already"] is True
    with pytest.raises(SystemExit):
        run("commit-push", "--worktree", wt, "--message", "feat: something else")


def test_commit_push_refuses_non_feature_and_outside_worktrees(git_env, tmp_path):
    ws, repo, origin = git_env
    with pytest.raises(SystemExit):
        run("commit-push", "--worktree", str(repo), "--message", "feat: x")
    outside = tmp_path / ".jira-auto-worktrees" / "X"
    outside.mkdir(parents=True)
    with pytest.raises(SystemExit):
        run("commit-push", "--worktree", str(outside), "--message", "feat: x")
    base = ws / ".jira-auto-worktrees"
    base.mkdir()
    subprocess.run(["git", "-C", str(repo), "worktree", "add", "-q", "-b", "hotfix", str(base / "H"), "origin/main"], check=True)
    (base / "H" / "b.txt").write_text("x", encoding="utf-8")
    with pytest.raises(SystemExit):
        run("commit-push", "--worktree", str(base / "H"), "--message", "feat: x")
    assert "hotfix" not in remote_branches(origin)


def test_run_tests_failure_and_detection(git_env, tmp_path, capsys, monkeypatch):
    ws, repo, origin = git_env
    shared = Path(os.environ["POKEMATE_SHARED_DIR"])
    (shared / "connections.local.json").write_text(json.dumps([
        {"kind": "settings", "id": "automation", "workDir": str(ws), "testCommands": {"app": "echo '1 failed, 2 passed'; exit 1"}},
    ]), encoding="utf-8")
    assert run("create-branch", "--repo", "app", "--branch", "feature/T-1", "--from", "origin/main") == 0
    assert run("worktree-add", "--repo", "app", "--branch", "feature/T-1") == 0
    wt = capsys.readouterr().out.strip().splitlines()[-1]
    assert run("run-tests", "--worktree", wt) == 1
    result = json.loads(capsys.readouterr().out)
    assert not result["ok"] and result["failed"] == 1 and result["passed"] == 2
    (tmp_path / "g").mkdir()
    (tmp_path / "g" / "gradlew").write_text("", encoding="utf-8")
    (tmp_path / "p").mkdir()
    (tmp_path / "p" / "pyproject.toml").write_text("", encoding="utf-8")
    (tmp_path / "p" / "uv.lock").write_text("", encoding="utf-8")
    assert automation.detect_test_command(str(tmp_path / "g")) == "./gradlew test"
    assert automation.detect_test_command(str(tmp_path / "p")) == "uv run pytest"
    assert automation.detect_test_command(str(tmp_path)) is None


def test_worktree_when_branch_checked_out_in_users_checkout(git_env, capsys):
    ws, repo, origin = git_env
    assert run("create-branch", "--repo", "app", "--branch", "feature/U-1", "--from", "origin/main") == 0
    subprocess.run(["git", "-C", str(repo), "checkout", "-q", "feature/U-1"], check=True)
    capsys.readouterr()
    assert run("worktree-add", "--repo", "app", "--branch", "feature/U-1") == 0
    wt = capsys.readouterr().out.strip().splitlines()[-1]
    assert run("worktree-add", "--repo", "app", "--branch", "feature/U-1") == 0
    (Path(wt) / "a.txt").write_text("changed\n", encoding="utf-8")
    assert run("commit-push", "--worktree", wt, "--message", "docs: update a") == 0
    log = subprocess.run(["git", "--git-dir", str(origin), "log", "-1", "--format=%s", "feature/U-1"], capture_output=True, check=True)
    assert log.stdout.decode().strip() == "docs: update a"
    head = subprocess.run(["git", "-C", str(repo), "rev-parse", "--abbrev-ref", "HEAD"], capture_output=True, check=True)
    status = subprocess.run(["git", "-C", str(repo), "status", "--porcelain"], capture_output=True, check=True)
    assert head.stdout.decode().strip() == "feature/U-1" and status.stdout.decode().strip() == ""


def test_junit_counts_from_xml(tmp_path):
    import time
    d = tmp_path / "mod" / "build" / "test-results" / "test"
    d.mkdir(parents=True)
    (d / "TEST-a.xml").write_text('<testsuite tests="5" failures="1" errors="0" skipped="1"></testsuite>', encoding="utf-8")
    (d / "TEST-b.xml").write_text('<testsuite tests="3" failures="0" errors="1" skipped="0"></testsuite>', encoding="utf-8")
    assert automation.junit_counts(str(tmp_path), time.time() - 60) == (5, 2)
    assert automation.junit_counts(str(tmp_path), time.time() + 60) is None


def test_review_state_check(env, capsys):
    _, data, _ = env
    state = data / "automation" / "state" / "github-review-state.json"
    state.parent.mkdir(parents=True, exist_ok=True)
    state.write_text(json.dumps({"baseline": ["https://git.example.com/acme/sample-api/pull/1"],
                                 "reviewed": {"https://git.example.com/acme/sample-api/pull/2": {"sha": "aaa"}}}))

    def check(url, sha=None):
        args = ["review-state", "--check", "--url", url] + (["--sha", sha] if sha else [])
        assert run(*args) == 0
        return json.loads(capsys.readouterr().out)

    assert check("https://git.example.com/acme/sample-api/pull/1", "x") == {"skip": True, "reason": "baseline"}
    assert check("https://git.example.com/acme/sample-api/pull/2", "aaa") == {"skip": True, "reason": "reviewed-same-sha"}
    assert check("https://git.example.com/acme/sample-api/pull/2", "bbb") == {"skip": False, "reason": "reviewed-other-sha"}
    assert check("https://git.example.com/acme/sample-api/pull/3", "ccc") == {"skip": False, "reason": "new"}
    with pytest.raises(SystemExit):
        run("review-state", "--url", "https://git.example.com/acme/sample-api/pull/3")
    assert run("review-state", "--url", "https://git.example.com/acme/sample-api/pull/3", "--sha", "ccc", "--result", "승인") == 0
    capsys.readouterr()
    assert check("https://git.example.com/acme/sample-api/pull/3", "ccc")["skip"] is True


def test_review_trigger_skips_baseline_and_same_sha(env, monkeypatch):
    from pokemate import events
    _, data, _ = env
    state = data / "automation" / "state" / "github-review-state.json"
    state.parent.mkdir(parents=True, exist_ok=True)
    state.write_text(json.dumps({"baseline": ["u1"], "reviewed": {"u2": {"sha": "same"}, "u3": {"sha": "old"}}}))
    monkeypatch.setattr(events, "_system", lambda system_id: (type("S", (), {"base_url": "https://git.example.com"})(), None))
    monkeypatch.setattr(events.github_client, "review_requests", lambda base_url: [
        {"id": n, "repo": "acme/sample-api", "number": n, "title": "t", "author": "kim", "url": f"u{n}"} for n in (1, 2, 3, 4)])
    asked = []
    monkeypatch.setattr(events.github_client, "head_sha", lambda base_url, repo, number: asked.append(number) or {2: "same", 3: "new"}[number])
    found = events._github_review_requested()
    assert [item.key for item in found] == ["3:new", "4"] and asked == [2, 3]
    assert found[0].title.startswith("다시 리뷰 요청") and found[1].title.startswith("리뷰 요청")


def test_count_results_reads_unittest_pytest_gradle():
    assert automation.count_results("test_a ... ok\n\n----------------------------------------------------------------------\nRan 8 tests in 0.001s\n\nOK\n") == (8, 0)
    assert automation.count_results("Ran 5 tests in 0.2s\n\nFAILED (failures=1, errors=1, skipped=1)\n") == (2, 2)
    assert automation.count_results("=========== 3 passed, 1 failed in 0.12s ===========") == (3, 1)
    assert automation.count_results("12 tests completed, 2 failed\n\nFAILURE: Build failed") == (10, 2)
    assert automation.count_results("12 tests completed") == (12, 0)
    assert automation.count_results("nothing here") == (0, 0)


def test_event_notify_skips_duplicates(env, capsys):
    _, data, _ = env
    (data / "teams-webhook-url").write_text("https://hooks.example.com/x")
    fake, _, _ = env
    fake.on(lambda c: c[0] == "curl", stdout=b"200")
    base = ["event", "--notify", "--source", "jira", "--task", "pokemate-jira", "--key", "ACME-1"]
    assert run(*base, "--event", "detected", "--result", "a") == 0
    assert run(*base, "--event", "detected", "--result", "b") == 0
    assert "skipped duplicate" in capsys.readouterr().out
    assert run(*base, "--event", "blocked", "--result", "확인 필요 2건") == 0
    assert run(*base, "--event", "blocked", "--result", "확인 필요 2건") == 0
    assert run(*base, "--event", "blocked", "--result", "개발 중단") == 0
    assert run("event", "--source", "jira", "--task", "pokemate-jira", "--key", "ACME-1", "--event", "detected") == 0
    posts = [c for c, _ in fake.calls if c[0] == "curl"]
    assert len(posts) == 3
