using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace TerminalEmulator.Control.Theming
{
    /// <summary>
    /// A terminal theme: colors (full 16-color ANSI palette override), cursor
    /// style and font settings. Serializes directly to the shape xterm.js
    /// expects, so themes can be hot-reloaded over the bridge at runtime.
    /// </summary>
    public sealed class TerminalTheme
    {
        [JsonProperty("name")] public string Name { get; set; }

        [JsonProperty("background")] public string Background { get; set; }
        [JsonProperty("foreground")] public string Foreground { get; set; }
        [JsonProperty("cursor")] public string Cursor { get; set; }
        [JsonProperty("cursorAccent")] public string CursorAccent { get; set; }
        [JsonProperty("selectionBackground")] public string SelectionBackground { get; set; }

        [JsonProperty("black")] public string Black { get; set; }
        [JsonProperty("red")] public string Red { get; set; }
        [JsonProperty("green")] public string Green { get; set; }
        [JsonProperty("yellow")] public string Yellow { get; set; }
        [JsonProperty("blue")] public string Blue { get; set; }
        [JsonProperty("magenta")] public string Magenta { get; set; }
        [JsonProperty("cyan")] public string Cyan { get; set; }
        [JsonProperty("white")] public string White { get; set; }
        [JsonProperty("brightBlack")] public string BrightBlack { get; set; }
        [JsonProperty("brightRed")] public string BrightRed { get; set; }
        [JsonProperty("brightGreen")] public string BrightGreen { get; set; }
        [JsonProperty("brightYellow")] public string BrightYellow { get; set; }
        [JsonProperty("brightBlue")] public string BrightBlue { get; set; }
        [JsonProperty("brightMagenta")] public string BrightMagenta { get; set; }
        [JsonProperty("brightCyan")] public string BrightCyan { get; set; }
        [JsonProperty("brightWhite")] public string BrightWhite { get; set; }

        /// <summary>"block", "underline" or "bar".</summary>
        [JsonProperty("cursorStyle")] public string CursorStyle { get; set; }

        [JsonProperty("fontFamily")] public string FontFamily { get; set; }
        [JsonProperty("fontSize")] public int FontSize { get; set; }

        public TerminalTheme()
        {
            CursorStyle = "block";
            FontFamily = "Cascadia Mono, Consolas, monospace";
            FontSize = 14;
        }

        public static TerminalTheme FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("Theme JSON is empty.", nameof(json));
            var theme = JsonConvert.DeserializeObject<TerminalTheme>(json);
            if (theme == null || string.IsNullOrWhiteSpace(theme.Name))
                throw new FormatException("Theme JSON must include a 'name'.");
            return theme;
        }

        public string ToJson()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }
    }

    /// <summary>Registry of built-in and user-registered themes.</summary>
    public static class ThemeManager
    {
        private static readonly Dictionary<string, TerminalTheme> Registry =
            new Dictionary<string, TerminalTheme>(StringComparer.OrdinalIgnoreCase);

        static ThemeManager()
        {
            Register(DarkPlus);
            Register(OneLight);
            Register(SolarizedDark);
        }

        public static TerminalTheme DarkPlus
        {
            get
            {
                return new TerminalTheme
                {
                    Name = "dark-plus",
                    Background = "#1e1e1e",
                    Foreground = "#cccccc",
                    Cursor = "#aeafad",
                    CursorAccent = "#1e1e1e",
                    SelectionBackground = "#264f78",
                    Black = "#000000", Red = "#cd3131", Green = "#0dbc79", Yellow = "#e5e510",
                    Blue = "#2472c8", Magenta = "#bc3fbc", Cyan = "#11a8cd", White = "#e5e5e5",
                    BrightBlack = "#666666", BrightRed = "#f14c4c", BrightGreen = "#23d18b",
                    BrightYellow = "#f5f543", BrightBlue = "#3b8eea", BrightMagenta = "#d670d6",
                    BrightCyan = "#29b8db", BrightWhite = "#ffffff"
                };
            }
        }

        public static TerminalTheme OneLight
        {
            get
            {
                return new TerminalTheme
                {
                    Name = "one-light",
                    Background = "#fafafa",
                    Foreground = "#383a42",
                    Cursor = "#526eff",
                    CursorAccent = "#fafafa",
                    SelectionBackground = "#d0d7e2",
                    Black = "#383a42", Red = "#e45649", Green = "#50a14f", Yellow = "#c18401",
                    Blue = "#4078f2", Magenta = "#a626a4", Cyan = "#0184bc", White = "#fafafa",
                    BrightBlack = "#5c6370", BrightRed = "#e06c75", BrightGreen = "#98c379",
                    BrightYellow = "#e5c07b", BrightBlue = "#61afef", BrightMagenta = "#c678dd",
                    BrightCyan = "#56b6c2", BrightWhite = "#ffffff"
                };
            }
        }

        public static TerminalTheme SolarizedDark
        {
            get
            {
                return new TerminalTheme
                {
                    Name = "solarized-dark",
                    Background = "#002b36",
                    Foreground = "#839496",
                    Cursor = "#93a1a1",
                    CursorAccent = "#002b36",
                    SelectionBackground = "#073642",
                    Black = "#073642", Red = "#dc322f", Green = "#859900", Yellow = "#b58900",
                    Blue = "#268bd2", Magenta = "#d33682", Cyan = "#2aa198", White = "#eee8d5",
                    BrightBlack = "#586e75", BrightRed = "#cb4b16", BrightGreen = "#586e75",
                    BrightYellow = "#657b83", BrightBlue = "#839496", BrightMagenta = "#6c71c4",
                    BrightCyan = "#93a1a1", BrightWhite = "#fdf6e3"
                };
            }
        }

        public static void Register(TerminalTheme theme)
        {
            if (theme == null) throw new ArgumentNullException(nameof(theme));
            if (string.IsNullOrWhiteSpace(theme.Name)) throw new ArgumentException("Theme requires a name.");
            lock (Registry) { Registry[theme.Name] = theme; }
        }

        public static TerminalTheme Get(string name)
        {
            lock (Registry)
            {
                TerminalTheme theme;
                return Registry.TryGetValue(name ?? string.Empty, out theme) ? theme : null;
            }
        }

        public static IReadOnlyList<string> Names
        {
            get { lock (Registry) { return new List<string>(Registry.Keys); } }
        }
    }
}
