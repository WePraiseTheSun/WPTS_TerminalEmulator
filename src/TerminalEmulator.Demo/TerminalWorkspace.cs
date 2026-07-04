using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using TerminalEmulator.Control;
using TerminalEmulator.Control.Sessions;

namespace TerminalEmulator.Demo
{
    /// <summary>
    /// A saved terminal workspace: the ordered set of open tabs, each with its
    /// shell profile and the working directory it was in when saved.
    /// </summary>
    public sealed class TerminalWorkspace
    {
        [JsonProperty("version")]
        public int Version { get; set; }

        [JsonProperty("savedAtUtc")]
        public DateTime SavedAtUtc { get; set; }

        [JsonProperty("activeTabIndex")]
        public int ActiveTabIndex { get; set; }

        [JsonProperty("tabs")]
        public List<WorkspaceTab> Tabs { get; set; }

        public TerminalWorkspace()
        {
            Version = 1;
            Tabs = new List<WorkspaceTab>();
        }

        public sealed class WorkspaceTab
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("kind")]
            public string Kind { get; set; }

            [JsonProperty("commandLine")]
            public string CommandLine { get; set; }

            [JsonProperty("workingDirectory")]
            public string WorkingDirectory { get; set; }

            [JsonProperty("autoRestart")]
            public bool AutoRestart { get; set; }

            [JsonProperty("maxAutoRestarts")]
            public int MaxAutoRestarts { get; set; }
        }

        // -------------------------------------------------------------------
        // Capture / apply
        // -------------------------------------------------------------------

        /// <summary>Captures the terminal's current tabs, in order, with live working directories.</summary>
        public static TerminalWorkspace Capture(TerminalControl terminal)
        {
            var workspace = new TerminalWorkspace { SavedAtUtc = DateTime.UtcNow };

            var sessions = terminal.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.Id == terminal.ActiveSessionId) workspace.ActiveTabIndex = i;

                workspace.Tabs.Add(new WorkspaceTab
                {
                    Name = session.Profile.Name,
                    Kind = session.Profile.Kind,
                    CommandLine = session.Profile.CommandLine,
                    WorkingDirectory = session.GetWorkingDirectory(),
                    AutoRestart = session.Profile.AutoRestart,
                    MaxAutoRestarts = session.Profile.MaxAutoRestarts
                });
            }

            return workspace;
        }

        /// <summary>
        /// Restores this workspace into the terminal: closes existing tabs,
        /// recreates the saved tabs in order (each starting in its saved
        /// working directory), then activates the saved active tab.
        /// Returns warnings for tabs that could not be restored.
        /// </summary>
        public IList<string> ApplyTo(TerminalControl terminal)
        {
            var warnings = new List<string>();
            if (Tabs == null || Tabs.Count == 0)
            {
                warnings.Add("The workspace file contains no tabs.");
                return warnings;
            }

            // Close current tabs (snapshot first: closing mutates the list).
            var existing = new List<string>();
            foreach (var session in terminal.Sessions) existing.Add(session.Id);
            foreach (var id in existing) terminal.CloseSession(id);

            var createdIds = new List<string>();

            foreach (var tab in Tabs)
            {
                var profile = ToProfile(tab, warnings);
                try
                {
                    var session = terminal.CreateSession(profile);
                    createdIds.Add(session.Id);
                }
                catch (Exception ex)
                {
                    warnings.Add("Could not restore tab '" + tab.Name + "': " + ex.Message);
                }
            }

            if (createdIds.Count > 0)
            {
                int index = ActiveTabIndex;
                if (index < 0 || index >= createdIds.Count) index = 0;
                terminal.SwitchToSession(createdIds[index]);
            }

            return warnings;
        }

        private static ShellProfile ToProfile(WorkspaceTab tab, IList<string> warnings)
        {
            // Rebuild from the preset for the kind (keeps command lines
            // correct across machines), fall back to the saved command line.
            ShellProfile profile;
            switch ((tab.Kind ?? "custom").ToLowerInvariant())
            {
                case "cmd": profile = ShellProfile.Cmd(); break;
                case "powershell": profile = ShellProfile.PowerShell(); break;
                case "wsl": profile = ShellProfile.Wsl(); break;
                default:
                    profile = ShellProfile.Custom(
                        string.IsNullOrWhiteSpace(tab.Name) ? "Custom" : tab.Name,
                        tab.CommandLine);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(tab.Name)) profile.Name = tab.Name;
            profile.AutoRestart = tab.AutoRestart;
            if (tab.MaxAutoRestarts > 0) profile.MaxAutoRestarts = tab.MaxAutoRestarts;

            // Restore the working directory. WSL sessions inherit wsl.exe's
            // Windows CWD, so setting it here lands bash in the mapped
            // /mnt/... directory as well.
            if (!string.IsNullOrWhiteSpace(tab.WorkingDirectory))
            {
                if (Directory.Exists(tab.WorkingDirectory))
                {
                    profile.StartDirectory = tab.WorkingDirectory;
                }
                else
                {
                    warnings.Add("Tab '" + profile.Name + "': saved directory no longer exists (" +
                                 tab.WorkingDirectory + "); using the default instead.");
                }
            }

            return profile;
        }

        // -------------------------------------------------------------------
        // File I/O
        // -------------------------------------------------------------------

        public void Save(string path)
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        public static TerminalWorkspace Load(string path)
        {
            var workspace = JsonConvert.DeserializeObject<TerminalWorkspace>(File.ReadAllText(path));
            if (workspace == null) throw new InvalidDataException("The file is not a valid workspace.");
            if (workspace.Tabs == null) workspace.Tabs = new List<WorkspaceTab>();
            if (workspace.Version > 1)
            {
                throw new InvalidDataException(
                    "Workspace version " + workspace.Version + " is newer than this application supports.");
            }
            return workspace;
        }
    }
}
