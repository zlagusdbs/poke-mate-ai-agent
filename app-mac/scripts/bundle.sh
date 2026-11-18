#!/bin/zsh
# PokeMate.app 만들기: 릴리스 빌드 → .app 구조로 복사 → ad-hoc 서명
set -euo pipefail
cd "$(dirname "$0")/.."
swift build -c release --product PokeMate
APP=build/PokeMate.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$(swift build -c release --show-bin-path)/PokeMate" "$APP/Contents/MacOS/PokeMate"
cp Resources/Info.plist "$APP/Contents/Info.plist"
LOCAL_BUNDLE=../local/bundle.json
[ -f "$LOCAL_BUNDLE" ] || LOCAL_BUNDLE=bundle.local.json
if [ -f "$LOCAL_BUNDLE" ]; then
  ID=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("bundleId", ""))' "$LOCAL_BUNDLE")
  [ -n "$ID" ] && /usr/libexec/PlistBuddy -c "Set :CFBundleIdentifier $ID" "$APP/Contents/Info.plist"
fi
codesign --force --sign - "$APP"
echo "만들었어요: $PWD/$APP"
