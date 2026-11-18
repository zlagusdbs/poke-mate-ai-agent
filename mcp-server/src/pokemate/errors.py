"""사내 시스템 호출 오류. code 는 도구 결과와 앱이 그대로 읽는 문자열이다."""


class PokeMateError(Exception):
    prefix = "ERROR"

    def __init__(self, system: str, detail: str = ""):
        super().__init__(f"{self.prefix}:{system} {detail}".strip())
        self.system = system

    @property
    def code(self) -> str:
        return f"{self.prefix}:{self.system}"


class AuthExpired(PokeMateError):
    prefix = "AUTH_EXPIRED"


class NetworkUnreachable(PokeMateError):
    prefix = "NETWORK_UNREACHABLE"


class NotConnected(PokeMateError):
    prefix = "NOT_CONNECTED"


class RequestRejected(PokeMateError):
    """시스템이 요청 내용을 거절함 (필수 항목 누락, 잘못된 값 등). detail 에 시스템이 준 이유."""
    prefix = "REJECTED"
