#!/usr/bin/env python3
"""Tests for check.py, runnable anywhere: CI has no captures folder, so without this a broken checker would pass
every commit in silence and nothing would say so.

Each test builds a throwaway repository with a synthetic captures folder and runs the real check.py in it. The
denied values are generated here rather than written down, so this file carries no value that looks real and
the published-tree sweep has nothing to allowlist.

  python3 tools/leak-guard/test_check.py
"""

import os
import shutil
import subprocess
import sys
import tempfile
import unittest

CHECK = os.path.join(os.path.dirname(os.path.abspath(__file__)), "check.py")

HEX = "".join(f"{i:02x}" for i in range(16)) + "c0ffee"              # 38 hex digits
MAC = "".join(f"{i:02x}" for i in range(0x20, 0x26))                    # 12 hex digits
ACCOUNT = str(10 ** 18 + 424242)                                        # 19 digits
ADDRESS = (203, 0, 113, 77)                                             # a documentation address, denied here
NAME = "canary" + "box"


def dotted(o):
    return ".".join(map(str, o))


class LeakGuardTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp()
        self.git("init", "-q")
        os.makedirs(os.path.join(self.root, "tools", "leak-guard"))
        shutil.copy(CHECK, os.path.join(self.root, "tools", "leak-guard", "check.py"))
        self.captures = os.path.join(self.root, "docs", "protocol", "captures")
        os.makedirs(self.captures)
        with open(os.path.join(self.captures, "leak-denylist.tsv"), "w") as f:
            f.write("# synthetic\n")
            for category, value in (("ipv4", dotted(ADDRESS)), ("hex", HEX), ("mac", MAC),
                                    ("account-id", ACCOUNT), ("name", NAME)):
                f.write(f"{category}\t{value}\n")
        with open(os.path.join(self.root, ".gitignore"), "w") as f:
            f.write("docs/protocol/captures/\n")
        self.write("base.txt", "nothing here\n")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "base")

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def git(self, *args):
        return subprocess.run(["git", "-c", "user.name=t", "-c", "user.email=t@example.invalid",
                               "-c", "core.hooksPath=/dev/null", *args],
                              cwd=self.root, capture_output=True, text=True, check=True).stdout

    def write(self, name, text):
        with open(os.path.join(self.root, name), "w") as f:
            f.write(text)

    def check(self, *args, stdin="", env=None):
        run = subprocess.run([sys.executable, os.path.join("tools", "leak-guard", "check.py"), *args],
                             cwd=self.root, capture_output=True, text=True, input=stdin,
                             env={**os.environ, **(env or {})})
        return run.returncode, run.stderr

    def staged(self, text):
        self.write("change.txt", text)
        self.git("add", "change.txt")
        return self.check("--staged")

    # --- what must be refused, in every spelling the checker claims ---

    def test_every_spelling_is_refused(self):
        o = ADDRESS
        spellings = [
            f"host={dotted(o)}:9303",
            "Host: " + "%3d.%3d.%3d.%3d" % o + ":9295",
            "peer = [%d, %d, %d, %d]" % o,
            "{ 0x%02x, 0x%02x, 0x%02x, 0x%02x }" % o,
            "Ipv4Addr::new(%d, %d, %d, %d)" % o,
            f"key {HEX.upper()}",
            "bytes " + " ".join(HEX[i:i + 2] for i in range(0, len(HEX), 2)),
            f"prefix{HEX}suffix",
            "mac " + ":".join(MAC[i:i + 2] for i in range(0, 12, 2)),
            f'"accountId":"{ACCOUNT}"',
            f"console {NAME.upper()} in the den",
        ]
        for text in spellings:
            with self.subTest(text=text):
                code, err = self.staged(text + "\n")
                self.assertEqual(code, 1, err)
                self.assertIn("change.txt:1", err)

    def test_a_refusal_never_prints_the_value(self):
        code, err = self.staged(f"key {HEX}\n")
        self.assertEqual(code, 1)
        self.assertNotIn(HEX, err.lower())
        code, err = self.check("--staged", env={"LEAK_GUARD_SHOW": "1"})
        self.assertIn(HEX, err)

    def test_the_message_is_checked(self):
        path = os.path.join(self.root, "MSG")
        self.write("MSG", f"fix: talk to {NAME}\n\n# {HEX} in a comment line is git's, not the message\n")
        code, err = self.check("--message", path)
        self.assertEqual(code, 1, err)
        self.assertIn("message line 1", err)
        self.assertNotIn("message line 3", err)

    def test_a_push_checks_every_outgoing_commit(self):
        self.write("leak.txt", f"{dotted(ADDRESS)}\n")
        self.git("add", "leak.txt")
        self.git("commit", "-q", "-m", "carries it")
        head = self.git("rev-parse", "HEAD").strip()
        code, err = self.check("--push", "origin", stdin=f"refs/heads/x {head} refs/heads/x {'0' * 40}\n")
        self.assertEqual(code, 1, err)
        self.assertIn("leak.txt:1", err)
        code, err = self.check("--history")
        self.assertEqual(code, 1, err)

    # --- what must pass ---

    def test_synthetic_and_ordinary_values_pass(self):
        code, err = self.staged("10.0.0.7 and 192.0.2.1, [172, 31, 0, 1], video_packet(10, 0, 2, 0, 0)\n"
                                "a 19-digit 1234567890123456789 and the word canary alone\n")
        self.assertEqual(code, 0, err)

    def test_an_exception_takes_effect_at_once(self):
        code, _ = self.staged(f"console {NAME}\n")
        self.assertEqual(code, 1)
        code, err = self.check("--allow", "name", NAME, "a synthetic fixture")
        self.assertEqual(code, 0, err)
        code, err = self.check("--staged")
        self.assertEqual(code, 0, err)
        code, _ = self.check("--allow", "not-a-category", NAME, "x")
        self.assertEqual(code, 2)

    def test_a_clone_without_the_captures_folder_is_not_checked(self):
        shutil.rmtree(self.captures)
        code, err = self.staged(f"key {HEX}\n")
        self.assertEqual(code, 0, err)

    def test_a_captures_folder_without_a_denylist_refuses(self):
        os.remove(os.path.join(self.captures, "leak-denylist.tsv"))
        code, err = self.staged("anything\n")
        self.assertEqual(code, 1)
        self.assertIn("build-denylist.py", err)


if __name__ == "__main__":
    unittest.main(verbosity=2)
