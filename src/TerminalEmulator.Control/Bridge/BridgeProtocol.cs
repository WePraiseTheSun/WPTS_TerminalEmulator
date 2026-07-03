namespace TerminalEmulator.Control.Bridge
{
    /// <summary>
    /// Message type identifiers for the bidirectional async JSON message bus
    /// between the .NET host and the xterm.js renderer. Kept in one place so
    /// the protocol is auditable; the JavaScript counterpart lives in
    /// Assets/terminal.js.
    /// </summary>
    public static class BridgeProtocol
    {
        // .NET -> JavaScript
        public const string Init = "init";                     // handshake reply: config + session list
        public const string Data = "data";                     // terminal output stream chunk
        public const string Replay = "replay";                 // reset renderer + replay scrollback (session switch)
        public const string Sessions = "sessions";             // session list / active session update
        public const string Config = "config";                 // theme / font hot reload
        public const string Completions = "completions";       // completion suggestions
        public const string Exited = "exited";                 // shell process exited

        // JavaScript -> .NET
        public const string Ready = "ready";                   // renderer loaded, bridge live
        public const string Input = "input";                   // keystrokes / raw input for the PTY
        public const string Resize = "resize";                 // cols/rows changed
        public const string Paste = "paste";                   // clipboard paste routed to PTY stdin
        public const string Ack = "ack";                       // renderer finished writing N chars (backpressure credit)
        public const string CreateSession = "createSession";   // open a new shell tab
        public const string CloseSession = "closeSession";     // close a shell tab
        public const string SwitchSession = "switchSession";   // activate a shell tab
        public const string RequestCompletion = "requestCompletion"; // path completion query
        public const string SetTheme = "setTheme";             // switch to a registered theme by name
    }
}
