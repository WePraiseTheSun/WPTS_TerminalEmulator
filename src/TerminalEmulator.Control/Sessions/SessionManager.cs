using System;
using System.Collections.Generic;
using System.Linq;

namespace TerminalEmulator.Control.Sessions
{
    /// <summary>
    /// Owns the set of live terminal sessions and the notion of the "active"
    /// (renderer-attached) session. Exactly one session is attached at a time;
    /// background sessions keep running and buffering into their replay caches.
    /// </summary>
    public sealed class SessionManager : IDisposable
    {
        private readonly Dictionary<string, TerminalSession> _sessions =
            new Dictionary<string, TerminalSession>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>(); // stable tab order
        private readonly object _sync = new object();
        private string _activeSessionId;
        private bool _disposed;

        /// <summary>Output from the currently attached session.</summary>
        public event Action<TerminalSession, string> SessionOutput;

        /// <summary>A session's shell exited. Arguments: session, exit code, will-restart.</summary>
        public event Action<TerminalSession, int, bool> SessionExited;

        public string ActiveSessionId
        {
            get { lock (_sync) { return _activeSessionId; } }
        }

        public TerminalSession ActiveSession
        {
            get
            {
                lock (_sync)
                {
                    if (_activeSessionId == null) return null;
                    TerminalSession session;
                    return _sessions.TryGetValue(_activeSessionId, out session) ? session : null;
                }
            }
        }

        /// <summary>Live sessions in stable tab (creation) order.</summary>
        public IReadOnlyList<TerminalSession> Sessions
        {
            get
            {
                lock (_sync)
                {
                    return _order.Select(id => _sessions[id]).ToList();
                }
            }
        }

        public TerminalSession GetSession(string id)
        {
            if (id == null) return null;
            lock (_sync)
            {
                TerminalSession session;
                return _sessions.TryGetValue(id, out session) ? session : null;
            }
        }

        /// <summary>Creates and starts a session. Does not attach it.</summary>
        public TerminalSession CreateSession(ShellProfile profile, int cols, int rows)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            var session = new TerminalSession(profile);
            session.Output += OnSessionOutput;
            session.Exited += OnSessionExited;
            session.Start(cols, rows);

            lock (_sync)
            {
                if (_disposed)
                {
                    session.Dispose();
                    throw new ObjectDisposedException(nameof(SessionManager));
                }
                _sessions[session.Id] = session;
                _order.Add(session.Id);
            }

            return session;
        }

        /// <summary>
        /// Switches the renderer to <paramref name="sessionId"/>. Detaches the
        /// previous session and returns the new session's replay buffer, or
        /// null when the session does not exist.
        /// </summary>
        public string SwitchTo(string sessionId, out TerminalSession attached)
        {
            lock (_sync)
            {
                attached = null;
                TerminalSession target;
                if (!_sessions.TryGetValue(sessionId, out target)) return null;

                TerminalSession previous;
                if (_activeSessionId != null &&
                    _sessions.TryGetValue(_activeSessionId, out previous) &&
                    previous != target)
                {
                    previous.Detach();
                }

                _activeSessionId = sessionId;
                attached = target;
                return target.Attach();
            }
        }

        /// <summary>
        /// Closes a session. Returns the id of the session that should become
        /// active next (may be null when no sessions remain).
        /// </summary>
        public string CloseSession(string sessionId)
        {
            TerminalSession session;
            string nextActive;

            lock (_sync)
            {
                if (!_sessions.TryGetValue(sessionId, out session)) return _activeSessionId;

                int index = _order.IndexOf(sessionId);
                _sessions.Remove(sessionId);
                if (index >= 0) _order.RemoveAt(index);
                if (_activeSessionId == sessionId) _activeSessionId = null;

                // Prefer the closed tab's right neighbor, then the last tab.
                string neighbor = null;
                if (_order.Count > 0)
                {
                    neighbor = index >= 0 && index < _order.Count ? _order[index] : _order[_order.Count - 1];
                }
                nextActive = _activeSessionId ?? neighbor;
            }

            session.Output -= OnSessionOutput;
            session.Exited -= OnSessionExited;
            session.Dispose();

            return nextActive;
        }

        private void OnSessionOutput(TerminalSession session, string payload)
        {
            var handler = SessionOutput;
            if (handler != null) handler(session, payload);
        }

        private void OnSessionExited(TerminalSession session, int exitCode, bool willRestart)
        {
            var handler = SessionExited;
            if (handler != null) handler(session, exitCode, willRestart);
        }

        public void Dispose()
        {
            List<TerminalSession> sessions;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                sessions = _sessions.Values.ToList();
                _sessions.Clear();
                _order.Clear();
                _activeSessionId = null;
            }

            foreach (var session in sessions)
            {
                session.Output -= OnSessionOutput;
                session.Exited -= OnSessionExited;
                session.Dispose();
            }
        }
    }
}
