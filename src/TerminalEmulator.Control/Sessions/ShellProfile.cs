using System;

namespace TerminalEmulator.Control.Sessions
{
    /// <summary>
    /// Describes how a shell session is launched.
    /// </summary>
    public sealed class ShellProfile
    {
        /// <summary>Display name shown in the tab strip.</summary>
        public string Name { get; set; }

        /// <summary>Full command line handed to CreateProcess.</summary>
        public string CommandLine { get; set; }

        /// <summary>Initial working directory (also the root for path completion).</summary>
        public string StartDirectory { get; set; }

        /// <summary>Restart the shell automatically if the process exits or crashes.</summary>
        public bool AutoRestart { get; set; }

        /// <summary>Maximum automatic restarts before giving up (guards crash loops).</summary>
        public int MaxAutoRestarts { get; set; }

        /// <summary>Short identifier used by the front end ("cmd", "powershell", ...).</summary>
        public string Kind { get; set; }

        public ShellProfile()
        {
            MaxAutoRestarts = 5;
            StartDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        public static ShellProfile Cmd()
        {
            return new ShellProfile
            {
                Name = "Command Prompt",
                Kind = "cmd",
                CommandLine = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe")
            };
        }

        public static ShellProfile PowerShell()
        {
            // PowerShell tracks its location internally and never updates the
            // Win32 process working directory, so reading the child's CWD from
            // the PEB always returns the launch directory. The fix is shell
            // integration (same as Windows Terminal): redefine `prompt` to
            // emit an OSC 9;9 working-directory report on every prompt, which
            // WorkingDirectoryTracker picks out of the output stream. The
            // injected function reproduces the default "PS C:\path> " prompt
            // (including nesting), and -EncodedCommand sidesteps command-line
            // quoting entirely. The user's profile still loads first, so this
            // redefinition wins even when a profile defines its own prompt.
            const string integration =
                "function prompt {" +
                " $l = $executionContext.SessionState.Path.CurrentLocation;" +
                " $p = \"PS $l$('>' * ($nestedPromptLevel + 1)) \";" +
                " \"$([char]27)]9;9;`\"$l`\"$([char]27)\\$p\"" +
                " }";

            string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(integration));

            return new ShellProfile
            {
                Name = "PowerShell",
                Kind = "powershell",
                CommandLine = Environment.ExpandEnvironmentVariables(
                    @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe") +
                    " -NoLogo -NoExit -EncodedCommand " + encoded
            };
        }

        /// <summary>Launches a WSL distribution (default distro when name is null).</summary>
        public static ShellProfile Wsl(string distribution = null)
        {
            string args = string.IsNullOrWhiteSpace(distribution) ? string.Empty : " -d " + distribution;
            return new ShellProfile
            {
                Name = string.IsNullOrWhiteSpace(distribution) ? "WSL" : "WSL (" + distribution + ")",
                Kind = "wsl",
                CommandLine = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\wsl.exe") + args
            };
        }

        /// <summary>Launches the Windows OpenSSH client against a target such as user@host.</summary>
        public static ShellProfile Ssh(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("SSH target is required.", nameof(target));
            return new ShellProfile
            {
                Name = "SSH " + target,
                Kind = "ssh",
                CommandLine = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\OpenSSH\ssh.exe") + " " + target
            };
        }

        public static ShellProfile Custom(string name, string commandLine, string startDirectory = null)
        {
            return new ShellProfile
            {
                Name = name,
                Kind = "custom",
                CommandLine = commandLine,
                StartDirectory = startDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
        }
    }
}
