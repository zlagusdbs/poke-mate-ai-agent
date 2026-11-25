import subprocess
import sys

from pokemate import github_client


def test_api_decodes_gh_output_as_utf8(monkeypatch):
    # gh 는 UTF-8 로 쓴다. Windows 기본 코드페이지(cp949)로 읽으면 한글 PR 제목에서 깨져 결과가 비어 버렸다.
    title = "[PRICING-1] 정율 쿠폰 – 회귀 원인"
    script = ("import sys,json; sys.stdout.buffer.write(json.dumps({'items':[{'number':1,'title':%r,"
              "'repository_url':'https://h/api/v3/repos/o/r','html_url':'u','user':{'login':'a'},'id':9}]},"
              "ensure_ascii=False).encode('utf-8'))" % title)
    real_run = subprocess.run
    monkeypatch.setattr(github_client, "gh_path", lambda: sys.executable)
    monkeypatch.setattr(github_client.subprocess, "run",
                        lambda cmd, **kw: real_run([sys.executable, "-c", script], **kw))
    rows = github_client.review_requests("https://github.example.invalid")
    assert [r["title"] for r in rows] == [title]


def test_direct_env_skips_proxy_for_internal_host():
    env = github_client.direct_env("ghe.example.invalid", {"HTTPS_PROXY": "http://10.0.0.1:8080", "no_proxy": "localhost"})
    assert env["NO_PROXY"] == env["no_proxy"] == "localhost,ghe.example.invalid"
    assert env["HTTPS_PROXY"] == "http://10.0.0.1:8080"
    assert "NO_PROXY" not in github_client.direct_env("github.com", {})
