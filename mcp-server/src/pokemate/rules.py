"""메이트 설정의 skills/*.md 읽기. 앱의 Rules.swift 와 같은 순서로 local/ 을 먼저, 없으면 shared/ 를 본다."""

from pathlib import Path

from .connections import local_dir, shared_dir


def skills_dirs() -> list[Path]:
    return [local_dir() / "skills", shared_dir() / "skills"]


def read(name: str) -> dict:
    stem = Path(name.strip()).name.removesuffix(".md")
    for folder in skills_dirs():
        path = folder / f"{stem}.md"
        if stem and path.parent == folder and path.is_file():
            return {"name": f"skills/{path.name}", "text": path.read_text(encoding="utf-8")}
    names = sorted({p.name for folder in skills_dirs() if folder.is_dir() for p in folder.glob("*.md")})
    return {"error": "NOT_FOUND", "detail": f"skills/{stem}.md 가 없어요", "available": names}
