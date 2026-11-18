import json

from pokemate import server


def test_usage_summary_reads_app_snapshot(tmp_path, monkeypatch):
    monkeypatch.setenv("POKEMATE_DATA_DIR", str(tmp_path))
    assert server.usage_summary()["error"] == "NO_USAGE"
    (tmp_path / "usage.json").write_text(json.dumps({"today": {"tokens": {"total": 5}, "costUSD": 0.1}}), encoding="utf-8")
    assert server.usage_summary()["today"]["tokens"]["total"] == 5
