import json

import pytest

from pokemate import automation


@pytest.fixture
def trial(tmp_path, monkeypatch):
    shared = tmp_path / "shared"
    shared.mkdir()
    (shared / "connections.local.json").write_text(json.dumps([
        {"id": "jira", "name": "Jira", "group": "ad", "enabled": True, "baseUrl": "https://jira.example.com"},
        {"kind": "settings", "id": "automation", "workDir": str(tmp_path / "real-ws"), "transitions": {"start": "Doing", "done": "Done"}},
    ]), encoding="utf-8")
    monkeypatch.setenv("POKEMATE_SHARED_DIR", str(shared))
    root = tmp_path / "trial"
    (root / "jira").mkdir(parents=True)
    (root / "automation.json").write_text(json.dumps({"workDir": str(root / "ws"), "transitions": {"start": "In Progress", "done": "In Review"}}))
    issue = {"issue": {"key": "ACME-1", "fields": {"summary": "sample", "status": {"name": "Open"}, "issuetype": {"name": "Story"},
                                                    "assignee": {"name": "sample-user", "key": "sample-user"}, "issuelinks": []}},
             "comments": [],
             "transitions": [{"id": "1", "name": "Start", "to": {"name": "In Progress"}}, {"id": "2", "name": "Review", "to": {"name": "In Review"}}]}
    (root / "jira" / "ACME-1.json").write_text(json.dumps(issue))
    monkeypatch.setenv("POKEMATE_TRIAL", str(root))
    monkeypatch.setenv("POKEMATE_DATA_DIR", str(tmp_path / "data"))
    return root


def stored(root):
    return json.loads((root / "jira" / "ACME-1.json").read_text())


def test_trial_settings_overlay(trial):
    assert automation.work_dir() == str(trial / "ws")
    assert automation.settings()["transitions"]["done"] == "In Review"


def test_trial_jira_writes_go_to_file(trial, tmp_path, capsys):
    note = tmp_path / "plan.md"
    note.write_text("[자동 작성] 처리 계획\n- sample", encoding="utf-8")
    assert automation.main(["jira-transition", "--key", "ACME-1", "--to", "start"]) == 0
    assert automation.main(["jira-comment", "--key", "ACME-1", "--body-file", str(note)]) == 0
    assert automation.main(["jira-get", "--key", "ACME-1"]) == 0
    data = stored(trial)
    assert data["issue"]["fields"]["status"]["name"] == "In Progress"
    assert data["comments"][0]["body"].startswith("[자동 작성] 처리 계획")
    assert '"status": "In Progress"' in capsys.readouterr().out


def test_trial_card_not_sent(trial, capsys):
    assert automation.main(["event", "--notify", "--source", "jira", "--task", "t", "--event", "blocked", "--key", "ACME-1"]) == 0
    assert "trial: card not sent" in capsys.readouterr().out


def test_without_trial_env_no_overlay(trial, monkeypatch):
    monkeypatch.delenv("POKEMATE_TRIAL")
    assert automation.settings()["transitions"]["done"] == "Done"
