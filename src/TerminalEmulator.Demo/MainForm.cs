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

            var fileMenu = new ToolStripMenuItem("&File");
            fileMenu.DropDownItems.Add("&Save Workspace…", null, (s, e) => SaveWorkspace());
            fileMenu.DropDownItems.Add("&Open Workspace…", null, (s, e) => OpenWorkspace());
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add("E&xit", null, (s, e) => Close());

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

            menu.Items.Add(fileMenu);
            menu.Items.Add(shellMenu);
            menu.Items.Add(viewMenu);
            return menu;
        }

        // ---------------------------------------------------------------
        // Workspace save / restore
        // ---------------------------------------------------------------

        private const string WorkspaceFilter = "Terminal workspace (*.terminal.json)|*.terminal.json|JSON files (*.json)|*.json|All files (*.*)|*.*";

        private void SaveWorkspace()
        {
            var workspace = TerminalWorkspace.Capture(_terminal);
            if (workspace.Tabs.Count == 0)
            {
                MessageBox.Show(this, "There are no open tabs to save.", "Save Workspace",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dialog = new SaveFileDialog
            {
                Title = "Save Workspace",
                Filter = WorkspaceFilter,
                DefaultExt = "terminal.json",
                FileName = "workspace.terminal.json",
                AddExtension = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    workspace.Save(dialog.FileName);
                    _status.Text = "Saved " + workspace.Tabs.Count + " tab(s) to " +
                                   System.IO.Path.GetFileName(dialog.FileName) + ".";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Failed to save workspace",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void OpenWorkspace()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Open Workspace",
                Filter = WorkspaceFilter,
                CheckFileExists = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                TerminalWorkspace workspace;
                try
                {
                    workspace = TerminalWorkspace.Load(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Failed to open workspace",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (workspace.Tabs.Count > 0 && _terminal.Sessions.Count > 0)
                {
                    var answer = MessageBox.Show(this,
                        "Restoring this workspace will close the " + _terminal.Sessions.Count +
                        " currently open tab(s) and open " + workspace.Tabs.Count +
                        " saved tab(s). Continue?",
                        "Open Workspace", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer != DialogResult.Yes) return;
                }

                var warnings = workspace.ApplyTo(_terminal);
                _status.Text = "Restored " + workspace.Tabs.Count + " tab(s) from " +
                               System.IO.Path.GetFileName(dialog.FileName) + ".";

                if (warnings.Count > 0)
                {
                    MessageBox.Show(this, string.Join(Environment.NewLine, warnings),
                        "Workspace restored with warnings",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
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
