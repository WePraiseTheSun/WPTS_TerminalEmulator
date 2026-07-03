/*
 * terminal.js — renderer side of the .NET <-> xterm.js bridge.
 *
 * Protocol (mirrors BridgeProtocol.cs):
 *   host -> renderer: init, data, replay, sessions, config, completions, exited
 *   renderer -> host: ready, input, resize, paste, ack, createSession,
 *                     closeSession, switchSession, requestCompletion, setTheme
 */
(function () {
  "use strict";

  // -------------------------------------------------------------------------
  // Bridge transport
  // -------------------------------------------------------------------------

  var bridge = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;

  function send(message) {
    if (bridge) bridge.postMessage(message);
  }

  // -------------------------------------------------------------------------
  // Terminal setup
  // -------------------------------------------------------------------------

  var term = new Terminal({
    allowProposedApi: true,
    cursorBlink: true,
    cursorStyle: "block",
    scrollback: 10000,
    fontFamily: "Cascadia Mono, Consolas, monospace",
    fontSize: 14,
    convertEol: false
  });

  var fitAddon = new FitAddon.FitAddon();
  term.loadAddon(fitAddon);
  term.loadAddon(new WebLinksAddon.WebLinksAddon());

  var container = document.getElementById("terminal");
  term.open(container);

  var state = {
    activeSessionId: null,
    sessions: [],
    // Best-effort shadow of the shell's current input line, used only to seed
    // path completion. Reset on Enter/Ctrl+C/escape sequences.
    shadowLine: ""
  };

  // -------------------------------------------------------------------------
  // Input -> host
  // -------------------------------------------------------------------------

  term.onData(function (data) {
    if (!state.activeSessionId) return;
    send({ type: "input", sessionId: state.activeSessionId, payload: data });
    updateShadowLine(data);
  });

  function updateShadowLine(data) {
    if (data.length > 0 && data.charCodeAt(0) === 0x1b) {
      // Cursor movement / function keys make the shadow unreliable.
      state.shadowLine = "";
      return;
    }
    for (var i = 0; i < data.length; i++) {
      var code = data.charCodeAt(i);
      if (code === 13 || code === 10 || code === 3) {          // Enter / ^C
        state.shadowLine = "";
      } else if (code === 127 || code === 8) {                  // Backspace
        state.shadowLine = state.shadowLine.slice(0, -1);
      } else if (code >= 32) {
        state.shadowLine += data[i];
      }
    }
  }

  // -------------------------------------------------------------------------
  // Clipboard & hotkeys
  // -------------------------------------------------------------------------

  function copySelection() {
    var selection = term.getSelection();
    if (selection && navigator.clipboard) {
      navigator.clipboard.writeText(selection).catch(function () { /* denied */ });
    }
  }

  function pasteFromClipboard() {
    if (!navigator.clipboard || !state.activeSessionId) return;
    navigator.clipboard.readText().then(function (text) {
      if (text) send({ type: "paste", sessionId: state.activeSessionId, text: text });
    }).catch(function () { /* permission denied */ });
  }

  term.attachCustomKeyEventHandler(function (ev) {
    if (ev.type !== "keydown") return true;

    if (completion.isOpen()) {
      if (completion.handleKey(ev)) return false;
    }

    // Ctrl+C: copy when a selection exists, otherwise fall through to SIGINT.
    if (ev.ctrlKey && !ev.shiftKey && !ev.altKey && (ev.key === "c" || ev.key === "C")) {
      if (term.hasSelection()) {
        copySelection();
        term.clearSelection();
        return false;
      }
      return true;
    }

    // Ctrl+Shift+C: forced copy mode.
    if (ev.ctrlKey && ev.shiftKey && (ev.key === "c" || ev.key === "C")) {
      copySelection();
      return false;
    }

    // Ctrl+V (and Ctrl+Shift+V): paste routed through the bridge to PTY stdin.
    if (ev.ctrlKey && (ev.key === "v" || ev.key === "V")) {
      pasteFromClipboard();
      return false;
    }

    // Ctrl+Space: path completion (Tab is left to the shell's own completion).
    if (ev.ctrlKey && ev.code === "Space") {
      completion.request();
      return false;
    }

    return true;
  });

  // -------------------------------------------------------------------------
  // Resize handling (debounced fit -> resize message)
  // -------------------------------------------------------------------------

  var fitTimer = null;

  function scheduleFit() {
    if (fitTimer) clearTimeout(fitTimer);
    fitTimer = setTimeout(function () {
      fitTimer = null;
      try { fitAddon.fit(); } catch (e) { return; }
      send({ type: "resize", cols: term.cols, rows: term.rows });
    }, 50);
  }

  new ResizeObserver(scheduleFit).observe(document.getElementById("terminal-container"));
  window.addEventListener("resize", scheduleFit);

  // -------------------------------------------------------------------------
  // Tabs
  // -------------------------------------------------------------------------

  var tabsElement = document.getElementById("tabs");

  function renderTabs() {
    tabsElement.textContent = "";

    state.sessions.forEach(function (session) {
      var tab = document.createElement("div");
      tab.className = "tab" + (session.id === state.activeSessionId ? " active" : "");
      tab.setAttribute("role", "tab");
      tab.title = session.title;

      var title = document.createElement("span");
      title.className = "title";
      title.textContent = session.title;
      tab.appendChild(title);

      var close = document.createElement("button");
      close.className = "close";
      close.textContent = "\u00d7";
      close.title = "Close session";
      close.addEventListener("click", function (ev) {
        ev.stopPropagation();
        send({ type: "closeSession", sessionId: session.id });
      });
      tab.appendChild(close);

      tab.addEventListener("click", function () {
        if (session.id !== state.activeSessionId) {
          send({ type: "switchSession", sessionId: session.id });
        }
      });

      tabsElement.appendChild(tab);
    });
  }

  Array.prototype.forEach.call(document.querySelectorAll("#actions .action"), function (button) {
    button.addEventListener("click", function () {
      send({ type: "createSession", shell: button.getAttribute("data-shell") });
      term.focus();
    });
  });

  // -------------------------------------------------------------------------
  // Theme engine (runtime hot reload, no page reload)
  // -------------------------------------------------------------------------

  function applyTheme(theme) {
    if (!theme) return;

    term.options.theme = {
      background: theme.background,
      foreground: theme.foreground,
      cursor: theme.cursor,
      cursorAccent: theme.cursorAccent,
      selectionBackground: theme.selectionBackground,
      black: theme.black, red: theme.red, green: theme.green, yellow: theme.yellow,
      blue: theme.blue, magenta: theme.magenta, cyan: theme.cyan, white: theme.white,
      brightBlack: theme.brightBlack, brightRed: theme.brightRed,
      brightGreen: theme.brightGreen, brightYellow: theme.brightYellow,
      brightBlue: theme.brightBlue, brightMagenta: theme.brightMagenta,
      brightCyan: theme.brightCyan, brightWhite: theme.brightWhite
    };

    if (theme.cursorStyle) term.options.cursorStyle = theme.cursorStyle;
    if (theme.fontFamily) term.options.fontFamily = theme.fontFamily;
    if (theme.fontSize) term.options.fontSize = theme.fontSize;

    var root = document.documentElement.style;
    root.setProperty("--term-bg", theme.background || "#1e1e1e");
    root.setProperty("--term-fg", theme.foreground || "#cccccc");
    document.body.style.background = theme.background || "#1e1e1e";

    scheduleFit();
  }

  // -------------------------------------------------------------------------
  // Completion popup
  // -------------------------------------------------------------------------

  var completion = (function () {
    var popup = document.getElementById("completion-popup");
    var list = document.getElementById("completion-list");
    var items = [];
    var selected = 0;

    function isOpen() { return !popup.hidden; }

    function close() {
      popup.hidden = true;
      items = [];
      selected = 0;
    }

    function request() {
      if (!state.activeSessionId) return;
      send({
        type: "requestCompletion",
        sessionId: state.activeSessionId,
        line: state.shadowLine
      });
    }

    function show(newItems) {
      items = newItems || [];
      selected = 0;
      if (items.length === 0) { close(); return; }
      render();
      popup.hidden = false;
    }

    function render() {
      list.textContent = "";
      items.forEach(function (item, index) {
        var li = document.createElement("li");
        li.dataset.kind = item.Kind || item.kind || "file";
        if (index === selected) li.classList.add("selected");

        var kind = document.createElement("span");
        kind.className = "kind";
        kind.textContent = li.dataset.kind === "directory" ? "dir" : "";
        li.appendChild(kind);

        var label = document.createElement("span");
        label.className = "label";
        label.textContent = item.Label || item.label || "";
        li.appendChild(label);

        li.addEventListener("click", function () {
          selected = index;
          insertSelected();
        });

        list.appendChild(li);
      });

      var selectedElement = list.children[selected];
      if (selectedElement) selectedElement.scrollIntoView({ block: "nearest" });
    }

    function insertSelected() {
      var item = items[selected];
      if (item && state.activeSessionId) {
        var text = item.InsertText || item.insertText || "";
        if (text) {
          send({ type: "input", sessionId: state.activeSessionId, payload: text });
          state.shadowLine += text;
        }
      }
      close();
      term.focus();
    }

    /** Returns true when the key was consumed by the popup. */
    function handleKey(ev) {
      switch (ev.key) {
        case "ArrowDown":
          selected = (selected + 1) % items.length;
          render();
          return true;
        case "ArrowUp":
          selected = (selected - 1 + items.length) % items.length;
          render();
          return true;
        case "Tab":
        case "Enter":
          insertSelected();
          return true;
        case "Escape":
          close();
          return true;
        default:
          // Any other key: dismiss the popup and let the terminal handle it.
          close();
          return false;
      }
    }

    return { isOpen: isOpen, request: request, show: show, close: close, handleKey: handleKey };
  })();

  // -------------------------------------------------------------------------
  // Host -> renderer message handling
  // -------------------------------------------------------------------------

  function handleMessage(message) {
    if (!message || !message.type) return;

    switch (message.type) {
      case "init":
        applyTheme(message.theme);
        scheduleFit();
        break;

      case "data":
        // Empty sessionId = host-level notice (e.g., shell failed to start):
        // always render it. Otherwise drop stale streams from other sessions.
        if (message.sessionId && message.sessionId !== state.activeSessionId) return;
        term.write(message.payload, function () {
          // Backpressure credit: tell the host how much has been rendered.
          if (message.sessionId) {
            send({ type: "ack", sessionId: message.sessionId, bytes: message.payload.length });
          }
        });
        break;

      case "replay":
        state.activeSessionId = message.sessionId || null;
        state.shadowLine = "";
        completion.close();
        term.reset();
        if (message.payload) {
          term.write(message.payload, function () {
            send({ type: "ack", sessionId: message.sessionId, bytes: message.payload.length });
          });
        }
        term.focus();
        break;

      case "sessions":
        state.sessions = message.sessions || [];
        if (message.activeSessionId) state.activeSessionId = message.activeSessionId;
        renderTabs();
        break;

      case "config":
        applyTheme(message.theme);
        break;

      case "completions":
        if (message.sessionId === state.activeSessionId) {
          completion.show(message.items || []);
        }
        break;

      case "exited":
        // The host injects a visible notice into the stream; nothing to do
        // here beyond keeping the tab strip accurate on the next update.
        break;
    }
  }

  if (bridge) {
    bridge.addEventListener("message", function (ev) {
      handleMessage(ev.data);
    });
  }

  // -------------------------------------------------------------------------
  // Boot
  // -------------------------------------------------------------------------

  fitAddon.fit();
  term.focus();
  send({ type: "ready", cols: term.cols, rows: term.rows });
})();
