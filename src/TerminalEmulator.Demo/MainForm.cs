using System;
using System.Drawing;
using System.Windows.Forms;
using TerminalEmulator.Control;
using TerminalEmulator.Control.Sessions;
using TerminalEmulator.Control.Theming;

namespace TerminalEmulator.Demo
{
    /// <summary>
    /// Reference host demonstrating the TerminalControl public API:
    /// multi-shell tabs, runtime theme switching and session lifecycle events.
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly TerminalControl _terminal;
        private readonly ToolStripStatusLabel _status;

        public MainForm()
        {
            Text = "Terminal Emulator Control — Demo Host";
            Width = 1100;
            Height = 700;
            MinimumSize = new Size(640, 400);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(30, 30, 30);

            _terminal = new TerminalControl
            {
                Dock = DockStyle.Fill,
                DefaultProfile = ShellProfile.Cmd()
            };

            var menu = BuildMenu();

            var statusStrip = new StatusStrip();
            _status = new ToolStripStatusLabel("Initializing…");
            statusStrip.Items.Add(_status);

            Controls.Add(_terminal);
            Controls.Add(statusStrip);
            Controls.Add(menu);
            MainMenuStrip = menu;

            _terminal.Ready += (s, e) => _status.Text = "Terminal ready.";
            _terminal.SessionExited += (sessionId, exitCode, willRestart) =>
            {
                BeginInvoke((Action)(() =>
                {
                    _status.Text = "Session " + sessionId + " exited (code " + exitCode + ")" +
                                   (willRestart ? " — restarting." : ".");
                }));
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _terminal.StartShell(); // initializes WebView2 + default session
        }

        private MenuStrip BuildMenu()
        {
            var menu = new MenuStrip();

            var shellMenu = new ToolStripMenuItem("&Shell");
            shellMenu.DropDownItems.Add("New &Command Prompt", null,
                (s, e) => SafeCreate(ShellProfile.Cmd()));
            shellMenu.DropDownItems.Add("New &PowerShell", null,
                (s, e) => SafeCreate(ShellProfile.PowerShell()));
            shellMenu.DropDownItems.Add("New &WSL (default distro)", null,
                (s, e) => SafeCreate(ShellProfile.Wsl()));

            var resilient = ShellProfile.Cmd();
            resilient.Name = "cmd (auto-restart)";
            resilient.AutoRestart = true;
            shellMenu.DropDownItems.Add("New cmd with &auto-restart", null,
                (s, e) => SafeCreate(resilient));

            shellMenu.DropDownItems.Add(new ToolStripSeparator());
            shellMenu.DropDownItems.Add("Close acti&ve session", null,
                (s, e) => _terminal.CloseActiveSession());
            shellMenu.DropDownItems.Add(new ToolStripSeparator());
            shellMenu.DropDownItems.Add("E&xit", null, (s, e) => Close());

            var viewMenu = new ToolStripMenuItem("&View");
            foreach (var themeName in ThemeManager.Names)
            {
                string captured = themeName;
                viewMenu.DropDownItems.Add("Theme: " + captured, null,
                    (s, e) => _terminal.ApplyTheme(ThemeManager.Get(captured)));
            }

            menu.Items.Add(shellMenu);
            menu.Items.Add(viewMenu);
            return menu;
        }

        private void SafeCreate(ShellProfile profile)
        {
            try
            {
                _terminal.CreateSession(profile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Failed to start shell",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
