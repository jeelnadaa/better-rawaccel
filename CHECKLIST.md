# Better Raw Accel — checklist

Status key:

- `[x]` done and checked on a real machine with the Raw Accel driver installed
- `[~]` written, but not exercised yet (the note says what is missing)
- `[ ]` not done

Checks were made by driving the running app through its MCP server and reading window captures. Mouse dragging, hovering and keyboard shortcuts in the window have not been tried by hand yet.

## Foundations
- [x] New host app (`src/BetterRawAccel`, .NET Framework 4.8, WebView2) reusing the signed driver and `wrapper.dll`
- [~] Friendly start-up when the driver is missing, with a button that runs the bundled installer — not tried, the test PC has the driver
- [x] Single instance
- [~] Crash log that appends under `%LOCALAPPDATA%\BetterRawAccel` — no crash has happened to prove it
- [x] Per-monitor DPI awareness (checked at 150 % scaling)

## Design (approved prototype: `design/prototype.html`)
- [x] Engineering-paper layout, title block, plot, readouts
- [x] Five themes: Paper, Ledger, Blueprint, Graphite, Phosphor (Paper, Graphite and Blueprint seen in the app)
- [x] Three typefaces: Satoshi, Cabinet Grotesk, Archivo; IBM Plex Mono for numbers (Satoshi and Archivo seen in the app)
- [~] Callout tooltips and the "How to use" sheet — carried over from the prototype, not hovered in the real app
- [~] Curve morphing, write re-trace, reduced-motion support — only still captures were checked
- [x] Live marker driven by real mouse input
- [x] Parts the prototype did not have, in the same style: profiles, devices, driver & app sheet, output and advanced sections, table editor, notices

## Fixes for problems found in the original GUI
- [x] Curve comes from the driver's own math (`wrapper.dll`) in one place, not three copies of one loop
- [x] Live preview while editing; no write needed just to see the curve
- [x] Window size and position are restored, and ignored if that monitor is gone
- [x] Theme switching works without a restart
- [x] Settings are written to a temp file and swapped in
- [x] Invalid values are rejected with a readable message; nothing changes
- [x] No leftover debug output
- [~] Options that were JSON-only now have controls: angle snapping, speed cap, L/R and U/D multipliers, smoothing half-lives — the values work over MCP; the controls themselves were not clicked

## New features
- [x] Driver-vs-editing comparison (dashed curve) and unwritten-change count
- [x] Multiple profiles: create, duplicate, rename, delete, select
- [x] Per-device settings: DPI, polling rate, leave alone, profile assignment
- [x] X and Y apart, with both curves on the plot
- [x] Undo, redo, discard unwritten changes
- [x] Import and export of settings (over MCP; the file dialogs were not opened)
- [~] Write saved settings when the app starts — not tried
- [~] Apply at Windows sign-in using the bundled `writer.exe` — not tried, it would add a shortcut to the Startup folder
- [x] Peak-speed hold
- [x] Table (lookup) mode editor, with values as sensitivity or output speed
- [x] Velocity and gain plots, switched at the top right of the plot or with `set_app_settings`
- [x] Offline fonts: Archivo and IBM Plex Mono are bundled; Satoshi and Cabinet Grotesk stay online-only for licence reasons and fall back to Archivo
- [~] Live marker ignores mice the driver leaves alone and mice on another profile; DPI normalisation needs no special handling — the marker still works for the normal case, but the left-alone and other-profile cases were not set up and tried

## MCP (inside the running app, not headless)
- [x] HTTP MCP server on 127.0.0.1 hosted by the GUI process; every change shows in the window
- [x] Requests with a foreign `Origin` are refused (checked: 403); on/off switch and port in the Driver sheet
- [x] 30 tools covering everything the UI can do, including `capture_window` and `show_panel`
- [x] Connection guide for Claude Code, Claude Desktop, Cursor, VS Code, Gemini CLI: `doc/MCP.md` (the Claude Desktop bridge and the non-Claude configs are documented but were not tried)
- [x] Keeps the driver's 1-second write delay (a write takes about 2 s end to end)
- [~] Turning the server off and changing its port from the sheet — not tried

## Releases and updates
- [x] GitHub Actions workflow: every push to `main` builds and publishes a release zip (v2.0.1 was published this way)
- [x] Zip includes the driver, installer, uninstaller and writer; the driver in the v2.0.1 zip is byte-identical to upstream's signed file
- [~] NuGet cache in the workflow — the first run saved it; a cache hit on a later run has not been looked at
- [x] SHA-256 file published next to the zip, and it matches
- [~] In-app updater — the check runs and handles "no releases yet"; download, hash check and file swap can only be tried once a release exists

## Repo
- [x] `README.md` with usage, MCP setup, build steps
- [x] `CREDITS.md` and `LICENSE` crediting Raw Accel and its authors (MIT)
- [x] `.gitignore` keeps personal files and the upstream clone out
- [x] First commit
- [x] Pushed; release v2.0.1 is public

## Ideas, not started
- [ ] Per-game profiles: switch profile when a chosen game is in the foreground
- [ ] Tray icon and global hotkeys for switching profiles
- [ ] Speed histogram under the curve: where your hand actually spends its time
- [ ] Draw the table curve by dragging points on the plot
- [ ] Pin a reference curve, or overlay two profiles, to compare
- [ ] cm/360 and sensitivity converter
- [ ] Share a profile as a short code, and export the plot as an image
- [ ] Automated tests for the engine, run in the workflow
- [ ] ARM64 build, code-signed releases, winget package
