from pokemate import connections


def test_connections_file_prefers_local(tmp_path, monkeypatch):
    monkeypatch.delenv("POKEMATE_SHARED_DIR", raising=False)
    monkeypatch.setenv("POKEMATE_LOCAL_DIR", str(tmp_path / "local"))
    assert connections.connections_file().name in ("connections.local.json", "connections.example.json")
    (tmp_path / "local").mkdir()
    (tmp_path / "local" / "connections.json").write_text("[]", encoding="utf-8")
    assert connections.connections_file() == tmp_path / "local" / "connections.json"


def test_shared_dir_env_keeps_legacy_file(tmp_path, monkeypatch):
    monkeypatch.setenv("POKEMATE_SHARED_DIR", str(tmp_path / "shared"))
    monkeypatch.setenv("POKEMATE_LOCAL_DIR", str(tmp_path / "local"))
    (tmp_path / "local").mkdir()
    (tmp_path / "local" / "connections.json").write_text("[]", encoding="utf-8")
    (tmp_path / "shared").mkdir()
    (tmp_path / "shared" / "connections.local.json").write_text("[]", encoding="utf-8")
    assert connections.connections_file() == tmp_path / "shared" / "connections.local.json"
