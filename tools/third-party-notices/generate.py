#!/usr/bin/env python3
"""Write THIRD-PARTY-NOTICES.txt for the dotnet client from the packages it actually ships.

Reads the app's resolved packages (`dotnet list package --include-transitive --format json`), plus the native
packages the interop projects reference, and takes each one's licence from its own package in the NuGet cache:
the SPDX expression when the nuspec has one, the bundled licence file when it has that instead. Nothing here is
typed by hand, so the notices cannot drift from what ships. Run it again whenever a package changes:

    python3 tools/third-party-notices/generate.py

Build-only packages (SDK build tools, C++/WinRT's code generator) put nothing in the output and are left out.
"""

import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "src" / "Ripcord.App" / "Ripcord.App.csproj"
NATIVE = [ROOT / "src" / "Ripcord.Input.Interop" / "Ripcord.Input.Interop.vcxproj",
          ROOT / "src" / "Ripcord.Media.Interop" / "Ripcord.Media.Interop.vcxproj"]
OUT = ROOT / "THIRD-PARTY-NOTICES.txt"
CACHE = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))

# Packages that only run during the build.
BUILD_ONLY = re.compile(r"^(Microsoft\.Windows\.SDK\.BuildTools.*|Microsoft\.Windows\.CppWinRT)$", re.IGNORECASE)


def app_packages():
    out = subprocess.run(["dotnet", "list", str(APP), "package", "--include-transitive", "--format", "json"],
                         capture_output=True, text=True, check=True).stdout
    found = {}
    for project in json.loads(out)["projects"]:
        for framework in project.get("frameworks", []):
            for key in ("topLevelPackages", "transitivePackages"):
                for p in framework.get(key, []):
                    found[p["id"]] = p.get("resolvedVersion") or p.get("requestedVersion")
    return found


def native_packages():
    found = {}
    for proj in NATIVE:
        text = proj.read_text(encoding="utf-8")
        for m in re.finditer(r'<PackageReference Include="([^"]+)">\s*<Version>([^<]+)</Version>', text):
            found[m.group(1)] = m.group(2)
        for m in re.finditer(r'<PackageReference Include="([^"]+)" Version="([^"]+)"', text):
            found[m.group(1)] = m.group(2)
    return found


# The two SPDX licences the shipped packages name, whose terms require their text to travel with the binaries.
# The packages carry the identifier only, so the standard text is filled in with each package's own copyright.
SPDX_TEXT = {
    "MIT": """{copyright}

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of
the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.""",
    "BSD-3-Clause": """{copyright}

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the
   following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the
   following disclaimer in the documentation and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote
   products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE
USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.""",
}


def describe(name, version):
    folder = CACHE / name.lower() / version.lower()
    nuspecs = list(folder.glob("*.nuspec"))
    if not nuspecs:
        return None, None, f"(not in the NuGet cache at {folder}; restore first)"
    tree = ET.parse(nuspecs[0])
    ns = {"n": tree.getroot().tag.split("}")[0].strip("{")}
    meta = tree.getroot().find("n:metadata", ns)
    authors = (meta.findtext("n:authors", default="", namespaces=ns) or "").strip()
    copyright_ = (meta.findtext("n:copyright", default="", namespaces=ns) or "").strip()
    licence = meta.find("n:license", ns)
    header = " / ".join(x for x in (authors, copyright_) if x)
    if licence is not None and licence.get("type") == "expression":
        expression = licence.text.strip()
        template = SPDX_TEXT.get(expression)
        text = template.format(copyright=copyright_ or f"Copyright (c) {authors}") if template else None
        return header, expression, text
    if licence is not None and licence.get("type") == "file":
        path = folder / licence.text.strip()
        if path.exists():
            return header, "see licence text below", path.read_text(encoding="utf-8", errors="replace").strip()
    return header, "unknown", f"(no licence metadata in {nuspecs[0].name})"


def main():
    packages = {**app_packages(), **native_packages()}
    shipped = sorted((n, v) for n, v in packages.items() if not BUILD_ONLY.match(n))

    lines = [
        "Ripcord: third-party notices for the dotnet client (Windows)",
        "",
        "Ripcord is licensed under Apache-2.0 (see LICENSE). It ships the following third-party components,",
        "each under its own licence. Generated by tools/third-party-notices/generate.py from each package's own",
        "metadata; do not edit by hand.",
        "",
        "A self-contained release also ships the .NET runtime, which is MIT-licensed by the .NET Foundation and",
        "carries its own notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT",
        "",
    ]
    texts = []
    for name, version in shipped:
        header, licence, text = describe(name, version)
        lines.append(f"- {name} {version}: {licence or 'unknown'}" + (f" ({header})" if header else ""))
        if text:
            texts.append((name, version, text))

    for name, version, text in texts:
        lines += ["", "=" * 100, f"{name} {version}", "=" * 100, "", text]

    OUT.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {OUT.relative_to(ROOT)}: {len(shipped)} packages, {len(texts)} licence texts")
    return 0


if __name__ == "__main__":
    sys.exit(main())
