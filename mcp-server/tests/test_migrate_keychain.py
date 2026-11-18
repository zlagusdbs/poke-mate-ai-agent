import json
from io import StringIO

import keyring

from pokemate import cli, credentials


def test_migrate_keychain_copies_without_printing_values(monkeypatch):
    keyring.set_password(credentials.LEGACY_SERVICE, "jira", '{"username": "u", "secret": "s3cret"}')
    keyring.set_password(credentials.LEGACY_SERVICE, "wiki", '{"username": "u", "secret": "old"}')
    keyring.set_password(credentials.SERVICE, "wiki", '{"username": "u", "secret": "new"}')
    out = StringIO()
    assert cli.main(["migrate-keychain"], stdout=out) == 0
    result = json.loads(out.getvalue())
    assert result["moved"] == ["jira"] and "s3cret" not in out.getvalue()
    assert credentials.load("jira").secret == "s3cret"
    assert credentials.load("wiki").secret == "new"
    assert keyring.get_password(credentials.LEGACY_SERVICE, "jira") is not None
