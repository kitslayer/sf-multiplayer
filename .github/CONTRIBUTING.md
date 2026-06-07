# Contributing to sf-multiplayer

Thanks for helping improve the Stick Fight dedicated-server project!

## Ground rules
- Be respectful — see the [Code of Conduct](CODE_OF_CONDUCT.md).
- `main` is protected: every change lands via pull request and needs maintainer
  (@kitslayer) review. Direct pushes and force-pushes to `main` are blocked.

## Project layout
See the "What's in this repo" table in the [README](../README.md) and
`notes/ARCHITECTURE.md`.

## Building the plugins
`sf-headless-host` (server) and `sf-client-recon` (client) are .NET 4.6 BepInEx 5
assemblies. You must supply the reference DLLs locally (not shipped, for copyright):
each plugin's `refs/` needs `Assembly-CSharp.dll` + `UnityEngine.dll` (from your Stick
Fight install) and `BepInEx.dll` + `0Harmony.dll` (from BepInEx 5.4.x). Then run
`bash setup-all.sh`. The Go router (`sf-router/`) is tested with `go test -race ./...`.

## Pull requests
1. Branch off `main` (or the active feature branch).
2. Keep changes focused; match the surrounding code style.
3. If you touch the installer or client, **test on Windows** (install -> play ->
   uninstall) — the maintainer develops on Linux and can't always verify Windows.
4. Update docs (`WHATS_NEW.md` / `NEXT_STEPS.md` / `notes/`) when behavior changes.
5. Open the PR, fill out the template, and wait for review.

## Reporting
Use the issue templates for bugs and feature requests. For security-sensitive
problems, do **not** open a public issue — see [SECURITY.md](SECURITY.md).
