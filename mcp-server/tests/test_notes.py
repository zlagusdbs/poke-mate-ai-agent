from pokemate import notes


def test_search_and_read(tmp_path, monkeypatch):
    monkeypatch.setenv("POKEMATE_DATA_DIR", str(tmp_path))
    (tmp_path / "notes").mkdir()
    (tmp_path / "notes" / "a.md").write_text("# 회의 메모\n쿠폰 TTL 은 10분으로", encoding="utf-8")
    (tmp_path / "notes" / "b.md").write_text("장보기\n우유", encoding="utf-8")
    found = notes.search("ttl")["notes"]
    assert [n["name"] for n in found] == ["a.md"] and found[0]["title"] == "회의 메모" and "TTL" in found[0]["snippet"]
    assert {n["name"] for n in notes.search()["notes"]} == {"a.md", "b.md"}
    assert notes.read("a")["text"].startswith("# 회의 메모")
    assert notes.read("../settings.json")["error"] == "NOT_FOUND"
