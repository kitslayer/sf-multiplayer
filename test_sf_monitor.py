#!/usr/bin/env python3
"""Unit tests for sf-monitor.py's log-tail input validation.

Pins _LOG_BRIDGE_RE — the validator that keeps the client-supplied ?bridge=
query param out of the /tmp/sf-oracle-{plugin,unity}-{bridge}.log path built in
Handler._serve_log. A loosening (or the classic $-vs-\\Z anchor slip, which lets
a trailing newline through) would reopen a path-injection vector.

Sibling of test_serve_lobbies.py; same import-by-path trick (sf-monitor.py has a
hyphen → not importable by name) and same __name__-gated main(), so importing
runs only module-level defs/constants — it does NOT start the sampler or server.

Run: python3 -m unittest test_sf_monitor -v   (stdlib only, no deps)
"""
import importlib.util
import os
import unittest

_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                     "monitoring", "sf-monitor.py")
_SPEC = importlib.util.spec_from_file_location("sf_monitor", _PATH)
mon = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(mon)


class TestLogBridgeRegex(unittest.TestCase):
    def test_accepts_4_to_6_digits(self):
        # Real bridges are the lobby's UDP port (e.g. 1337); 4..6 digits covers
        # the whole valid port range without admitting anything else.
        for good in ("1337", "1338", "8080", "12345", "123456", "9999"):
            self.assertTrue(_match(mon, good), f"should accept {good!r}")

    def test_rejects_wrong_length_and_non_digits(self):
        for bad in ("", "123", "1234567", "12ab", "abcd", "0x10", "13.7"):
            self.assertFalse(_match(mon, bad), f"should reject {bad!r}")

    def test_rejects_trailing_newline(self):
        # The $-vs-\Z pin: re's $ also matches just before a trailing newline, so
        # "1337\n" sails through ^\d{4,6}$ and lands a stray \n in the log path.
        # \Z is end-of-string only — this case is the red→green proof of the fix.
        for bad in ("1337\n", "1337\r\n", "12345\n"):
            self.assertFalse(_match(mon, bad), f"should reject {bad!r}")

    def test_rejects_path_and_shell_metachars(self):
        for bad in ("../etc", "1337/x", "13/37", " 1337", "1337 ", "1337;rm"):
            self.assertFalse(_match(mon, bad), f"should reject {bad!r}")


def _match(mon, s):
    return mon._LOG_BRIDGE_RE.match(s) is not None


class TestClampInt(unittest.TestCase):
    """Handler._clamp_int hardens the untrusted ?n= query param. A bare int()
    (the old code) raised ValueError on ?n=abc and 500'd the request inside
    ThreadingHTTPServer; this pins the graceful clamp-and-fallback instead."""
    def _c(self, raw, default=240, hi=2880):
        return mon.Handler._clamp_int(raw, default, hi)

    def test_valid_passthrough(self):
        self.assertEqual(self._c("100"), 100)
        self.assertEqual(self._c("0"), 0)

    def test_clamps_to_hi(self):
        self.assertEqual(self._c("99999"), 2880)

    def test_clamps_negative_to_zero(self):
        self.assertEqual(self._c("-5"), 0)

    def test_non_numeric_falls_back_to_default_without_raising(self):
        # The red→green case: each of these would raise on a bare int().
        for bad in ("abc", "", "1.5", "0x10", "12;rm", None):
            self.assertEqual(self._c(bad), 240, f"{bad!r} should fall back")


if __name__ == "__main__":
    unittest.main()
