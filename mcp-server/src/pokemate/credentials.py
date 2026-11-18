"""로그인 정보 저장소. 키체인(Windows 는 자격 증명 관리자)에만 저장한다."""

import json
import os
from dataclasses import asdict, dataclass
from pathlib import Path

import keyring
import platformdirs

SERVICE = "poke-mate"
LEGACY_SERVICE = "pocket-mate"


@dataclass(frozen=True)
class Credential:
    """사내 계정 아이디·비밀번호."""

    username: str
    secret: str


def data_dir() -> Path:
    override = os.environ.get("POKEMATE_DATA_DIR")
    path = Path(override) if override else Path(platformdirs.user_data_dir("poke-mate", appauthor=False))
    path.mkdir(parents=True, exist_ok=True)
    return path


def save(system: str, cred: Credential) -> None:
    keyring.set_password(SERVICE, system, json.dumps(asdict(cred), ensure_ascii=False))
    _set_expired(system, False)


def load(system: str) -> Credential | None:
    raw = keyring.get_password(SERVICE, system)
    if raw is None:
        return None
    return Credential(**json.loads(raw))


def migrate_legacy(accounts: list[str]) -> list[str]:
    moved = []
    for account in accounts:
        if keyring.get_password(SERVICE, account) is not None:
            continue
        old = keyring.get_password(LEGACY_SERVICE, account)
        if old is not None:
            keyring.set_password(SERVICE, account, old)
            moved.append(account)
    return moved


def delete(system: str) -> None:
    try:
        keyring.delete_password(SERVICE, system)
    except keyring.errors.PasswordDeleteError:
        pass
    _set_expired(system, False)


def mark_expired(system: str) -> None:
    _set_expired(system, True)


def is_expired(system: str) -> bool:
    return system in _read_status().get("expired", [])


def _status_file() -> Path:
    return data_dir() / "status.json"


def _read_status() -> dict:
    try:
        return json.loads(_status_file().read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError):
        return {}


def _set_expired(system: str, expired: bool) -> None:
    status = _read_status()
    current = set(status.get("expired", []))
    current.add(system) if expired else current.discard(system)
    status["expired"] = sorted(current)
    _status_file().write_text(json.dumps(status, ensure_ascii=False), encoding="utf-8")
