# Credits

## Raw Accel

Better Raw Accel is a new interface for [Raw Accel](https://github.com/RawAccelOfficial/rawaccel).
Everything that actually changes your mouse input is their work:

- the kernel driver (`rawaccel.sys`), shipped here **unchanged and with its original signature**
- the acceleration math and every acceleration mode
- the `settings.json` format
- `wrapper.dll`, the bridge this app uses to talk to the driver and to compute curves
- `installer.exe`, `uninstaller.exe` and `writer.exe`
- the guide and FAQ in [`doc/`](doc/)

Raw Accel authors, from the original project's credits:

- **simon (a1xd)** — driver and acceleration logic
- **\_m00se\_** — GUI, gain features, acceleration types
- **Sidiouth** — primary tester and sounding board
- **TauntyArmordillo** — originator of the alternate curve ideas (Natural and Motivity types)
- **Kovaak** — brought the team together
- and the contributors to the Raw Accel repository

Raw Accel is released under the MIT License, Copyright (c) 2020 a1xd.
The full text is in [`vendor/rawaccel/LICENSE`](vendor/rawaccel/LICENSE) and ships with every release as `licenses/RawAccel-LICENSE.txt`.

The binaries in [`vendor/rawaccel/x64`](vendor/rawaccel/x64) come from the official Raw Accel v1.7.1 release.
If you want to build them yourself, the source is in the Raw Accel repository.

## What is new here

The host app in `src/BetterRawAccel` (window, interface, MCP server, updater) was written for this project.
It replaces Raw Accel's `rawaccel.exe` GUI and nothing else.

## Other components

- [Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) — renders the interface
- [Newtonsoft.Json](https://www.newtonsoft.com/json) — MIT License
- Typefaces, loaded from their foundries' servers at run time and not redistributed with the app:
  - Satoshi and Cabinet Grotesk — Indian Type Foundry, via Fontshare (ITF Free Font License)
  - Archivo — Omnibus-Type (SIL Open Font License 1.1)
  - IBM Plex Mono — IBM (SIL Open Font License 1.1)
