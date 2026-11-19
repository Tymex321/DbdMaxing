# Contributing to DbdMaxing

Thanks for helping! Contributions of every size are welcome, from fixing a perk score to improving the scanner.

## Ways to help

- **Tier-list updates:** perk balance changes every patch. Edit [`src/DbdMaxing.Core/Data/meta.json`](src/DbdMaxing.Core/Data/meta.json) and explain your reasoning in the PR.
- **Scanner tuning:** attach screenshots (1080p, 1440p or 4K) where perks were missed or misread.
- **Bug reports:** use the issue templates and attach `%AppData%\DbdMaxing\dbdmaxing.log`.

## Development setup

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Clone the repo and run:
   ```
   dotnet build DbdMaxing.sln
   dotnet test tests/DbdMaxing.Core.Tests
   ```
3. You can run the app (`src/DbdMaxing.App`) on Windows 10 (version 2004) or later. The Core library and its tests also build and run on macOS and Linux.

## Guidelines

- **Anti-cheat safety is non-negotiable.** Never add code that opens the game process, reads or writes its memory, injects code, or inspects its network traffic. Screen capture and Steam's on-disk files are the only allowed data sources.
- Keep `DbdMaxing.Core` free of Windows-only APIs, so it stays testable everywhere.
- All styling goes through `src/DbdMaxing.App/Themes/Neon.xaml`, with no hard-coded colours in views.
- Every perk id in `meta.json` must exist in `perks.json`. A unit test enforces this.
- Add or update tests for Core changes, and make sure `dotnet test` passes before opening a PR.
- Use small, focused commits with clear messages.

## Releasing

Update `CHANGELOG.md` and the `<Version>` in `src/DbdMaxing.App/DbdMaxing.App.csproj`, then push a tag:

```
git tag v0.2.0 && git push origin v0.2.0
```

CI builds the exe and attaches it to a GitHub Release.
