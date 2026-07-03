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

  function pasteText(text) {
    if (text && state.activeSessionId) {
      send({ type: "paste", sessionId: state.activeSessionId, text: text });
    }
  }

  // Used by the context menu, where no native paste event is available.
  function pasteFromClipboard() {
    if (!navigator.clipboard || !state.activeSessionId) return;
    navigator.clipboard.readText().then(pasteText).catch(function () { /* permission denied */ });
  }

  // Single paste path. Ctrl+V / Shift+Insert trigger the browser's native
  // paste; we intercept it in the capture phase (before xterm's own textarea
  // listener runs) and route the text through the bridge to PTY stdin.
  // Routing on the paste event instead of reading the clipboard from the
  // keydown handler prevents the text being delivered twice (once via our
  // bridge, once via xterm's default paste -> onData).
  container.addEventListener("paste", function (ev) {
    ev.preventDefault();
    ev.stopImmediatePropagation();
    var text = ev.clipboardData ? ev.clipboardData.getData("text/plain") : "";
    pasteText(text);
  }, true);

  function clearScreen() {
    term.clear();
    if (state.activeSessionId) {
      send({ type: "clearScreen", sessionId: state.activeSessionId });
    }
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

    // Ctrl+V (and Ctrl+Shift+V): block xterm's keydown handling but allow the
    // browser default so exactly one native paste event fires; the capture
    // listener above routes it through the bridge.
    if (ev.ctrlKey && (ev.key === "v" || ev.key === "V")) {
      return false;
    }

    // Ctrl+L: clear the screen (keeps the current prompt line), shell-agnostic.
    if (ev.ctrlKey && !ev.shiftKey && !ev.altKey && (ev.key === "l" || ev.key === "L")) {
      ev.preventDefault();
      clearScreen();
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
  // Context menu (Cut / Copy / Paste / Delete / Select All)
  // -------------------------------------------------------------------------

  var contextMenu = (function () {
    var element = document.getElementById("context-menu");

    function buildItems() {
      var hasSelection = term.hasSelection();
      return [
        {
          label: "Cut", enabled: hasSelection,
          run: function () { copySelection(); term.clearSelection(); }
        },
        {
          label: "Copy", enabled: hasSelection,
          run: function () { copySelection(); }
        },
        {
          label: "Paste", enabled: !!state.activeSessionId,
          run: function () { pasteFromClipboard(); }
        },
        {
          label: "Delete", enabled: !!state.activeSessionId,
          // Terminals cannot remove already-rendered output; Delete forwards
          // the Delete key (deletes the character at the shell's cursor).
          run: function () {
            term.clearSelection();
            send({ type: "input", sessionId: state.activeSessionId, payload: "\u001b[3~" });
          }
        },
        { separator: true },
        {
          label: "Select All", enabled: true,
          run: function () { term.selectAll(); }
        }
      ];
    }

    function open(x, y) {
      element.textContent = "";

      buildItems().forEach(function (item) {
        if (item.separator) {
          var hr = document.createElement("div");
          hr.className = "separator";
          element.appendChild(hr);
          return;
        }

        var entry = document.createElement("div");
        entry.className = "item" + (item.enabled ? "" : " disabled");
        entry.textContent = item.label;
        if (item.enabled) {
          entry.addEventListener("mousedown", function (ev) {
            // mousedown (not click): act before the terminal steals focus
            // and clears the selection.
            ev.preventDefault();
            ev.stopPropagation();
            close();
            item.run();
            term.focus();
          });
        }
        element.appendChild(entry);
      });

      element.style.display = "block";

      // Clamp inside the viewport.
      var rect = element.getBoundingClientRect();
      var left = Math.min(x, window.innerWidth - rect.width - 4);
      var top = Math.min(y, window.innerHeight - rect.height - 4);
      element.style.left = Math.max(0, left) + "px";
      element.style.top = Math.max(0, top) + "px";
    }

    function close() {
      element.style.display = "none";
    }

    function isOpen() {
      return element.style.display === "block";
    }

    return { open: open, close: close, isOpen: isOpen };
  })();

  container.addEventListener("contextmenu", function (ev) {
    ev.preventDefault();
    ev.stopPropagation();
    contextMenu.open(ev.clientX, ev.clientY);
  });

  document.addEventListener("mousedown", function (ev) {
    if (contextMenu.isOpen() && !ev.target.closest("#context-menu")) contextMenu.close();
  });
  document.addEventListener("keydown", function (ev) {
    if (ev.key === "Escape") contextMenu.close();
  }, true);
  window.addEventListener("blur", function () { contextMenu.close(); });
  window.addEventListener("resize", function () { contextMenu.close(); });

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
