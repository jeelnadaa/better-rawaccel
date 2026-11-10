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
- [ ] Gain and velocity plots (the original had these as extra charts)
- [ ] Offline fonts (bundle the OFL faces; Fontshare faces stay online-only for licence reasons)
- [ ] Live marker that respects per-device DPI normalisation and disabled devices

## MCP (inside the running app, not headless)
- [x] HTTP MCP server on 127.0.0.1 hosted by the GUI process; every change shows in the window
- [x] Requests with a foreign `Origin` are refused (checked: 403); on/off switch and port in the Driver sheet
- [x] 30 tools covering everything the UI can do, including `capture_window` and `show_panel`
- [x] Keeps the driver's 1-second write delay (a write takes about 2 s end to end)
- [~] Turning the server off and changing its port from the sheet — not tried

## Releases and updates
- [~] GitHub Actions workflow: every push to `main` builds and publishes a release zip — written, never run (nothing is pushed yet)
- [~] Zip includes the driver, installer, uninstaller and writer; the workflow fails if any is missing
- [~] NuGet cache in the workflow
- [~] SHA-256 file published next to the zip
- [~] In-app updater — the check runs and handles "no releases yet"; download, hash check and file swap can only be tried once a release exists

## Repo
- [x] `README.md` with usage, MCP setup, build steps
- [x] `CREDITS.md` and `LICENSE` crediting Raw Accel and its authors (MIT)
- [x] `.gitignore` keeps personal files and the upstream clone out
- [x] First commit
- [ ] Push to GitHub (waits for the owner's go-ahead; the first push publishes the first release)
