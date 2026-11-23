"""메이트 사내 시스템 연결 (MCP 서버 + 연결 관리 명령어)."""


def _utf8_stdio(streams=None) -> None:
    """Windows 에서는 표준 입출력을 UTF-8 로 맞춘다.

    파이프로 실행되면 Python 은 시스템 코드 페이지(한국어 Windows 는 cp949)로 읽고 쓴다.
    앱은 UTF-8 로 주고받으므로, 맞추지 않으면 한글 결과 메시지가 깨지고 한글이 든 비밀번호도 틀어진다.
    """
    import sys

    if sys.platform != "win32":
        return
    for stream in streams if streams is not None else (sys.stdin, sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError, OSError):
            pass


def main() -> None:
    import sys

    _utf8_stdio()
    from .cli import main as cli_main

    sys.exit(cli_main())
