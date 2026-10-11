# DynamicBay – rules for coding agents

A Dynamic Island for Windows: C# 13 / .NET 9, WPF, one app project `src/DynamicBay`, xUnit tests in
`tests/DynamicBay.Tests`. Read `docs/ARCHITECTURE.md` first, `docs/DEVELOPMENT.md` for builds and screenshots.
The owner speaks German; UI texts exist in German and English (`core:L De=… En=…`, `Core/Loc.cs`).

Keep this file short and current. Update it in the same commit when the structure or a rule changes.

## Build and test

```powershell
dotnet build DynamicBay.sln -c Release
dotnet test tests/DynamicBay.Tests -c Release
$env:DYNAMICBAY_SNAPSHOT="settings"; powershell -File tools/snapshot.ps1   # screenshots of all settings pages
```

Every fix comes with a test. UI changes are checked with a screenshot (snapshot mode), not by assumption.

## Never

- Start, stop, restart or reinstall the installed app (`C:\Program Files\DynamicBay`). The owner installs updates
  through the in-app prompt. Never install anything automatically from the app either: updates ask first.
- Run `tools/update-smoke.ps1` on the owner's PC. It installs and removes the app; it is for CI or a VM.
- Play music, or write to real accounts (Spotify, Teams/Graph, To Do, calendars) in tests or live checks. Use the
  snapshot mode or fixtures. Tests never call external services.
- Log tokens, passwords, cookies or authorization headers.
- Lower the frame rate of visible animations to save CPU or RAM (the owner rejected it twice). Stop hidden
  animations instead, and respect the setting Darstellung → Bildrate.
- Register the identity package from the running app with `DeferRegistrationWhenPackagesAreInUse`. That broke the
  start of every installed copy after an update (docs/postmortems/2026-10-11-identity-package.md). The installer
  registers it while the app is closed.
- Push, tag or publish a release without the owner asking for it.

## Design

- No emojis anywhere (UI, docs, commits). Icons only as geometry: Lucide, Simple Icons, own glyphs in
  `Themes/Icons.Custom.xaml`.
- Motion like Apple: springs (`Motion/`), no linear fades for size changes.
- Rounded corners are concentric, no oval buttons (chips: `Btn.Chip`, radius 10).
- Settings descriptions and hints: one short sentence. Details go to `docs/`.
- Error messages for users: short, localized, without technical codes (`Core/ErrorText.cs`); details go to the log.

## Code

- Match the surrounding code: comment density, naming, small static helpers for pure logic (see `PlacementMath`)
  so it can be tested without a window.
- Parse external JSON tolerantly (`JsonNode`, missing or null fields are normal) and keep a fixture for every shape
  seen in the wild (`tests/DynamicBay.Tests/Fixtures/Api`).
- Every animation or state change that sets hit testing, capture, Topmost or timers must undo it on every path,
  including when it is interrupted.
- Third-party code, icons or fonts: add the license text to `THIRD-PARTY-NOTICES.txt`.

## Releases

Version in `src/DynamicBay/DynamicBay.csproj`, tag `v<version>`. `release.yml` tests, builds, runs the update smoke
test (older releases → new installer, app must start with identity) and only then publishes. The identity package
is signed with the fixed certificate from the secrets `SIGNING_PFX_BASE64` / `SIGNING_PFX_PASSWORD`
(`tools/new-signing-cert.ps1`).
