using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TerminalEmulator.Control.Completion
{
    public sealed class CompletionItem
    {
        /// <summary>Full display label (file or directory name).</summary>
        public string Label { get; set; }

        /// <summary>Text to inject into the PTY to complete the token.</summary>
        public string InsertText { get; set; }

        /// <summary>"directory" or "file" — used by the renderer for classification.</summary>
        public string Kind { get; set; }
    }

    /// <summary>
    /// Backend-driven filesystem completion. Given the shadow of the current
    /// input line, it extracts the trailing path token (quote- and
    /// environment-variable-aware), resolves it against the session's working
    /// directory and streams back matching entries.
    ///
    /// Extension point: implement <see cref="ICompletionSource"/> to add SSH
    /// remote or WSL path resolution and register it with the engine.
    /// </summary>
    public sealed class PathCompletionEngine
    {
        private const int MaxResults = 50;

        private readonly List<ICompletionSource> _sources = new List<ICompletionSource>();

        public void RegisterSource(ICompletionSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            _sources.Add(source);
        }

        public IReadOnlyList<CompletionItem> Complete(string line, string baseDirectory)
        {
            if (line == null) line = string.Empty;

            string token = ExtractTrailingToken(line);
            string expanded = Environment.ExpandEnvironmentVariables(token);

            foreach (var source in _sources)
            {
                var custom = source.TryComplete(expanded, baseDirectory);
                if (custom != null) return custom;
            }

            return CompleteFileSystem(expanded, baseDirectory);
        }

        private static IReadOnlyList<CompletionItem> CompleteFileSystem(string token, string baseDirectory)
        {
            var results = new List<CompletionItem>();

            try
            {
                string directoryPart;
                string prefix;

                int lastSeparator = token.LastIndexOfAny(new[] { '\\', '/' });
                if (lastSeparator >= 0)
                {
                    directoryPart = token.Substring(0, lastSeparator + 1);
                    prefix = token.Substring(lastSeparator + 1);
                }
                else
                {
                    directoryPart = string.Empty;
                    prefix = token;
                }

                string searchDirectory;
                if (Path.IsPathRooted(directoryPart))
                {
                    searchDirectory = directoryPart;
                }
                else if (!string.IsNullOrEmpty(baseDirectory))
                {
                    searchDirectory = Path.Combine(baseDirectory, directoryPart);
                }
                else
                {
                    return results;
                }

                if (!Directory.Exists(searchDirectory)) return results;

                var entries = Directory
                    .EnumerateFileSystemEntries(searchDirectory)
                    .Where(p => Path.GetFileName(p)
                        .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => !Directory.Exists(p)) // directories first
                    .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                    .Take(MaxResults);

                foreach (var entry in entries)
                {
                    string name = Path.GetFileName(entry);
                    bool isDirectory = Directory.Exists(entry);

                    // Only inject the remainder past what was already typed.
                    string remainder = name.Length >= prefix.Length
                        ? name.Substring(prefix.Length)
                        : name;

                    results.Add(new CompletionItem
                    {
                        Label = name + (isDirectory ? "\\" : string.Empty),
                        InsertText = remainder + (isDirectory ? "\\" : string.Empty),
                        Kind = isDirectory ? "directory" : "file"
                    });
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            catch (ArgumentException) { }

            return results;
        }

        /// <summary>
        /// Extracts the last token of the line, honoring double quotes so that
        /// paths with spaces ("C:\Program Files\...) complete correctly.
        /// </summary>
        internal static string ExtractTrailingToken(string line)
        {
            if (line.Length == 0) return string.Empty;

            int quoteCount = line.Count(c => c == '"');
            if (quoteCount % 2 == 1)
            {
                // Inside an open quote: token starts after the last quote.
                return line.Substring(line.LastIndexOf('"') + 1);
            }

            int lastSpace = line.LastIndexOf(' ');
            string token = lastSpace >= 0 ? line.Substring(lastSpace + 1) : line;
            return token.Trim('"');
        }
    }

    /// <summary>Pluggable completion source (SSH remote filesystems, WSL paths, ...).</summary>
    public interface ICompletionSource
    {
        /// <summary>Returns completions, or null to fall through to the next source.</summary>
        IReadOnlyList<CompletionItem> TryComplete(string token, string baseDirectory);
    }
}
