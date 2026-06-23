#!/usr/bin/env python3
"""Unit tests for serve-lobbies.py control-plane validation + static-lobby parsing.

The project's first Python test. Pins two pieces of the lobby control plane that
are easy to regress and costly to get wrong:
  - LOBBY_CODE_RE — the validator that keeps client-supplied lobby codes out of
    the shell / registry file paths (a loosening would reopen an injection vector).
  - _static_lobbies() — parses SF_STATIC_LOBBIES (e.g. "MAIN:1337") so the
    always-on MAIN lobby shows in the browser; a parse regression hides it.

Run: python3 -m unittest test_serve_lobbies -v   (stdlib only, no deps)
"""
import importlib.util
import os
import unittest

# serve-lobbies.py has a hyphen → not importable by name; load it by path. Its
# main() is __name__-gated, so importing runs only module-level defs/constants —
# it does NOT start the HTTP server or the reaper thread.
_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "serve-lobbies.py")
_SPEC = importlib.util.spec_from_file_location("serve_lobbies", _PATH)
sl = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(sl)


class TestLobbyCodeRegex(unittest.TestCase):
    def test_accepts_valid_codes(self):
        # 1..16 chars of A-Z0-9 (matches the router's maxCodeLen).
        for code in ("A", "AAAA", "MAIN", "OK2", "A1B2C3D4E5F6G7H8"):
            self.assertTrue(sl.LOBBY_CODE_RE.match(code), f"should accept {code!r}")

    def test_rejects_injection_and_malformed(self):
        # Empty, lowercase, spaces, path-traversal, shell metachars, newline, >16.
        for bad in ("", "ab", "aaaa", "A B", "A;B", "A/B", "A.B", "../etc",
                    "'; rm -rf /", "MAIN\n", "A1B2C3D4E5F6G7H8X"):
            self.assertIsNone(sl.LOBBY_CODE_RE.match(bad), f"should reject {bad!r}")


class TestStaticLobbies(unittest.TestCase):
    def _parse(self, value):
        old = sl.STATIC_LOBBIES
        try:
            sl.STATIC_LOBBIES = value
            return sl._static_lobbies()
        finally:
            sl.STATIC_LOBBIES = old

    def test_code_and_port(self):
        out = self._parse("MAIN:1337")
        self.assertEqual(len(out), 1)
        self.assertEqual(out[0]["code"], "MAIN")
        self.assertEqual(out[0]["port"], "1337")
        self.assertTrue(out[0]["alive"])
        self.assertEqual(out[0]["static"], "true")

    def test_default_port_and_uppercasing(self):
        out = self._parse("main")  # no port, lowercase
        self.assertEqual(len(out), 1)
        self.assertEqual(out[0]["code"], "MAIN")   # uppercased
        self.assertEqual(out[0]["port"], "1337")   # default when omitted

    def test_bad_port_falls_back_to_default(self):
        out = self._parse("FOO:notaport")
        self.assertEqual(out[0]["code"], "FOO")
        self.assertEqual(out[0]["port"], "1337")

    def test_skips_blanks_and_invalid_codes(self):
        out = self._parse("MAIN:1337, , BAD CODE!, OK2:1338")
        self.assertEqual([l["code"] for l in out], ["MAIN", "OK2"])

    def test_empty_env_yields_nothing(self):
        self.assertEqual(self._parse(""), [])


if __name__ == "__main__":
    unittest.main()
