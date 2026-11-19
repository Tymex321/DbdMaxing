# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0] - 2026-11-19

### Added
- Detects Dead by Daylight starting and stopping, with tray notifications.
- Detects the Steam account automatically from `loginusers.vdf`, with owned perks stored separately for each account.
- A screen-capture perk scanner (OpenCV icon matching) that is anti-cheat safe because it never reads game memory.
- A best-effort DLC hint from local Steam app manifests.
- A build engine that scores perk tiers, synergies, anti-synergies, tag limits and role coverage.
- Killer-specific build tuning.
- A bundled database of 321 perks and 98 characters, refreshed from the dbd.tricky.lol API.
- An editable tier list (`meta.json`) that `meta.override.json` can override.
- A futuristic black and dark-purple WPF interface with survivor builds, killer builds, My Perks and Settings pages.
- Start with Windows, close to tray and single-instance behaviour.
- GitHub Actions CI that produces a self-contained single-file `DbdMaxing.exe`.

[Unreleased]: https://github.com/Tymex321/DbdMaxing/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Tymex321/DbdMaxing/releases/tag/v0.1.0
