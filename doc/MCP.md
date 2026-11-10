# Connecting an AI tool to Better Raw Accel

Better Raw Accel hosts a [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server inside its window. Once your AI tool is connected, you can say things like "make my curve flatter above 40 counts/ms" and watch the window change.

## Before you start

1. **Start `BetterRawAccel.exe` and leave it open.** The server only exists while the window is running. There is no background service.
2. Open **Driver** in the title block and look at **MCP server**. It should say `listening`.
3. Note the address shown there. Unless you changed the port it is:

   ```
   http://127.0.0.1:47700/mcp
   ```

The server speaks MCP's **Streamable HTTP** transport. It is reachable from this computer only.

## Claude Code

```bash
claude mcp add --transport http rawaccel http://127.0.0.1:47700/mcp
```

Check it with `claude mcp list`, or type `/mcp` inside Claude Code. Add `--scope user` to have it in every project.

## Claude Desktop

Claude Desktop starts local MCP servers as programs, so it needs a small bridge to reach an HTTP address. The usual one is [`mcp-remote`](https://www.npmjs.com/package/mcp-remote), which needs [Node.js](https://nodejs.org).

Open **Settings → Developer → Edit Config** and add:

```json
{
  "mcpServers": {
    "rawaccel": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://127.0.0.1:47700/mcp"]
    }
  }
}
```

Restart Claude Desktop. This route has not been tested by this project yet; if it fails, see [Troubleshooting](#troubleshooting).

## Cursor

Create `.cursor/mcp.json` in a project, or `~/.cursor/mcp.json` for all projects:

```json
{
  "mcpServers": {
    "rawaccel": {
      "url": "http://127.0.0.1:47700/mcp"
    }
  }
}
```

## VS Code (GitHub Copilot agent mode)

Create `.vscode/mcp.json`:

```json
{
  "servers": {
    "rawaccel": {
      "type": "http",
      "url": "http://127.0.0.1:47700/mcp"
    }
  }
}
```

## Gemini CLI

In `~/.gemini/settings.json`:

```json
{
  "mcpServers": {
    "rawaccel": {
      "httpUrl": "http://127.0.0.1:47700/mcp"
    }
  }
}
```

## Other tools

Anything that supports MCP over Streamable HTTP works with the same address. Pick "HTTP" or "Streamable HTTP" as the transport, not "SSE". If a tool can only start a command (stdio), use the `mcp-remote` bridge shown under Claude Desktop.

The config keys above are the ones those tools used when this was written. If a tool has changed its format, its own MCP documentation wins; the address stays the same.

## Check the connection without an AI tool

With the app open, run this in a terminal:

```bash
curl -s -X POST http://127.0.0.1:47700/mcp -H "Content-Type: application/json" -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"
```

You should get a JSON list of tools. If you do, the server is fine and any problem is in the AI tool's configuration.

## What to ask

- "Show me my current Raw Accel settings."
- "Switch to Natural with a limit of 2 and show me the window."
- "What sensitivity do I get at 30 counts/ms? And at 60?"
- "What is my cm/360 in CS2 at 800 DPI and sens 1.2, and what is the equivalent sens in Valorant?"
- "Pin my current curve as a reference so I can compare it to a steeper curve."
- "Show me my speed histogram to see where my hand speed lands on the curve."
- "Share my current profile as a code."
- "Switch the theme to Obsidian or Cyberpunk."
- "Make a profile called `tracking` copied from `default`, with a lower cap."
- "I overshoot on fast flicks. Look at my curve and suggest a change, but don't write it yet."
- "Open the sensitivity calculator panel."

## How it behaves

- **You see everything.** Tools act on the open window. A note appears on the plot naming each tool that changes something, and the driver square in the title block blinks.
- **Edits are not writes.** Changing values only edits what the window shows. The driver is only touched by `apply` (write) and `reset_driver` (acceleration off). Tell your AI tool whether it may call those.
- **Bad values are refused.** Raw Accel's own validation runs on every change. A refused change leaves everything as it was and returns the reason.
- **Undo works.** Changes made over MCP are on the same undo stack as yours: `Ctrl`+`Z`.
- **The one-second delay stays.** Raw Accel delays every write by a second so settings can't be flipped rapidly. A write over MCP takes about two seconds.

## Tools

| Tool | What it does |
|---|---|
| `get_state` | Selected profile, its values, unwritten changes, errors |
| `list_modes` | Modes with their parameters, ranges and defaults |
| `get_profile` | All values of a profile |
| `get_curve` | Sensitivity sampled across input speeds, for the edited or the driver's curve |
| `evaluate` | Sensitivity at one input speed |
| `get_live_input` | Current hand speed and recent peak |
| `capture_window` | A PNG of the window as it looks now |
| `update_profile` | Change any values of the selected profile |
| `set_mode` | Switch acceleration mode |
| `undo`, `redo`, `revert` | Step through edits, or drop everything not written |
| `apply` | Write to the driver and save `settings.json` |
| `reset_driver` | Turn acceleration off in the driver |
| `list_profiles`, `select_profile`, `create_profile`, `rename_profile`, `delete_profile` | Manage profiles |
| `list_devices`, `set_device`, `set_default_device`, `remove_device` | Per-mouse DPI, polling rate, profile |
| `export_settings`, `import_settings` | Settings as Raw Accel `settings.json` text |
| `export_profile_code`, `import_profile_code` | Share and import individual profiles as compact short codes (`RA1:...`) |
| `calculate_cm360` | Calculate cm/360, in/360, curve range (slowest tracking to fastest flick), and game equivalents |
| `convert_sensitivity` | Convert sensitivity across games (Source/CS2, Valorant, Overwatch 2, Fortnite, Rainbow Six Siege) and DPIs |
| `pin_reference_curve`, `clear_reference_curve`, `get_reference_curve` | Pin and overlay a reference baseline curve on the plot to compare live edits |
| `get_speed_histogram`, `reset_speed_histogram` | Retrieve hand speed distribution statistics (median, 90th percentile, peak) and bucket counts |
| `export_plot_image` | Export the plot canvas image as a PNG |
| `get_app_settings`, `set_app_settings` | Theme (14 light and dark themes), typeface, plot view and scale, start-up, updates |
| `show_panel` | Open or close a panel (`profiles`, `devices`, `system`, `help`, `themes`, `fonts`, `cm360`, `none`) |
| `check_for_updates`, `install_update` | Look for and install a newer release |

Units: speeds are counts per millisecond. Sensitivity is a multiplier where 1 means unchanged.

## Troubleshooting

| Problem | What to check |
|---|---|
| Connection refused | Is the app open? Does **Driver → MCP server** say `listening`? |
| `not running`, with a message about the port | Another program uses that port. Change the port in the Driver sheet and use the new address in your tool. |
| The tool connects but lists no tools | It may be using the old SSE transport. Choose HTTP / Streamable HTTP. |
| HTTP 403 | The request came with a web page's `Origin`, or through a host name other than `127.0.0.1` or `localhost`. Use the address exactly as shown. |
| HTTP 405 | The tool sent `GET`. The server only answers `POST`; this is normal during some tools' start-up probing and can be ignored if tools then appear. |
| It worked, then stopped | The app was closed, or it restarted on another port. Reopen it and check the address. |

## Security

- The server listens on the loopback address only, so other computers on your network can't reach it.
- It refuses requests that carry a non-local `Origin` or `Host`, which stops web pages in your browser from talking to it.
- There is no password. Any program running as you on this PC can use it. That is no new power: any such program can already talk to the Raw Accel driver directly.
- If you don't use it, switch it off in **Driver → MCP server**.
