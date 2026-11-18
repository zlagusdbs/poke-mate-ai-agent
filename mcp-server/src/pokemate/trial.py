import datetime as dt
import glob
import json
import os

ME = {"name": "sample-user", "key": "sample-user", "displayName": "Sample User"}


def trial_dir():
    path = os.environ.get("POKEMATE_TRIAL")
    return os.path.expanduser(path) if path else None


def overlay(name):
    root = trial_dir()
    if not root:
        return {}
    path = os.path.join(root, f"{name}.json")
    if not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as f:
        return json.load(f)


class FakeJira:
    base_url = "https://jira.example.com"

    def __init__(self, root):
        self.root = os.path.join(root, "jira")

    def _path(self, key):
        return os.path.join(self.root, f"{key}.json")

    def _load(self, key):
        path = self._path(key)
        if not os.path.exists(path):
            raise SystemExit(f"{key}: 시험 이슈 파일이 없어요")
        with open(path, encoding="utf-8") as f:
            return json.load(f)

    def _save(self, key, data):
        with open(self._path(key), "w", encoding="utf-8") as f:
            json.dump(data, f, ensure_ascii=False, indent=1)

    def me(self):
        return dict(ME)

    def field_id(self, name):
        return "customfield_epic" if name == "Epic Link" else None

    def get_issue(self, key):
        return self._load(key)["issue"]

    def search_raw(self, jql, max_results=50, fields="*navigable"):
        rows = []
        for path in sorted(glob.glob(os.path.join(self.root, "*.json")))[:max_results]:
            with open(path, encoding="utf-8") as f:
                rows.append(json.load(f)["issue"])
        return rows

    def comments(self, key):
        return list(self._load(key).get("comments") or [])

    def add_comment(self, key, body):
        data = self._load(key)
        comments = data.setdefault("comments", [])
        comment = {"id": str(len(comments) + 1), "author": dict(ME), "created": dt.datetime.now().isoformat(timespec="seconds"), "body": body}
        comments.append(comment)
        self._save(key, data)
        return comment

    def remote_links(self, key):
        return list(self._load(key).get("remoteLinks") or [])

    def transitions(self, key):
        return list(self._load(key).get("transitions") or [])

    def transition(self, key, transition_id):
        data = self._load(key)
        found = next((t for t in data.get("transitions") or [] if str(t.get("id")) == str(transition_id)), None)
        if found is None:
            raise SystemExit(f"{key}: transition {transition_id} 없음")
        data["issue"].setdefault("fields", {})["status"] = {"name": (found.get("to") or {}).get("name")}
        data.setdefault("transitioned", []).append((found.get("to") or {}).get("name"))
        self._save(key, data)
