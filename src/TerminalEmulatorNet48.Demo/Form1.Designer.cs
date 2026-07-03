namespace TerminalEmulatorNet48.Demo
{
    partial class Form1
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            TerminalEmulator.Control.Sessions.ShellProfile shellProfile1 = new TerminalEmulator.Control.Sessions.ShellProfile();
            TerminalEmulator.Control.Theming.TerminalTheme terminalTheme1 = new TerminalEmulator.Control.Theming.TerminalTheme();
            this.terminalControl1 = new TerminalEmulator.Control.TerminalControl();
            this.SuspendLayout();
            // 
            // terminalControl1
            // 
            this.terminalControl1.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(30)))), ((int)(((byte)(30)))), ((int)(((byte)(30)))));
            shellProfile1.AutoRestart = false;
            shellProfile1.CommandLine = "C:\\WINDOWS\\System32\\cmd.exe";
            shellProfile1.Kind = "cmd";
            shellProfile1.MaxAutoRestarts = 5;
            shellProfile1.Name = "Command Prompt";
            shellProfile1.StartDirectory = "C:\\Users\\jerry";
            this.terminalControl1.DefaultProfile = shellProfile1;
            this.terminalControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.terminalControl1.Location = new System.Drawing.Point(0, 0);
            this.terminalControl1.Name = "terminalControl1";
            this.terminalControl1.Size = new System.Drawing.Size(800, 450);
            this.terminalControl1.TabIndex = 0;
            terminalTheme1.Background = "#1e1e1e";
            terminalTheme1.Black = "#000000";
            terminalTheme1.Blue = "#2472c8";
            terminalTheme1.BrightBlack = "#666666";
            terminalTheme1.BrightBlue = "#3b8eea";
            terminalTheme1.BrightCyan = "#29b8db";
            terminalTheme1.BrightGreen = "#23d18b";
            terminalTheme1.BrightMagenta = "#d670d6";
            terminalTheme1.BrightRed = "#f14c4c";
            terminalTheme1.BrightWhite = "#ffffff";
            terminalTheme1.BrightYellow = "#f5f543";
            terminalTheme1.Cursor = "#aeafad";
            terminalTheme1.CursorAccent = "#1e1e1e";
            terminalTheme1.CursorStyle = "block";
            terminalTheme1.Cyan = "#11a8cd";
            terminalTheme1.FontFamily = "Cascadia Mono, Consolas, monospace";
            terminalTheme1.FontSize = 14;
            terminalTheme1.Foreground = "#cccccc";
            terminalTheme1.Green = "#0dbc79";
            terminalTheme1.Magenta = "#bc3fbc";
            terminalTheme1.Name = "dark-plus";
            terminalTheme1.Red = "#cd3131";
            terminalTheme1.SelectionBackground = "#264f78";
            terminalTheme1.White = "#e5e5e5";
            terminalTheme1.Yellow = "#e5e510";
            this.terminalControl1.Theme = terminalTheme1;
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Controls.Add(this.terminalControl1);
            this.Name = "Form1";
            this.Text = "Form1";
            this.ResumeLayout(false);

        }

        #endregion

        private TerminalEmulator.Control.TerminalControl terminalControl1;
    }
}

