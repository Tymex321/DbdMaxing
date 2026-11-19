# Security Policy

## Anti-cheat stance

DbdMaxing is designed so it can't trigger Easy Anti-Cheat. It **only**:

- takes screenshots of the screen area covered by the game window (GDI BitBlt, as OBS and Discord do),
- lists running processes by name,
- reads Steam's own text files on disk (`loginusers.vdf`, `libraryfolders.vdf`, `appmanifest_381210.acf`).

It never opens a handle to the game process, never reads or writes game memory, never injects DLLs, and never intercepts or modifies network traffic. Any change that breaks this rule is treated as a security bug.

## Reporting a vulnerability

Please **don't** open a public issue for security problems. Use [GitHub private vulnerability reporting](https://github.com/Tymex321/DbdMaxing/security/advisories/new) instead. You'll get a response within a week.

## Supported versions

Only the latest release receives fixes.
