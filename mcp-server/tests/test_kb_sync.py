import json
import subprocess

import pytest

from pokemate import automation, kb_sync


def git(cwd, *args):
    subprocess.run(["git", "-c", "core.autocrlf=false", *args], cwd=cwd, check=True, capture_output=True)


def head(cwd):
    return subprocess.run(["git", "rev-parse", "HEAD"], cwd=cwd, check=True, capture_output=True).stdout.decode().strip()


@pytest.fixture
def source_repo(tmp_path):
    repo = tmp_path / "src"
    repo.mkdir()
    git(repo, "init", "-q", "-b", "main")
    git(repo, "config", "user.email", "t@example.invalid")
    git(repo, "config", "user.name", "t")
    (repo / "docs").mkdir()
    (repo / "docs" / "a.md").write_text("server 10.1.2.3\n", encoding="utf-8")
    (repo / "docs" / "keep.md").write_text("unchanged\n", encoding="utf-8")
    (repo / "docs" / "gone.md").write_text("bye\n", encoding="utf-8")
    (repo / "raw").mkdir()
    (repo / "raw" / "x.md").write_text("raw\n", encoding="utf-8")
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", "first")
    return repo


SOURCE = {"name": "kb", "repo": "team/kb", "paths": ["docs/"], "into": "", "onlyExt": [".md"], "exclude": ["raw/"]}


# ---------- 가리기 ----------

@pytest.mark.parametrize("raw, masked", [
    ("APIDCDV01GM(172.30.247.111, Win2019)", "APIDCDV01GM([IP 가림], Win2019)"),
    ("서버는 127.0.0.1 로컬 바인딩", "서버는 127.0.0.1 로컬 바인딩"),
    ("ArcheFxV2 2.0.0.7 Version=1.0.0.0", "ArcheFxV2 2.0.0.7 Version=1.0.0.0"),
    ('client_email = "svr@itservice.iam.gserviceaccount.com"', 'client_email = "[메일 가림]"'),
    ("LMS 02-1234-5678 / 1588-1234", "LMS [전화번호 가림] / [전화번호 가림]"),
    ("`SellingController.cs:1619-1684` L1551-1567", "`SellingController.cs:1619-1684` L1551-1567"),
    ("UID=regsp;PWD=s3cr3t; Connection", "UID=[가림];PWD=[가림]; Connection"),
    ("CERTPASSWD=abc123", "CERTPASSWD=[가림]"),
    ("비밀번호: hunter2 이다", "비밀번호: [가림] 이다"),
    ("✅PASS = C#+SP+테이블", "✅PASS = C#+SP+테이블"),
    ("--pwd = @new_pwd", "--pwd = @new_pwd"),
    ("pwd={비밀번호};", "pwd={비밀번호};"),
    ("    Password = TestSeller.Password,", "    Password = TestSeller.Password,"),
])
def test_mask_text(raw, masked):
    assert kb_sync.mask_text(raw)[0] == masked


def test_mask_text_counts_and_is_idempotent():
    text, counts = kb_sync.mask_text("a 10.0.0.12 b me@example.com PWD=x;")
    assert counts == {"[IP 가림]": 1, "[메일 가림]": 1, "[가림]": 1}
    assert kb_sync.mask_text(text) == (text, {})


def test_suspect_lines():
    assert kb_sync.suspect_lines("ok\nSP 배포 계정 regsp/x\nPWD=[가림]\n") == [2, 3]


# ---------- 고르기·버전 ----------

def test_selected_paths_exclude_ext():
    s = {**SOURCE, "excludeDirs": ["_scan"]}
    assert kb_sync.selected("docs/a.md", s)
    assert not kb_sync.selected("docs/a.json", s)
    assert not kb_sync.selected("docsx/a.md", s)
    assert not kb_sync.selected("docs/_scan/a.md", s)
    assert not kb_sync.selected("raw/x.md", {"name": "kb", "exclude": ["raw/", "README.md"]})
    assert not kb_sync.selected("README.md", {"name": "kb", "exclude": ["raw/", "README.md"]})
    assert kb_sync.selected("wiki/README.md", {"name": "kb", "exclude": ["raw/", "README.md"]})
    assert kb_sync.selected("CLAUDE.md", {"name": "kb", "paths": ["README.md", "CLAUDE.md", "knowledgebase/"]})
    assert kb_sync.dest_path("README.md", {"into": "knowledgebase"}) == "knowledgebase/README.md"


def test_next_hotfix():
    merged = ["release/v2-0-0", "release/v1-2-0", "hotfix/v1-2-1/x", "feature/v9-0-0/y"]
    assert kb_sync.next_hotfix(merged, []) == "v2-0-1"
    assert kb_sync.next_hotfix(merged, ["hotfix/v2-0-1/other", "release/v2-1-0"]) == "v2-0-2"
    assert kb_sync.next_hotfix([], []) == "v1-0-1"
    assert kb_sync.sync_branch_pattern("pricing").match("hotfix/v2-0-1/kb-pricing")
    assert not kb_sync.sync_branch_pattern("pricing").match("hotfix/v2-0-1/kb-product")


# ---------- 반영 ----------

def test_apply_source_first_time_copies_selected_files_masked(source_repo, tmp_path):
    dest = tmp_path / "dest"
    result = kb_sync.apply_source(SOURCE, source_repo, "HEAD", None, dest)
    assert sorted(result["added"]) == ["docs/a.md", "docs/gone.md", "docs/keep.md"]
    assert (dest / "docs" / "a.md").read_text(encoding="utf-8") == "server [IP 가림]\n"
    assert not (dest / "raw").exists()
    assert result["masked"] == {"[IP 가림]": 1}


def test_apply_source_only_changes_since_base(source_repo, tmp_path):
    dest = tmp_path / "dest"
    base = head(source_repo)
    kb_sync.apply_source(SOURCE, source_repo, "HEAD", None, dest)
    # 손으로 더 가린 파일: 원본이 바뀌지 않았으면 그대로 둔다
    (dest / "docs" / "keep.md").write_text("[가림] by hand\n", encoding="utf-8")
    (dest / "docs" / "a.md").write_text("server [IP 가림] 계정 [가림]\n", encoding="utf-8")
    (source_repo / "docs" / "a.md").write_text("server 10.1.2.3\nnew line PWD=x;\n", encoding="utf-8")
    (source_repo / "docs" / "new.md").write_text("new\n", encoding="utf-8")
    (source_repo / "docs" / "gone.md").unlink()
    (source_repo / "raw" / "x.md").write_text("raw2\n", encoding="utf-8")
    git(source_repo, "add", "-A")
    git(source_repo, "commit", "-q", "-m", "second")

    result = kb_sync.apply_source(SOURCE, source_repo, "HEAD", base, dest)
    assert result["from"] == base and result["to"] == head(source_repo)
    assert result["added"] == ["docs/new.md"]
    assert result["modified"] == ["docs/a.md"]
    assert result["deleted"] == ["docs/gone.md"]
    assert (dest / "docs" / "keep.md").read_text(encoding="utf-8") == "[가림] by hand\n"
    assert (dest / "docs" / "a.md").read_text(encoding="utf-8") == "server [IP 가림]\nnew line PWD=[가림];\n"
    assert [c["path"] for c in result["check"]] == ["docs/a.md"]
    assert "손으로 가린" in result["check"][0]["why"]


def test_apply_source_no_change_when_base_is_head(source_repo, tmp_path):
    result = kb_sync.apply_source(SOURCE, source_repo, "HEAD", head(source_repo), tmp_path / "dest")
    assert not kb_sync.has_changes(result)


def test_apply_source_keeps_listed_files(source_repo, tmp_path):
    s = {**SOURCE, "paths": [""], "exclude": ["raw/"]}
    (source_repo / "README.md").write_text("team readme\n", encoding="utf-8")
    git(source_repo, "add", "-A")
    git(source_repo, "commit", "-q", "-m", "readme")
    dest = tmp_path / "dest"
    dest.mkdir()
    (dest / "README.md").write_text("snapshot note\n", encoding="utf-8")
    kb_sync.apply_source(s, source_repo, "HEAD", None, dest, keep=["README.md"])
    assert (dest / "README.md").read_text(encoding="utf-8") == "snapshot note\n"


def test_pr_body_lists_files_without_content():
    results = [{"name": "kb", "repo": "team/kb", "from": "a" * 40, "to": "b" * 40, "added": ["docs/new.md"], "modified": [],
                "deleted": [], "masked": {"[가림]": 2}, "check": [{"path": "docs/new.md", "why": "비밀번호·계정 낱말이 남은 줄 1개"}]}]
    body = kb_sync.pr_body("kb", "resources/kb", results, "2026-11-23")
    assert "`aaaaaaa` → `bbbbbbb`" in body and "추가 `resources/kb/docs/new.md`" in body
    assert "머지 전에 사람이 확인할 파일" in body and "## 확인 결과" in body


# ---------- 명령 ----------

def test_cmd_domain_without_kb(tmp_path, capsys):
    cfg = tmp_path / "kb-sync.json"
    cfg.write_text(json.dumps({"target": {"repo": "me/brain"}, "domains": {"coupon": "pricing"}, "kbs": {}}), encoding="utf-8")
    assert automation.main(["kb-sync", "--domain", "payment", "--config", str(cfg)]) == 0
    assert json.loads(capsys.readouterr().out)["status"] == "no-kb"


def test_cmd_unknown_kb(tmp_path, capsys):
    cfg = tmp_path / "kb-sync.json"
    cfg.write_text(json.dumps({"target": {"repo": "me/brain"}, "domains": {"coupon": "pricing"}, "kbs": {}}), encoding="utf-8")
    assert automation.main(["kb-sync", "--domain", "coupon", "--config", str(cfg)]) == 0
    assert json.loads(capsys.readouterr().out)["status"] == "unknown-kb"


def test_cmd_blocked_in_eval(monkeypatch):
    monkeypatch.setenv("POKEMATE_EVAL", "1")
    with pytest.raises(SystemExit):
        automation.main(["kb-sync", "--kb", "x"])
