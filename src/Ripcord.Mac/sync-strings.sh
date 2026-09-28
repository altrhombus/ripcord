#!/bin/sh
# Brings Shared/Localizable.xcstrings up to date with the strings the app and the widget extension use.
#
# Xcode's editor does this on every build; a command-line build only extracts the keys, into .stringsdata
# files, and leaves the catalog alone. This builds, then merges what was extracted, marking strings no longer
# in the source as stale rather than deleting their translations.
set -eu
cd "$(dirname "$0")"
xcodebuild -project Ripcord.xcodeproj -scheme Ripcord -configuration Debug -derivedDataPath build/dd build -quiet
set --
for f in $(find build/dd/Build/Intermediates.noindex -name '*.stringsdata' \
             -path '*/Ripcord.build/*' -o -name '*.stringsdata' -path '*/RipcordWidgets.build/*'); do
  set -- "$@" --stringsdata "$f"
done
xcrun xcstringstool sync Shared/Localizable.xcstrings "$@"
echo "$(xcrun xcstringstool print Shared/Localizable.xcstrings | wc -l | tr -d ' ') strings in Shared/Localizable.xcstrings"
