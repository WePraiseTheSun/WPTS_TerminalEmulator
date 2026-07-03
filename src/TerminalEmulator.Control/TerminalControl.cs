using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TerminalEmulator.Control.Bridge;
using TerminalEmulator.Control.Completion;
using TerminalEmulator.Control.Sessions;
using TerminalEmulator.Control.Theming;

namespace TerminalEmulator.Control
{
    /// <summary>
    /// A reusable WinForms terminal emulator control.
    ///
    /// Rendering: WebView2 (Chromium) hosting xterm.js from embedded local
    /// assets only. Backend: one isolated ConPTY instance per session with
    /// ring-buffered streaming, renderer backpressure and adaptive flushing.
    ///
    /// Typical usage:
    /// <code>
    ///   var terminal = new TerminalControl { Dock = DockStyle.Fill };
    ///   Controls.Add(terminal);
    ///   terminal.StartShell(); // in OnLoad
    /// </code>
    /// </summary>
    public partial class TerminalControl : UserControl
    {
        private const string VirtualHost = "terminal.app";
        private const string StartPage = "https://" + VirtualHost + "/index.html";

        private readonly SessionManager _sessions = new SessionManager();
        private readonly PathCompletionEngine _completion = new PathCompletionEngine();
        private readonly Timer _resizeCoalescer;

        private WebView2 _webView;
        private TerminalTheme _theme = ThemeManager.DarkPlus;
        private ShellProfile _defaultProfile = ShellProfile.Cmd();

        private volatile bool _bridgeReady;
        private bool _startRequested;
        private bool _disposedControl;
        private int _cols = 120;
        private int _rows = 30;
        private int _pendingCols = -1;
        private int _pendingRows = -1;

        /// <summary>Raised once the renderer is loaded and the bridge is live.</summary>
        public event EventHandler Ready;

        /// <summary>Raised when a session's shell process exits. Args: session id, exit code, will-restart.</summary>
        public event Action<string, int, bool> SessionExited;

        /// <summary>The profile used by <see cref="StartShell()"/> and the default "+" tab action.</summary>
        public ShellProfile DefaultProfile
        {
            get { return _defaultProfile; }
            set { _defaultProfile = value ?? throw new ArgumentNullException(nameof(value)); }
        }

        /// <summary>The active theme. Assigning applies it live without reload.</summary>
        public TerminalTheme Theme
        {
            get { return _theme; }
            set { ApplyTheme(value); }
        }

        /// <summary>The completion engine; register ICompletionSource plugins here (SSH, WSL, ...).</summary>
        public PathCompletionEngine CompletionEngine
        {
            get { return _completion; }
        }

        public string ActiveSessionId
        {
            get { return _sessions.ActiveSessionId; }
        }

        public TerminalControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.FromArgb(30, 30, 30);

            _resizeCoalescer = new Timer { Interval = 40 };
            _resizeCoalescer.Tick += OnResizeCoalescerTick;

            _sessions.SessionOutput += OnSessionOutput;
            _sessions.SessionExited += OnSessionExited;
        }

        // -------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------

        /// <summary>Initializes WebView2 and starts the default shell session.</summary>
        public void StartShell()
        {
            StartShell(null);
        }

        /// <summary>Initializes WebView2 and starts a shell session using the given profile.</summary>
        public async void StartShell(ShellProfile profile)
        {
            try
            {
                await StartShellAsync(profile);
            }
            catch (Exception ex)
            {
                ShowFatalError(ex);
            }
        }

        /// <summary>Awaitable variant of <see cref="StartShell(ShellProfile)"/>.</summary>
        public async Task StartShellAsync(ShellProfile profile = null)
        {
            if (_disposedControl) throw new ObjectDisposedException(nameof(TerminalControl));
            if (profile != null) _defaultProfile = profile;
            if (_startRequested) return;
            _startRequested = true;

            await InitializeWebViewAsync();
            // The default session is created when the renderer signals "ready".
        }

        /// <summary>Creates a new session and switches the renderer to it.</summary>
        public TerminalSession CreateSession(ShellProfile profile)
        {
            var session = _sessions.CreateSession(profile ?? _defaultProfile, _cols, _rows);
            ActivateSession(session.Id);
            PushSessionList();
            return session;
        }

        /// <summary>Closes a session; if it was active, activates the next one.</summary>
        public void CloseSession(string sessionId)
        {
            string next = _sessions.CloseSession(sessionId);
            if (next != null)
            {
                ActivateSession(next);
            }
            else
            {
                PostToRenderer(new { type = BridgeProtocol.Replay, sessionId = string.Empty, payload = string.Empty });
            }
            PushSessionList();
        }

        public void CloseActiveSession()
        {
            var id = _sessions.ActiveSessionId;
            if (id != null) CloseSession(id);
        }

        /// <summary>Switches the renderer stream to another running session.</summary>
        public void SwitchToSession(string sessionId)
        {
            ActivateSession(sessionId);
            PushSessionList();
        }

        /// <summary>Applies a theme live over the bridge (no reload).</summary>
        public void ApplyTheme(TerminalTheme theme)
        {
            _theme = theme ?? throw new ArgumentNullException(nameof(theme));
            if (_bridgeReady)
            {
                PostToRenderer(new { type = BridgeProtocol.Config, theme = _theme });
            }
        }

        /// <summary>Loads a theme from JSON, registers it, and applies it.</summary>
        public void ApplyThemeJson(string json)
        {
            var theme = TerminalTheme.FromJson(json);
            ThemeManager.Register(theme);
            ApplyTheme(theme);
        }

        /// <summary>Sends raw input (keystrokes / control sequences) to the active session.</summary>
        public void SendInput(string text)
        {
            var session = _sessions.ActiveSession;
            if (session != null) session.WriteInput(text);
        }

        // -------------------------------------------------------------------
        // WebView2 bootstrap
        // -------------------------------------------------------------------

        private async Task InitializeWebViewAsync()
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(30, 30, 30)
            };
            Controls.Add(_webView);

            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TerminalEmulatorControl", "WebView2");

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(environment);

            var core = _webView.CoreWebView2;

            // Security posture: local embedded assets only, JS strictly for the bridge.
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif

            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, ResolveAssetsPath(), CoreWebView2HostResourceAccessKind.Allow);

            core.NavigationStarting += OnNavigationStarting;
            core.NewWindowRequested += (s, e) => e.Handled = true;
            core.PermissionRequested += OnPermissionRequested;
            core.WebMessageReceived += OnWebMessageReceived;

            // Drag & drop of files: Chromium cannot expose full local paths to
            // JavaScript, so drops are handled at the WinForms layer instead.
            // Disabling the browser's own drop target makes the OLE drop bubble
            // up the HWND chain to the WebView2 control, where standard
            // WinForms drag-drop events fire with real file system paths.
            try
            {
                _webView.AllowExternalDrop = false;
            }
            catch (InvalidOperationException) { }
            catch (NotSupportedException) { }

            _webView.AllowExternalDrop = true;
            _webView.DragEnter += OnTerminalDragEnter;
            _webView.DragDrop += OnTerminalDragDrop;

            core.Navigate(StartPage);
        }

        private static string ResolveAssetsPath()
        {
            string baseDirectory = Path.GetDirectoryName(typeof(TerminalControl).Assembly.Location) ?? ".";
            string assets = Path.Combine(baseDirectory, "Assets");
            if (!Directory.Exists(assets))
            {
                throw new DirectoryNotFoundException(
                    "Terminal assets not found at '" + assets + "'. Ensure the Assets folder " +
                    "is copied to the output directory alongside TerminalEmulator.Control.dll.");
            }
            return assets;
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            // No external navigation, ever.
            Uri uri;
            bool allowed = Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) &&
                           string.Equals(uri.Host, VirtualHost, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
            if (!allowed) e.Cancel = true;
        }

        private void OnPermissionRequested(object sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            // Clipboard read is required for Ctrl+V routing; everything else is denied.
            e.State = e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
        }

        // -------------------------------------------------------------------
        // Bridge: JavaScript -> .NET
        // -------------------------------------------------------------------

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject message;
            try
            {
                message = JObject.Parse(e.WebMessageAsJson);
            }
            catch (JsonException)
            {
                return; // malformed message; ignore
            }

            string type = (string)message["type"];
            if (string.IsNullOrEmpty(type)) return;

            switch (type)
            {
                case BridgeProtocol.Ready:
                    HandleRendererReady();
                    break;

                case BridgeProtocol.Input:
                    HandleInput((string)message["sessionId"], (string)message["payload"]);
                    break;

                case BridgeProtocol.Resize:
                    HandleResize((int?)message["cols"] ?? 0, (int?)message["rows"] ?? 0);
                    break;

                case BridgeProtocol.Paste:
                    HandlePaste((string)message["sessionId"], (string)message["text"]);
                    break;

                case BridgeProtocol.Ack:
                    HandleAck((string)message["sessionId"], (long?)message["bytes"] ?? 0);
                    break;

                case BridgeProtocol.CreateSession:
                    HandleCreateSession((string)message["shell"]);
                    break;

                case BridgeProtocol.CloseSession:
                    CloseSession((string)message["sessionId"]);
                    break;

                case BridgeProtocol.SwitchSession:
                    SwitchToSession((string)message["sessionId"]);
                    break;

                case BridgeProtocol.RequestCompletion:
                    HandleCompletionRequest((string)message["sessionId"], (string)message["line"]);
                    break;

                case BridgeProtocol.SetTheme:
                    var theme = ThemeManager.Get((string)message["name"]);
                    if (theme != null) ApplyTheme(theme);
                    break;

                case BridgeProtocol.ClearScreen:
                    var cleared = _sessions.GetSession((string)message["sessionId"]) ?? _sessions.ActiveSession;
                    if (cleared != null) cleared.ClearReplay();
                    break;
            }
        }

        private void HandleRendererReady()
        {
            _bridgeReady = true;

            PostToRenderer(new
            {
                type = BridgeProtocol.Init,
                theme = _theme,
                themes = ThemeManager.Names
            });

            if (_sessions.Sessions.Count == 0)
            {
                try
                {
                    CreateSession(_defaultProfile);
                }
                catch (Exception ex)
                {
                    PostToRenderer(new
                    {
                        type = BridgeProtocol.Data,
                        sessionId = string.Empty,
                        payload = "\r\n\u001b[31mFailed to start shell: " + ex.Message + "\u001b[0m\r\n"
                    });
                }
            }
            else
            {
                // Renderer reloaded (e.g., WebView2 process recovery): re-attach.
                var active = _sessions.ActiveSessionId ?? _sessions.Sessions.First().Id;
                ActivateSession(active);
                PushSessionList();
            }

            var ready = Ready;
            if (ready != null) ready(this, EventArgs.Empty);
        }

        private void HandleInput(string sessionId, string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            var session = _sessions.GetSession(sessionId) ?? _sessions.ActiveSession;
            if (session != null) session.WriteInput(payload);
        }

        private void HandlePaste(string sessionId, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var session = _sessions.GetSession(sessionId) ?? _sessions.ActiveSession;
            if (session != null) session.Paste(text);
        }

        private void HandleAck(string sessionId, long chars)
        {
            var session = _sessions.GetSession(sessionId);
            if (session != null) session.AcknowledgeRendered(chars);
        }

        // -------------------------------------------------------------------
        // Drag & drop: dropped files paste their (quoted) paths into the
        // active shell; dropped text pastes as text.
        // -------------------------------------------------------------------

        private void OnTerminalDragEnter(object sender, DragEventArgs e)
        {
            bool acceptable = e.Data.GetDataPresent(DataFormats.FileDrop) ||
                              e.Data.GetDataPresent(DataFormats.UnicodeText) ||
                              e.Data.GetDataPresent(DataFormats.Text);
            e.Effect = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnTerminalDragDrop(object sender, DragEventArgs e)
        {
            var session = _sessions.ActiveSession;
            if (session == null) return;

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null && paths.Length > 0)
                {
                    session.WriteInput(BuildDroppedPathText(paths, session.Profile.Kind));
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                session.Paste(e.Data.GetData(DataFormats.UnicodeText) as string);
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                session.Paste(e.Data.GetData(DataFormats.Text) as string);
            }
        }

        internal static string BuildDroppedPathText(string[] paths, string shellKind)
        {
            bool isWsl = string.Equals(shellKind, "wsl", StringComparison.OrdinalIgnoreCase);

            var parts = new List<string>(paths.Length);
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                string value = isWsl ? ToWslPath(path) : path;
                parts.Add(QuoteIfNeeded(value));
            }
            return string.Join(" ", parts);
        }

        private static string QuoteIfNeeded(string path)
        {
            bool needsQuotes = false;
            foreach (var c in path)
            {
                if (c == ' ' || c == '\t' || c == '&' || c == '(' || c == ')' ||
                    c == '^' || c == ';' || c == ',' || c == '=' || c == '\'')
                {
                    needsQuotes = true;
                    break;
                }
            }
            return needsQuotes ? "\"" + path + "\"" : path;
        }

        /// <summary>Best-effort C:\foo\bar -> /mnt/c/foo/bar translation for WSL sessions.</summary>
        private static string ToWslPath(string path)
        {
            if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            {
                return "/mnt/" + char.ToLowerInvariant(path[0]) + path.Substring(2).Replace('\\', '/');
            }
            return path.Replace('\\', '/');
        }

        private void HandleResize(int cols, int rows)
        {
            if (cols <= 0 || rows <= 0) return;
            _pendingCols = cols;
            _pendingRows = rows;

            // Coalesce bursts (window drags produce dozens of resize events)
            // so ConPTY only sees the settled size.
            _resizeCoalescer.Stop();
            _resizeCoalescer.Start();
        }

        private void OnResizeCoalescerTick(object sender, EventArgs e)
        {
            _resizeCoalescer.Stop();
            if (_pendingCols <= 0 || _pendingRows <= 0) return;

            _cols = _pendingCols;
            _rows = _pendingRows;

            var session = _sessions.ActiveSession;
            if (session != null) session.Resize(_cols, _rows);
        }

        private void HandleCreateSession(string shellKind)
        {
            ShellProfile profile;
            switch (shellKind)
            {
                case "powershell": profile = ShellProfile.PowerShell(); break;
                case "cmd": profile = ShellProfile.Cmd(); break;
                case "wsl": profile = ShellProfile.Wsl(); break;
                default: profile = _defaultProfile; break;
            }

            try
            {
                CreateSession(profile);
            }
            catch (Exception ex)
            {
                PostToRenderer(new
                {
                    type = BridgeProtocol.Data,
                    sessionId = _sessions.ActiveSessionId ?? string.Empty,
                    payload = "\r\n\u001b[31mFailed to start " + profile.Name + ": " + ex.Message + "\u001b[0m\r\n"
                });
            }
        }

        private void HandleCompletionRequest(string sessionId, string line)
        {
            var session = _sessions.GetSession(sessionId) ?? _sessions.ActiveSession;
            if (session == null) return;

            string baseDirectory = session.Profile.StartDirectory;
            string capturedSessionId = session.Id;

            Task.Run(() =>
            {
                IReadOnlyList<CompletionItem> items;
                try
                {
                    items = _completion.Complete(line ?? string.Empty, baseDirectory);
                }
                catch (Exception)
                {
                    items = new List<CompletionItem>();
                }

                PostToRenderer(new
                {
                    type = BridgeProtocol.Completions,
                    sessionId = capturedSessionId,
                    items
                });
            });
        }

        // -------------------------------------------------------------------
        // Bridge: .NET -> JavaScript
        // -------------------------------------------------------------------

        private void OnSessionOutput(TerminalSession session, string payload)
        {
            PostToRenderer(new
            {
                type = BridgeProtocol.Data,
                sessionId = session.Id,
                payload
            });
        }

        private void OnSessionExited(TerminalSession session, int exitCode, bool willRestart)
        {
            PostToRenderer(new
            {
                type = BridgeProtocol.Exited,
                sessionId = session.Id,
                exitCode,
                willRestart
            });

            var handler = SessionExited;
            if (handler != null)
            {
                try { handler(session.Id, exitCode, willRestart); }
                catch (Exception) { }
            }
        }

        private void ActivateSession(string sessionId)
        {
            TerminalSession attached;
            string replay = _sessions.SwitchTo(sessionId, out attached);
            if (attached == null) return;

            attached.Resize(_cols, _rows);

            PostToRenderer(new
            {
                type = BridgeProtocol.Replay,
                sessionId = attached.Id,
                payload = replay ?? string.Empty
            });
        }

        private void PushSessionList()
        {
            var list = _sessions.Sessions
                .Select(s => new { id = s.Id, title = s.Profile.Name, kind = s.Profile.Kind })
                .ToList();

            PostToRenderer(new
            {
                type = BridgeProtocol.Sessions,
                sessions = list,
                activeSessionId = _sessions.ActiveSessionId
            });
        }

        private void PostToRenderer(object message)
        {
            if (_disposedControl || !_bridgeReady) return;

            string json = JsonConvert.SerializeObject(message);

            if (InvokeRequired)
            {
                try { BeginInvoke((Action)(() => PostCore(json))); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            }
            else
            {
                PostCore(json);
            }
        }

        private void PostCore(string json)
        {
            if (_disposedControl) return;
            var webView = _webView;
            if (webView == null || webView.CoreWebView2 == null) return;

            try
            {
                webView.CoreWebView2.PostWebMessageAsJson(json);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void ShowFatalError(Exception ex)
        {
            if (_disposedControl) return;
            var label = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.IndianRed,
                BackColor = Color.FromArgb(30, 30, 30),
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Terminal failed to initialize:\r\n" + ex.Message +
                       "\r\n\r\nEnsure the WebView2 Runtime is installed and ConPTY is available (Windows 10 1809+)."
            };
            Controls.Clear();
            Controls.Add(label);
        }

        // -------------------------------------------------------------------
        // Disposal
        // -------------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposedControl)
            {
                _disposedControl = true;
                _bridgeReady = false;

                _resizeCoalescer.Stop();
                _resizeCoalescer.Dispose();

                _sessions.SessionOutput -= OnSessionOutput;
                _sessions.SessionExited -= OnSessionExited;
                _sessions.Dispose();

                if (_webView != null)
                {
                    _webView.DragEnter -= OnTerminalDragEnter;
                    _webView.DragDrop -= OnTerminalDragDrop;
                    _webView.Dispose();
                    _webView = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}
