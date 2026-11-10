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
- [x] Fourteen themes: 6 light (Paper, Ledger, Parchment, Millimeter, Technical, Chalk) and 8 dark (Blueprint, Graphite, Phosphor, Obsidian, Midnight, Radar, Cyberpunk, Solarized) with high-contrast elevated dropdowns
- [x] Three typefaces: Satoshi, Cabinet Grotesk, Archivo; IBM Plex Mono for numbers
- [x] Callout tooltips and the "How to use" sheet — with backdrop blur/scrim and elevated popover surface
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
- [x] NuGet cache in the workflow (the second run restored it)
- [x] SHA-256 file published next to the zip, and it matches
- [x] In-app updater: a released 2.0.1 found 2.0.2 at start-up, downloaded it, passed the hash check, swapped its files, restarted as 2.0.2 and removed the staging folder. Not tried: installing on close instead of at once, and an app folder that is not writable

## Repo
- [x] `README.md` with usage, MCP setup, build steps
- [x] `CREDITS.md` and `LICENSE` crediting Raw Accel and its authors (MIT)
- [x] `.gitignore` keeps personal files and the upstream clone out
- [x] First commit
- [x] Pushed; releases v2.0.1 and v2.0.2 are public

## Ideas and new additions
- [x] Speed histogram under the curve: live hand-speed recording, distribution stats (median, p90, peak), canvas overlay with toggle, and MCP tools (`get_speed_histogram`, `reset_speed_histogram`)
- [x] Draw the table curve by dragging points on the plot: interactive direct manipulation on the canvas for LUT mode (drag points, double-click to add, right-click to delete)
- [x] Pin a reference curve, or overlay two profiles, to compare: dashed baseline curve on canvas, legend indicator, and MCP tools (`pin_reference_curve`, `clear_reference_curve`, `get_reference_curve`)
- [x] cm/360 and sensitivity converter: interactive sheet in title block, live readout, multi-game yaw conversions (CS2/Source/Apex, Valorant, Overwatch 2, Fortnite, Rainbow Six Siege), and MCP tools (`calculate_cm360`, `convert_sensitivity`)
- [x] Share a profile as a short code, and export the plot as an image: compact shareable `RA1:...` codes with UI buttons (`export_profile_code`, `import_profile_code`) and PNG export (`export_plot_image`)
- [x] 14 light and dark themes: Paper, Ledger, Parchment, Millimeter, Technical, Chalk, Blueprint, Graphite, Phosphor, Obsidian, Midnight, Radar, Cyberpunk, Solarized
- [x] High-contrast backdrop scrim and elevated styling for all dropdowns, modal sheets, and "How to use" notes
- [x] Automated tests for the engine: `tests/BetterRawAccel.Tests` with 10 passing unit tests running in `dotnet test`
- [ ] Per-game profiles: switch profile when a chosen game is in the foreground
- [ ] Tray icon and global hotkeys for switching profiles
- [ ] ARM64 build, code-signed releases, winget package
