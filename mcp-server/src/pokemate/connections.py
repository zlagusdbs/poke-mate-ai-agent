"""연결 대상 시스템 목록 (shared/connections.json)."""

import json
import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class SystemDef:
    id: str
    name: str
    group: str
    enabled: bool
    base_url: str | None
    auth: str = "password"   # password: 사내 아이디·비밀번호 / gh: GitHub CLI 로그인 / claude.ai: claude.ai 커넥터 (앱이 확인)
    web_url: str | None = None  # 사람이 여는 화면 주소 (API 주소와 다를 때)
    company: str | None = None  # 연결 화면에서 묶어 보일 회사 이름
    login_domain: str | None = None  # 로그인 화면에서 고르는 도메인 (Approval)
    home: str | None = None
    aliases: tuple[str, ...] = ()


def shared_dir() -> Path:
    override = os.environ.get("POKEMATE_SHARED_DIR")
    if override:
        return Path(override)
    # mcp-server/src/pokemate/connections.py → 저장소 루트/shared
    return Path(__file__).resolve().parents[3] / "shared"


def local_dir() -> Path:
    override = os.environ.get("POKEMATE_LOCAL_DIR")
    return Path(override) if override else Path(__file__).resolve().parents[3] / "local"


def connections_file() -> Path:
    """회사 주소가 든 connections.local.json(저장소에 올리지 않음)이 있으면 그것을, 없으면 예시 파일을 쓴다."""
    legacy = shared_dir() / "connections.local.json"
    if os.environ.get("POKEMATE_SHARED_DIR"):
        return legacy if legacy.exists() else shared_dir() / "connections.example.json"
    for path in (local_dir() / "connections.json", legacy):
        if path.exists():
            return path
    return shared_dir() / "connections.example.json"


def load_settings(name: str, path: Path | None = None) -> dict:
    raw = json.loads((path or connections_file()).read_text(encoding="utf-8"))
    return next((dict(s) for s in raw if s.get("kind") == "settings" and s.get("id") == name), {})


def load_systems(path: Path | None = None) -> list[SystemDef]:
    raw = [s for s in json.loads((path or connections_file()).read_text(encoding="utf-8")) if s.get("kind") != "settings"]
    return [
        SystemDef(id=s["id"], name=s["name"], group=s["group"], enabled=bool(s["enabled"]), base_url=s.get("baseUrl"),
                  auth=s.get("auth", "password"), web_url=s.get("webUrl"), company=s.get("company"),
                  login_domain=s.get("loginDomain"), home=s.get("home"), aliases=tuple(s.get("aliases") or ()))
        for s in raw
    ]


def find_system(system_id: str, systems: list[SystemDef] | None = None) -> SystemDef | None:
    return next((s for s in (systems or load_systems()) if s.id == system_id), None)
