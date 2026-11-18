"""메이트 메모: 메이트 폴더(앱 데이터 폴더)의 notes/*.md. 앱의 Notes.swift 와 같은 폴더를 본다."""

from pathlib import Path

from . import credentials

SNIPPET = 120


def notes_dir() -> Path:
    return credentials.data_dir() / "notes"


def _title(path: Path, text: str) -> str:
    for line in text.splitlines():
        if line.strip():
            return line.lstrip("# ").strip()[:80]
    return path.stem


def search(query: str = "", limit: int = 30) -> dict:
    """제목이나 본문에 query 가 든 메모 (비우면 최근 메모). 최근 수정 순."""
    q = query.strip().lower()
    rows = []
    if notes_dir().is_dir():
        for path in sorted(notes_dir().glob("*.md"), key=lambda p: p.stat().st_mtime, reverse=True):
            text = path.read_text(encoding="utf-8")
            if q and q not in text.lower() and q not in path.stem.lower():
                continue
            body = " ".join(text.split())
            at = body.lower().find(q) if q else 0
            rows.append({"name": path.name, "title": _title(path, text),
                         "snippet": body[max(0, at - 30): max(0, at - 30) + SNIPPET]})
            if len(rows) >= limit:
                break
    return {"notes": rows}


def read(name: str) -> dict:
    path = notes_dir() / Path(name.strip()).name
    if path.suffix != ".md":
        path = path.with_suffix(".md")
    if path.parent != notes_dir() or not path.is_file():
        return {"error": "NOT_FOUND", "detail": f"메모 {name} 이(가) 없어요"}
    return {"name": path.name, "text": path.read_text(encoding="utf-8")}
