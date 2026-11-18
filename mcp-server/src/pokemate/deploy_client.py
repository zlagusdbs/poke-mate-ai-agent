"""사내 Deploy (배포 관리) REST API 조회와 배포 승인. 사내 계정 Basic 인증."""

from urllib.parse import quote

from .basic_client import BasicClient

SYSTEM = "deploy"


def _release(x: dict) -> dict:
    def name(v):
        return v.get("name") if isinstance(v, dict) else v
    return {
        "id": x.get("id"),
        "domainId": x.get("domainId"),
        "summary": x.get("summary") or "",
        "description": (x.get("description") or "")[:200],
        "status": x.get("status"),
        "creatingUser": name(x.get("creatingUser")),
        "approver": name(x.get("approver")),
        "created": (x.get("created") or "")[:16],
    }


class DeployClient(BasicClient):
    system = SYSTEM

    def verify(self) -> None:
        self._get("/api/releases")

    def my_releases(self) -> list[dict]:
        return [_release(x) for x in self._get("/api/releases").json() or []]

    def approvals(self) -> list[dict]:
        return [_release(x) for x in self._get("/api/releases/approvals").json() or []]

    def domain_name(self, domain_id) -> str:
        return (self._get(f"/api/domains/{domain_id}").json() or {}).get("name") or ""

    def approve(self, domain_name: str, release_id) -> None:
        """배포 승인: 상태를 approved 로 바꾼다. 승인되면 시스템이 배치를 시작한다."""
        self._patch(f"/api/domains/{quote(str(domain_name), safe='')}/releases/{release_id}",
                    [{"op": "replace", "path": "/status", "value": "approved"}])

    def domain_releases(self, domain: str, status: str = "", limit: int = 20) -> list[dict]:
        data = self._get(f"/api/domains/{domain}/releases", params={"status": status, "offset": 0, "limit": limit}).json()
        return [_release(x) for x in data or []]

    def search(self, start_date: str, end_date: str) -> list[dict]:
        data = self._get("/api/releases/search", params={"startDate": start_date, "endDate": end_date}).json()
        return [_release(x) for x in data or []]
