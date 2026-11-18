import json

from pokemate import alarms


def test_list_alarms_summarises_time_and_trigger(tmp_path, monkeypatch):
    monkeypatch.setenv("POKEMATE_LOCAL_DIR", str(tmp_path))
    assert alarms.list_alarms() == {"alarms": [], "count": 0}
    data = [
        {"id": "A", "title": "아침 보고", "prompt": "{{daily-report}}", "hour": 9, "minute": 5,
         "repeatRule": {"weekdays": {}}, "enabled": True},
        {"id": "B", "title": "Jira 자동 처리", "prompt": "{{file:~/x/jira.md}}", "hour": 0, "minute": 0,
         "repeatRule": {"daily": {}}, "enabled": False, "allowWrite": True,
         "trigger": {"system": "jira", "event": "jira.assigned_to_me", "intervalMinutes": 1}},
        {"id": "C", "title": "", "prompt": "", "hour": 18, "minute": 0,
         "repeatRule": {"days": {"_0": [2, 4]}}, "enabled": True},
    ]
    (tmp_path / "alarms.json").write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    out = alarms.list_alarms()
    assert out["count"] == 3 and out["enabled"] == 2
    a, b, c = out["alarms"]
    assert a["when"] == "09:05 · 평일" and a["task"] == "내 미완료 Jira 정리 보고" and a["kind"] == "시각"
    assert b["kind"] == "트리거" and "1분마다" in b["when"] and "새 이슈가 할당" in b["when"]
    assert b["allowWrite"] is True and b["enabled"] is False and b["task"].startswith("파일에 적힌 지시")
    assert c["title"] == "(제목 없음)" and c["when"] == "18:00 · 월·수" and c["task"] == "알림만"


def test_alarms_list_includes_last_run_and_history():
    import datetime as dt
    import json as _json
    from pokemate import alarms as _alarms, credentials as _credentials
    d = _credentials.data_dir()
    from pokemate.connections import local_dir as _local_dir
    _local_dir().mkdir(parents=True, exist_ok=True)
    (_local_dir() / "alarms.json").write_text(_json.dumps([
        {"id": "A1", "title": "Jira", "prompt": "{{prompt:jira}}", "hour": 0, "minute": 0, "repeatRule": {"daily": {}}, "enabled": True,
         "trigger": {"system": "jira", "event": "jira.assigned_to_me", "intervalMinutes": 1}, "allowWrite": True},
        {"id": "A2", "title": "근무", "prompt": "", "hour": 10, "minute": 0, "repeatRule": {"weekdays": {}}, "enabled": True, "command": "pokemate auto worklog-check"},
    ]), encoding="utf-8")
    today = dt.date.today().isoformat()
    (d / "alarm-runs.json").write_text(_json.dumps({"A1": {"lastAt": "2026-11-17T07:45:10Z", "result": "새 건 1", "failed": False, "day": today, "todayCount": 1}}), encoding="utf-8")
    rows = _alarms.list_alarms()["alarms"]
    assert rows[0]["lastRun"]["result"] == "새 건 1" and rows[0]["lastRun"]["todayCount"] == 1
    assert rows[0]["task"] == "프롬프트 파일 prompts/jira.md" and rows[1]["task"].startswith("명령 실행:") and rows[1]["lastRun"] is None
    (d / "automation" / "activity").mkdir(parents=True)
    (d / "automation" / "activity" / "2026-11.jsonl").write_text(
        _json.dumps({"time": "2026-11-17T16:45:10+09:00", "source": "jira", "event": "processed", "key": "NOVA-9900"}, ensure_ascii=False) + "\n"
        + _json.dumps({"time": "2026-11-17T16:50:00+09:00", "source": "github", "event": "approved", "key": "o/r#1"}) + "\n", encoding="utf-8")
    (d / "automation" / "state").mkdir()
    (d / "automation" / "state" / "jira-state.json").write_text(_json.dumps({"processed": {"NOVA-9900": {"processedAt": "2026-11-17T16:45:10+09:00", "result": "브랜치"}}}), encoding="utf-8")
    found = _alarms.history("NOVA-9900")
    assert [e["key"] for e in found["activity"]] == ["NOVA-9900"] and "NOVA-9900" in found["jira-state"]
    assert len(_alarms.history()["activity"]) == 2


def test_work_sessions_and_history_session_tag():
    import json as _json
    from pokemate import alarms as _alarms, credentials as _credentials, server as _server
    base = _credentials.data_dir() / "automation"
    base.mkdir(parents=True, exist_ok=True)
    (base / "sessions.json").write_text(_json.dumps({
        "sessions": {"price-service": {"id": "7859cf89-7fc4-4414", "repo": "price-service", "createdAt": "c", "lastUsed": "2026-11-17T08:09:29Z",
                                       "keys": ["NOVA-9900"], "contextTokens": 300, "contextWindow": 1000,
                                       "usage": {"input": 1, "output": 2, "cacheRead": 3, "cacheWrite": 4, "costUSD": 0.5}},
                     "운영": {"id": "aaaaaaaa-1", "repo": "운영", "lastUsed": "2026-11-17T09:00:00Z", "keys": [], "usage": {}}},
        "archived": [{"id": "bbbbbbbb-2", "repo": "price-service", "lastUsed": "2026-11-10T00:00:00Z", "keys": ["NOVA-1"], "usage": {}}],
    }), encoding="utf-8")
    (base / "sessions-live.json").write_text(_json.dumps({"running": {"운영": "OPS-3"}, "queued": {"운영": ["OPS-4"]}, "updatedAt": "u"}), encoding="utf-8")
    found = _server.work_sessions(key="NOVA-9900")["sessions"]
    assert len(found) == 1 and found[0]["repo"] == "price-service" and found[0]["sessionId"] == "7859cf89" and found[0]["contextPercent"] == 30
    assert found[0]["tokens"] == 10 and found[0]["costUSD"] == 0.5 and not found[0]["archived"]
    old = _server.work_sessions(key="NOVA-1")["sessions"]
    assert old[0]["archived"] and old[0]["sessionId"] == "bbbbbbbb" and old[0]["running"] is None
    ops = _server.work_sessions(key="OPS-4")["sessions"]
    assert ops[0]["repo"] == "운영" and ops[0]["running"] == "OPS-3" and ops[0]["queued"] == ["OPS-4"]
    every = _server.work_sessions()["sessions"]
    assert [r["repo"] for r in every] == ["운영", "price-service", "price-service"] and every[-1]["archived"]
    (base / "activity").mkdir(exist_ok=True)
    (base / "activity" / "2026-11.jsonl").write_text(_json.dumps({"time": "t", "source": "jira", "event": "processed", "key": "NOVA-9900"}) + "\n", encoding="utf-8")
    hist = _alarms.history("NOVA-9900")
    assert hist["activity"][0]["workSession"] == {"repo": "price-service", "sessionId": "7859cf89"}


def test_work_sessions_counts_other_sessions():
    import json as _json
    from pokemate import credentials as _credentials, server as _server
    d = _credentials.data_dir()
    (d / "automation").mkdir(parents=True, exist_ok=True)
    (d / "automation" / "sessions.json").write_text(_json.dumps({"sessions": {"r": {"id": "w1", "repo": "r", "keys": [], "usage": {}}}, "archived": []}), encoding="utf-8")
    work = d / "claude-config" / "projects" / "-Users-u-Library-Application-Support-poke-mate-work"
    work.mkdir(parents=True)
    for name in ("w1", "a", "b"):
        (work / f"{name}.jsonl").write_text("{}", encoding="utf-8")
    old = d / "claude-config" / "projects" / "-Users-u-Library-Application-Support-old-app-work"
    old.mkdir()
    (old / "c.jsonl").write_text("{}", encoding="utf-8")
    result = _server.work_sessions()
    other = result["otherSessions"]
    assert result["activeCount"] == 1 and other["total"] == 3
    assert {g["label"]: g["count"] for g in other["groups"]} == {"PokeMate 대화·알람 실행": 2, "예전 데이터 폴더 실행 기록": 1}
    assert "~/.claude/projects" in other["note"]
