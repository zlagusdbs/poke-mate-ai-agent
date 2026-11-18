from pokemate import rules


def test_read_skill_local_over_shared(tmp_path, monkeypatch):
    monkeypatch.setenv("POKEMATE_LOCAL_DIR", str(tmp_path / "local"))
    monkeypatch.setenv("POKEMATE_SHARED_DIR", str(tmp_path / "shared"))
    (tmp_path / "local" / "skills").mkdir(parents=True)
    (tmp_path / "shared" / "skills").mkdir(parents=True)
    (tmp_path / "shared" / "skills" / "jira.md").write_text("# Jira 일반", encoding="utf-8")
    (tmp_path / "shared" / "skills" / "hr.md").write_text("# 근태 일반", encoding="utf-8")
    (tmp_path / "local" / "skills" / "jira.md").write_text("# Jira\n- 프로젝트 키: X", encoding="utf-8")
    assert rules.read("jira")["text"].startswith("# Jira\n")
    assert rules.read("skills/jira.md")["name"] == "skills/jira.md"
    assert rules.read("hr")["text"] == "# 근태 일반"
    missing = rules.read("../settings")
    assert missing["error"] == "NOT_FOUND" and missing["available"] == ["hr.md", "jira.md"]
