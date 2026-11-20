"""사내 Deploy (배포 관리) REST API 조회와 배포 승인.

인증: 연결 설정에 화면 주소(webUrl)가 있으면 그 화면에 사내 계정으로 한 번 로그인해 받은 쿠키로 API 를 부른다.
배포 API 는 Basic 요청마다 LDAP 인증과 GitHub 토큰 발급을 해서 무겁고, GitHub 이 멈추면 Basic 도 실패하기 때문이다.
webUrl 이 없으면 예전처럼 요청마다 Basic 인증을 보낸다.
"""

from urllib.parse import quote

import httpx

from .basic_client import TIMEOUT_SECONDS, BasicClient
from .credentials import Credential
from .errors import AuthExpired, NetworkUnreachable, PokeMateError

SYSTEM = "deploy"
LOGIN_PATH = "/Account/Authenticate"   # 화면의 로그인 폼(/Account/LogOn)이 보내는 곳
LOGIN_PAGE = "/Account/LogOn"


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

    def __init__(self, base_url: str, cred: Credential, transport: httpx.BaseTransport | None = None, login_url: str = ""):
        self.login_url = (login_url or "").rstrip("/")
        if not self.login_url:
            super().__init__(base_url, cred, transport)
            self._logged_in = True
            return
        self.base_url = base_url.rstrip("/")
        self._cred = cred
        self._logged_in = False
        self._client = httpx.Client(base_url=self.base_url, timeout=TIMEOUT_SECONDS, transport=transport,
                                    headers={"Accept": "application/json"})

    def _login(self) -> None:
        """화면에 로그인해 쿠키를 받는다. 성공하면 로그인 화면이 아닌 곳으로 넘겨 주며 쿠키를 심는다."""
        if self._logged_in:
            return
        try:
            response = self._client.post(self.login_url + LOGIN_PATH, follow_redirects=False,
                                         data={"UserId": self._cred.username, "Password": self._cred.secret, "ReturnUrl": ""},
                                         headers={"Accept": "text/html", "Referer": self.login_url + LOGIN_PAGE})
        except httpx.TransportError as e:
            raise NetworkUnreachable(SYSTEM, str(e)) from e
        if response.status_code >= 500:
            raise PokeMateError(SYSTEM, f"HTTP {response.status_code}")
        moved = response.status_code in (301, 302, 303) and LOGIN_PAGE.lower() not in response.headers.get("location", "").lower()
        if not moved or not response.cookies:
            raise AuthExpired(SYSTEM, "로그인 실패")
        self._logged_in = True

    def _get(self, path: str, params: dict | None = None) -> httpx.Response:
        self._login()
        return super()._get(path, params)

    def _patch(self, path: str, body) -> None:
        self._login()
        super()._patch(path, body)

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
