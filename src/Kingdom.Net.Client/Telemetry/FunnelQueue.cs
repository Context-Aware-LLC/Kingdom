using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Kingdom.Net.Client.Auth;

namespace Kingdom.Net.Client.Telemetry
{
    /// <summary>
    /// Where the funnel's queue lives between processes. One JSON row per line. Any member may
    /// throw; <see cref="FunnelQueue"/> catches every one of them.
    /// </summary>
    public interface IFunnelQueueStore
    {
        /// <summary>Every stored line, oldest first. Empty when nothing was ever stored.</summary>
        IReadOnlyList<string> Load();

        /// <summary>Add one line at the end, durably, before returning.</summary>
        void Append(string line);

        /// <summary>Make the stored lines exactly <paramref name="lines"/>.</summary>
        void Replace(IReadOnlyList<string> lines);
    }

    /// <summary>
    /// <see cref="IFunnelQueueStore"/> on one file. Appends go straight to the file, so the
    /// <c>exit</c> row written from <c>OnApplicationQuit</c> is on disk before the process goes.
    /// A replace writes a sibling <c>.tmp</c> first and copies it over, so a crash mid-write leaves
    /// the old queue rather than half of the new one.
    /// </summary>
    public sealed class FileFunnelQueueStore : IFunnelQueueStore
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public string Path { get; }

        public FileFunnelQueueStore(string path)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
        }

        public IReadOnlyList<string> Load()
        {
            if (!File.Exists(Path)) return Array.Empty<string>();
            return File.ReadAllLines(Path, Utf8);
        }

        public void Append(string line)
        {
            EnsureDirectory();
            File.AppendAllText(Path, line + "\n", Utf8);
        }

        public void Replace(IReadOnlyList<string> lines)
        {
            EnsureDirectory();
            var sb = new StringBuilder();
            foreach (string line in lines) sb.Append(line).Append('\n');
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Utf8);
            File.Copy(tmp, Path, overwrite: true);
            File.Delete(tmp);
        }

        private void EnsureDirectory()
        {
            string? dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
    }

    /// <summary>
    /// The funnel's durable row queue (pre-publication item 2, Durability): rows survive a relaunch
    /// with the <c>session_id</c> they were written with, and leave only when the server took them
    /// or rejected them.
    ///
    /// <para><b>Capped, oldest dropped.</b> Past <see cref="Capacity"/> the oldest rows go, so a
    /// device that is offline for weeks holds its most recent sessions, not its first.</para>
    ///
    /// <para><b>Never throws.</b> Every store failure is logged and absorbed: the in-memory queue
    /// carries on, and the rows reach the server if this process lives to flush them. Telemetry
    /// that can fail a caller is worse than telemetry that loses a row.</para>
    ///
    /// <para><b>Rows are kept as their JSON text,</b> so a reload needs no parsing beyond a sanity
    /// check — and a row from an earlier process cannot pick up this process's session id, because
    /// nothing here ever rewrites one.</para>
    /// </summary>
    public sealed class FunnelQueue
    {
        public const int DefaultCapacity = 500;

        private readonly IFunnelQueueStore _store;
        private readonly Action<string>? _log;
        private readonly object _gate = new object();
        private readonly List<Entry> _rows = new List<Entry>();

        public int Capacity { get; }

        /// <summary>One queued row. A reference, so a flush removes exactly the rows it sent even
        /// when rows were added or trimmed while it was in flight.</summary>
        public sealed class Entry
        {
            public string Json { get; }
            internal Entry(string json) => Json = json;
        }

        public FunnelQueue(IFunnelQueueStore store, int capacity = DefaultCapacity, Action<string>? log = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            Capacity = capacity < 1 ? 1 : capacity;
            _log = log;
            Load();
        }

        public int Count
        {
            get { lock (_gate) return _rows.Count; }
        }

        /// <summary>The queued rows' JSON, oldest first.</summary>
        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate)
            {
                var copy = new List<string>(_rows.Count);
                foreach (Entry e in _rows) copy.Add(e.Json);
                return copy;
            }
        }

        /// <summary>Queue one row, durably. Never throws.</summary>
        public void Enqueue(FunnelRow row)
        {
            if (row == null) return;
            string json;
            try { json = row.ToJson(); }
            catch (Exception ex) { Log("could not encode a row: " + ex.Message); return; }
            Enqueue(json);
        }

        /// <summary>Queue one already-encoded row. Never throws.</summary>
        public void Enqueue(string json)
        {
            if (string.IsNullOrEmpty(json) || json.IndexOf('\n') >= 0 || json.IndexOf('\r') >= 0) return;
            lock (_gate)
            {
                _rows.Add(new Entry(json));
                if (_rows.Count > Capacity)
                {
                    int drop = _rows.Count - Capacity;
                    _rows.RemoveRange(0, drop);
                    Log("queue at its cap of " + Capacity + "; dropped the oldest " + drop + " row(s).");
                    Persist();
                    return;
                }
                try { _store.Append(json); }
                catch (Exception ex) { Log("could not append a row to disk: " + ex.Message); }
            }
        }

        /// <summary>Up to <paramref name="max"/> rows from the front, for a flush to send.</summary>
        public IReadOnlyList<Entry> Peek(int max)
        {
            lock (_gate)
            {
                int n = Math.Min(Math.Max(0, max), _rows.Count);
                return _rows.GetRange(0, n);
            }
        }

        /// <summary>Remove exactly these rows (sent, or rejected), and write the rest back.
        /// Rows already trimmed by the cap are simply not there. Never throws.</summary>
        public void Remove(IReadOnlyList<Entry> entries)
        {
            if (entries == null || entries.Count == 0) return;
            lock (_gate)
            {
                var gone = new HashSet<Entry>(entries);
                int removed = _rows.RemoveAll(gone.Contains);
                if (removed > 0) Persist();
            }
        }

        private void Load()
        {
            IReadOnlyList<string> lines;
            try { lines = _store.Load(); }
            catch (Exception ex)
            {
                Log("could not read the stored queue; starting empty: " + ex.Message);
                return;
            }

            int skipped = 0;
            foreach (string line in lines)
            {
                if (IsRow(line)) _rows.Add(new Entry(line.Trim()));
                else if (!string.IsNullOrWhiteSpace(line)) skipped++;
            }

            bool trimmed = false;
            if (_rows.Count > Capacity)
            {
                _rows.RemoveRange(0, _rows.Count - Capacity);
                trimmed = true;
            }
            if (skipped > 0) Log("skipped " + skipped + " unreadable stored row(s).");
            // A torn last line (a crash mid-append) or a trim is written out now, so the next append
            // does not land on the end of a half line.
            if (skipped > 0 || trimmed) Persist();
        }

        /// <summary>A stored line is replayed only if it is a JSON object naming its milestone and
        /// session. A torn line from a crash mid-write fails this and is dropped, rather than poisoning
        /// every later batch it would ride in.</summary>
        private static bool IsRow(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (!AuthJson.TryReadTopLevel(line, out var fields)) return false;
            return AuthJson.TryString(fields, "milestone", out string milestone) && milestone.Length > 0 &&
                   AuthJson.TryString(fields, "session_id", out string session) && session.Length > 0;
        }

        private void Persist()
        {
            var lines = new List<string>(_rows.Count);
            foreach (Entry e in _rows) lines.Add(e.Json);
            try { _store.Replace(lines); }
            catch (Exception ex) { Log("could not rewrite the stored queue: " + ex.Message); }
        }

        private void Log(string message)
        {
            try { _log?.Invoke("[Funnel] " + message); }
            catch { /* a broken logger must not fail the caller either */ }
        }
    }
}
