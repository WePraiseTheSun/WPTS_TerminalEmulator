# Terminal Emulator Control

A production-grade, reusable **WinForms UserControl** implementing the Terminal Emulator Control Specification v1.1: a full terminal experience built from a Chromium rendering layer (**WebView2 + xterm.js**) over a native Windows pseudo-console backend (**ConPTY**), targeting **.NET Framework 4.8**.

## Requirements

- Windows 10 1809 (build 17763) or later — ConPTY is not available on earlier builds.
- [WebView2 Evergreen Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (preinstalled on Windows 11 and up-to-date Windows 10).
- Visual Studio 2022 with the .NET Framework 4.8 targeting pack.

## Build & run

```
git clone <repo>
cd TerminalEmulator
# open TerminalEmulator.sln in Visual Studio 2022 and press F5, or:
msbuild TerminalEmulator.sln /restore /p:Configuration=Release
src\TerminalEmulator.Demo\bin\Release\net48\TerminalEmulator.Demo.exe
```

NuGet packages (`Microsoft.Web.WebView2`, `Newtonsoft.Json`) restore automatically. The xterm.js browser bundles (`@xterm/xterm` 6.0.0, `@xterm/addon-fit`, `@xterm/addon-web-links`, MIT-licensed — see `Assets/lib/LICENSE-xterm.txt`) are vendored under `src/TerminalEmulator.Control/Assets/lib` so the control runs fully offline with no CDN dependency, matching the security model (local embedded assets only).

## Hosting the control

```csharp
public partial class MainForm : Form
{
    private TerminalControl terminal;

    public MainForm()
    {
        InitializeComponent();
        terminal = new TerminalControl { Dock = DockStyle.Fill };
        Controls.Add(terminal);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        terminal.StartShell(); // initializes WebView2 + default session
    }
}
```

Public API highlights:

```csharp
terminal.CreateSession(ShellProfile.PowerShell());     // new tab
terminal.CreateSession(ShellProfile.Wsl("Ubuntu"));    // WSL distro
terminal.CreateSession(ShellProfile.Ssh("user@host")); // OpenSSH client
terminal.SwitchToSession(id);                          // fast attach/detach
terminal.CloseSession(id);
terminal.ApplyTheme(ThemeManager.Get("solarized-dark"));
terminal.ApplyThemeJson(File.ReadAllText("mytheme.json")); // JSON hot reload
terminal.SendInput("dir\r");                           // automation
terminal.CompletionEngine.RegisterSource(new MySshCompletionSource());
terminal.SessionExited += (id, code, willRestart) => { /* ... */ };
```

Set `ShellProfile.AutoRestart = true` for crash-recovering shells (bounded by `MaxAutoRestarts`).

## Architecture

```
WinForms host ──> TerminalControl (UserControl)
                      │
                      ├── WebView2 (Chromium) ── https://terminal.app (virtual host → Assets/)
                      │        └── xterm.js renderer + tab strip + completion popup
                      │              ▲│  async JSON message bus (PostWebMessageAsJson / postMessage)
                      ├── Bridge routing (BridgeProtocol.cs ↔ terminal.js)
                      │
                      └── SessionManager ──> TerminalSession (per tab)
                               ├── PseudoConsole (CreatePseudoConsole / Resize / Close)
                               ├── ConPtyProcess (EXTENDED_STARTUPINFO_PRESENT + PSEUDOCONSOLE attribute)
                               ├── OutputAggregator ── RingBuffer (1 MiB, drop-oldest + visible truncation notice)
                               └── Replay cache (2 M chars) for instant tab switching
```

### Streaming pipeline & flush strategies (spec §3.3, §8)

Each session's pipe reader posts raw bytes into a per-session **ring buffer**. An aggregator drains it and decodes UTF-8 with a **stateful decoder** (multi-byte sequences split across pipe reads reassemble correctly), then flushes strings over the bridge:

- **Interactive mode** — ~4 ms flush latency for a 250 ms window after every keystroke, so echo feels immediate.
- **Batch mode** — ~30 ms aggregation windows during bulk output (builds, logs, git), capping WebView2 IPC message counts and DOM churn.
- A 64 KiB threshold forces an immediate flush regardless of mode; chunks are capped at 128 KiB per IPC message.

**Backpressure:** the renderer acknowledges rendered characters via `ack` messages (xterm.js `write` callbacks). When un-acknowledged volume passes a 512 K-char high-water mark, the pipe read loop pauses — the OS pipe fills and the shell blocks on write, exactly like a real terminal that can't keep up. No message flooding, stable throughput under `type bigfile.txt`-class bursts.

### Sessions (spec §3.1, §6)

Each tab is an isolated `TerminalSession`: its own ConPTY handle, process tree, ring buffer and encoding pipeline — no shared stdout buffers. Switching detaches the renderer stream from the old session and replays the new session's cached scrollback (`term.reset()` + replay write), so background shells keep running and switches are instant. Resize events are debounced in the renderer (50 ms) **and** coalesced in the host (40 ms) so ConPTY only sees settled dimensions during window drags.

### Bridge protocol (spec §5)

All message types are defined in one auditable place on each side: `Bridge/BridgeProtocol.cs` and the header of `Assets/terminal.js`. Host→renderer: `init`, `data`, `replay`, `sessions`, `config`, `completions`, `exited`. Renderer→host: `ready`, `input`, `resize`, `paste`, `ack`, `createSession`, `closeSession`, `switchSession`, `requestCompletion`, `setTheme`.

### Clipboard (spec §4.2)

- **Ctrl+C** — copies when a selection is active (does *not* send SIGINT); otherwise passes through to the shell.
- **Ctrl+Shift+C** — forced copy.
- **Ctrl+V** — read via the Clipboard API (the host grants only the `ClipboardRead` WebView2 permission), routed over the bridge and injected into PTY stdin with line endings normalized to `\r`.

### Auto-completion (spec §7.1)

**Ctrl+Space** opens backend-driven path completion (Tab is deliberately left to the shell's own completion). The renderer keeps a best-effort shadow of the current input line; the host's `PathCompletionEngine` extracts the trailing token (double-quote and `%ENV%`-aware), resolves it against the session's start directory and streams up to 50 matches, directories first. `ICompletionSource` is the extension point for SSH-remote or WSL path resolution.

### Theme engine (spec §4.3)

Themes carry fore/background, the full 16-color ANSI palette, cursor style (block/underline/bar) and font settings, serialize directly to xterm.js's theme shape, and hot-reload over the bridge with no page reload. Built-ins: `dark-plus`, `one-light`, `solarized-dark`; user themes register via `ThemeManager.Register` or `ApplyThemeJson`.

### Security model (spec §11)

- WebView2 is locked to the `https://terminal.app` virtual host mapped onto the embedded `Assets` folder; `NavigationStarting` cancels everything else and new windows are suppressed.
- A CSP on `index.html` restricts scripts/styles to `'self'`.
- Host objects are disabled; the bridge is plain `postMessage` JSON only. Dev tools are disabled in Release builds.
- Only the `ClipboardRead` permission is ever granted; all other permission requests are denied.
- Per the spec, no command filtering happens at the application layer — that is the PTY's responsibility.

### Error handling & lifecycle (spec §10)

Deterministic teardown order (close pseudo console → terminate lingering process → release pipe streams and handles), attribute-list and handle disposal in `ConPtyProcess`, crash recovery via bounded auto-restart with a visible `[process exited …]` notice injected into the stream, and full cleanup on control disposal.

## Design notes & known trade-offs

- **Completion line shadow.** The renderer cannot read the shell's internal line buffer, so completion is seeded from a shadow of typed characters (reset on Enter/^C/escape sequences) and resolves relative paths against the session's start directory. This is documented, deliberate, and replaceable via `ICompletionSource` (e.g., shell-integration escape sequences).
- **Inline syntax recoloring** of the live prompt is intentionally not enabled: the PTY owns echo, and rewriting echoed cells fights the shell's own rendering (the same reason Windows Terminal doesn't recolor cmd input). Token classification is applied where the control owns the pixels — the completion popup — and the theme's ANSI palette governs everything the shell colors itself.
- **Anonymous pipes** are used per the canonical ConPTY pattern; reads run on dedicated thread-pool tasks, and all UI marshaling goes through `BeginInvoke`.

## Repository layout

```
src/TerminalEmulator.Control/   the reusable UserControl library
  Native/       ConPTY + process P/Invoke, pipe & handle lifecycle
  IO/           RingBuffer, OutputAggregator (flush strategies)
  Sessions/     ShellProfile, TerminalSession, SessionManager
  Bridge/       protocol constants
  Completion/   PathCompletionEngine + ICompletionSource
  Theming/      TerminalTheme + ThemeManager
  Assets/       index.html, terminal.js, terminal.css, vendored xterm.js
src/TerminalEmulator.Demo/      reference WinForms host
```
