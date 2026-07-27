using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RomboTool.Engine;

/// <summary>Search parameters for a grep run.</summary>
public sealed class GrepOptions
{
    /// <summary>A folder OR a single file. Auto-detected.</summary>
    public string Path = "";
    /// <summary>Set when the user explicitly picked one file: bypasses size cap + binary sniff.</summary>
    public bool IsSingleFile;
    public string FilePatterns = "*";   // space-separated DOS-style globs, e.g. "*.txt *.log"
    public string Text = "";
    public bool Regex;
    public bool IgnoreCase = true;
    public bool Recurse = true;
    public bool InvertMatch;
    public bool WholeWord;
    public bool Multiline;
    /// <summary>Stop after this many matches (0 = unlimited). Keeps huge-file searches snappy.</summary>
    public long MaxMatches;

    /// <summary>True when the raw-byte fast path applies (plain literal, no word boundary).</summary>
    public bool UsesByteMatcher => !Regex && !InvertMatch && !WholeWord;
}

/// <summary>A single matching line, shaped for the results grid.</summary>
public readonly record struct GrepMatch(
    string FullPath,
    string FileName,
    long LineNumber,
    string Line,           // full decoded line (capped) — for "copy line" / send-to-search
    string Preview,        // snippet centered on the first match ("…context match context…")
    int MatchStart,        // offset of the match within Preview (for highlighting)
    int MatchLength,
    int LineLength,        // length of the full original line
    int Occurrences)       // number of matches on the line
{
    /// <summary>The matched substring, as shown highlighted in the preview.</summary>
    public string MatchText =>
        MatchLength > 0 && MatchStart >= 0 && MatchStart + MatchLength <= Preview.Length
            ? Preview.Substring(MatchStart, MatchLength) : "";
}

/// <summary>Live progress snapshot pushed from the search worker(s).</summary>
/// <param name="BytesDone">Progress toward <paramref name="BytesTotal"/> (drives the bar; reaches total on completion).</param>
/// <param name="BytesScanned">Bytes actually read from disk (drives the throughput / data-read readout).</param>
public readonly record struct GrepProgress(
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    long BytesScanned,
    long LinesScanned,
    int FilesWithMatches,
    long MatchCount,
    string CurrentFile,
    long CurrentLine);

/// <summary>
/// Cross-platform file searcher tuned for very large, messy files (multi-GB combo dumps
/// with embedded NUL bytes and CRLF lines). Reads through a raw byte buffer — never
/// materialising a whole file or an unbounded line in memory — and, for plain literal
/// searches, scans the raw bytes so non-matching lines are never decoded.
/// </summary>
public static class GrepEngine
{
    /// <summary>Folder-mode files bigger than this are skipped (single-file mode has no cap).</summary>
    public const long FolderMaxFileSize = 8L * 1024 * 1024 * 1024;

    const int ReadBufferSize = 1 << 20;          // 1 MiB read chunks
    const int MaxLineBytes = 64 * 1024;          // longest line we keep; overflow is truncated
    const int PreviewContextBefore = 48;         // chars kept before the match in a snippet
    const int PreviewContextAfter = 120;         // chars kept after the match in a snippet
    const int BinarySniffBytes = 8000;
    const long ProgressByteStep = 8L * 1024 * 1024; // push progress at least every 8 MiB

    public static Regex BuildRegex(GrepOptions o)
    {
        var opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (o.IgnoreCase) opts |= RegexOptions.IgnoreCase;
        if (o.Multiline) opts |= RegexOptions.Multiline;

        var pattern = o.Regex ? o.Text : Regex.Escape(o.Text);
        if (o.WholeWord) pattern = $@"\b(?:{pattern})\b";
        return new Regex(pattern, opts);
    }

    /// <summary>Compile space-separated DOS globs (*, ?) into a single filename regex.</summary>
    static Regex BuildGlob(string patterns)
    {
        var parts = patterns.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) parts = new[] { "*" };

        var sb = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append('|');
            var p = Regex.Escape(parts[i]).Replace("\\*", ".*").Replace("\\?", ".");
            sb.Append('(').Append('^').Append(p).Append('$').Append(')');
        }
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    public static bool IsDirectory(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
        catch { return false; }
    }

    public static List<string> EnumerateFiles(GrepOptions o)
    {
        // Single explicit file, or a path that happens to be a file.
        if (o.IsSingleFile || (File.Exists(o.Path) && !IsDirectory(o.Path)))
            return new List<string> { o.Path };

        var glob = BuildGlob(o.FilePatterns);
        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = o.Recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        };

        var result = new List<string>();
        foreach (var path in Directory.EnumerateFiles(o.Path, "*", enumOpts))
            if (glob.IsMatch(System.IO.Path.GetFileName(path)))
                result.Add(path);
        return result;
    }

    /// <summary>Shared, thread-safe search state.</summary>
    sealed class State
    {
        public long BytesTotal, BytesDone, BytesScanned, LinesScanned, MatchCount, LastReportedBytes;
        public int FilesTotal, FilesDone, FilesWithMatches;
        public volatile string CurrentFile = "";
        public long CurrentLine;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        long _lastReportMs;

        public bool ShouldReport(bool force)
        {
            if (force) return true;
            long bytes = Interlocked.Read(ref BytesDone);
            if (bytes - Interlocked.Read(ref LastReportedBytes) < ProgressByteStep) return false;
            long now = Clock.ElapsedMilliseconds;
            long last = Interlocked.Read(ref _lastReportMs);
            if (now - last < 33) return false;   // debounce to ~30 Hz
            if (Interlocked.CompareExchange(ref _lastReportMs, now, last) != last) return false;
            Interlocked.Exchange(ref LastReportedBytes, bytes);
            return true;
        }

        public GrepProgress Snapshot() => new(
            Volatile.Read(ref FilesDone), FilesTotal,
            Interlocked.Read(ref BytesDone), BytesTotal, Interlocked.Read(ref BytesScanned),
            Interlocked.Read(ref LinesScanned),
            Volatile.Read(ref FilesWithMatches), Interlocked.Read(ref MatchCount),
            CurrentFile, Volatile.Read(ref CurrentLine));
    }

    public static async Task SearchAsync(
        GrepOptions o,
        Action<List<GrepMatch>> onFileMatches,
        Action<GrepProgress> onProgress,
        CancellationToken ct,
        ManualResetEventSlim? pauseGate = null)
    {
        var files = EnumerateFiles(o);
        var state = new State { FilesTotal = files.Count };

        // Cheap pre-pass: total bytes for an accurate progress bar (Length only, no reads).
        foreach (var f in files)
            try { state.BytesTotal += new FileInfo(f).Length; } catch { }

        onProgress(state.Snapshot());

        var matcher = o.UsesByteMatcher ? new ByteMatcher(o.Text, o.IgnoreCase) : null;
        var regex = matcher == null ? BuildRegex(o) : null;

        void Report(bool force)
        {
            if (state.ShouldReport(force)) onProgress(state.Snapshot());
        }

        if (files.Count == 1)
        {
            await Task.Run(() => SearchOneFile(files[0], o, matcher, regex, onFileMatches, state, Report, ct, pauseGate));
        }
        else
        {
            var parOpts = new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
            };
            try
            {
                await Parallel.ForEachAsync(files, parOpts, (file, token) =>
                {
                    SearchOneFile(file, o, matcher, regex, onFileMatches, state, Report, token, pauseGate);
                    return ValueTask.CompletedTask;
                });
            }
            catch (OperationCanceledException) { }
        }

        onProgress(state.Snapshot());  // final, unthrottled
    }

    static void SearchOneFile(
        string file, GrepOptions o, ByteMatcher? matcher, Regex? regex,
        Action<List<GrepMatch>> onFileMatches, State state, Action<bool> report,
        CancellationToken ct, ManualResetEventSlim? pauseGate)
    {
        state.CurrentFile = System.IO.Path.GetFileName(file);
        List<GrepMatch>? matches = null;
        long length = 0, counted = 0;
        bool fileHadMatch = false;

        try
        {
            try { length = new FileInfo(file).Length; } catch { length = 0; }
            if (length == 0) return;
            if (!o.IsSingleFile && length > FolderMaxFileSize) return;

            using var stream = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                ReadBufferSize, FileOptions.SequentialScan);

            // Only sniff for binary noise in folder mode, and never reject a text-extension
            // file — combo dumps are .txt with occasional NUL noise and must stay searchable.
            if (!o.IsSingleFile && !LooksTextual(file) && IsBinary(stream))
                return;   // finally still counts the file as done

            var buf = new byte[ReadBufferSize];
            var line = new byte[MaxLineBytes];
            int lineLen = 0;
            bool overflow = false;
            long lineNumber = 0;
            int read;

            void Flush()
            {
                lineNumber++;
                Interlocked.Increment(ref state.LinesScanned);
                int len = lineLen;
                if (len > 0 && line[len - 1] == (byte)'\r') len--;
                var m = MatchLine(file, line, len, lineNumber, o, matcher, regex);
                if (m.HasValue)
                {
                    fileHadMatch = true;
                    Volatile.Write(ref state.CurrentLine, lineNumber);
                    (matches ??= new()).Add(m.Value);
                    long total = Interlocked.Increment(ref state.MatchCount);
                    if (matches.Count >= 256) { onFileMatches(matches); matches = null; }
                    if (o.MaxMatches > 0 && total >= o.MaxMatches) throw new StopSearch();
                }
                lineLen = 0;
                overflow = false;
            }

            while ((read = stream.Read(buf, 0, buf.Length)) > 0)
            {
                pauseGate?.Wait(ct);
                for (int i = 0; i < read; i++)
                {
                    byte b = buf[i];
                    if (b == (byte)'\n') Flush();
                    else if (!overflow)
                    {
                        if (lineLen < line.Length) line[lineLen++] = b;
                        else overflow = true;   // drop the rest of a pathologically long line
                    }
                }

                Interlocked.Add(ref state.BytesDone, read);
                Interlocked.Add(ref state.BytesScanned, read);
                counted += read;
                report(false);
                ct.ThrowIfCancellationRequested();
            }
            if (lineLen > 0) Flush();   // trailing line without newline
        }
        catch (StopSearch) { }
        catch (OperationCanceledException) { }
        catch { /* unreadable file: skip, keep going */ }
        finally
        {
            if (matches is { Count: > 0 }) onFileMatches(matches);
            // Account for bytes we didn't stream (binary/oversize skip, early stop, errors)
            // so the aggregate progress bar always reaches its total.
            if (length > counted) Interlocked.Add(ref state.BytesDone, length - counted);
            if (fileHadMatch) Interlocked.Increment(ref state.FilesWithMatches);
            Interlocked.Increment(ref state.FilesDone);
            report(false);   // throttled; SearchAsync sends the definitive final snapshot
        }
    }

    sealed class StopSearch : Exception { }

    static GrepMatch? MatchLine(
        string file, byte[] line, int len, long lineNumber,
        GrepOptions o, ByteMatcher? matcher, Regex? regex)
    {
        if (matcher != null)
        {
            // Fast path: detect on raw bytes, only decode + measure when there's a hit.
            if (matcher.IndexIn(line, len) < 0) return null;
            var text = Decode(line, len);
            int idx = text.IndexOf(o.Text, o.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (idx < 0) idx = 0;
            int occ = CountLiteral(text, o.Text, o.IgnoreCase);
            return MakeMatch(file, lineNumber, text, idx, o.Text.Length, Math.Max(1, occ));
        }

        var s = Decode(line, len);
        if (o.InvertMatch)
            return regex!.IsMatch(s) ? null : MakeMatch(file, lineNumber, s, 0, 0, 0);

        var m = regex!.Match(s);
        if (!m.Success) return null;
        int count = regex.Matches(s).Count;
        return MakeMatch(file, lineNumber, s, m.Index, m.Length, count);
    }

    static int CountLiteral(string haystack, string needle, bool ignoreCase)
    {
        if (needle.Length == 0) return 0;
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, cmp)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    static string Decode(byte[] bytes, int len)
    {
        // UTF-8 with the default replacement fallback: never throws, and invalid bytes from
        // binary noise become U+FFFD rather than derailing the search.
        return Encoding.UTF8.GetString(bytes, 0, len);
    }

    /// <summary>
    /// Build a match-centered snippet so the grid shows "…context <b>match</b> context…"
    /// instead of a giant line. Returns the snippet plus the match offset within it.
    /// </summary>
    const int MaxStoredLine = 4096;   // cap the full line we keep for "copy line"

    static GrepMatch MakeMatch(string file, long lineNumber, string line, int start, int length, int occurrences)
    {
        int lineLength = line.Length;

        if (start < 0 || start > lineLength) { start = 0; length = 0; }
        else if (start + length > lineLength) length = lineLength - start;

        int winStart = Math.Max(0, start - PreviewContextBefore);
        int winEnd = Math.Min(lineLength, start + length + PreviewContextAfter);
        var sb = new StringBuilder();
        if (winStart > 0) sb.Append('…');
        int prefixLen = sb.Length;
        sb.Append(line, winStart, winEnd - winStart);
        if (winEnd < lineLength) sb.Append('…');
        var preview = sb.ToString();
        int previewStart = prefixLen + (start - winStart);
        if (previewStart > preview.Length) { previewStart = 0; length = 0; }
        else if (previewStart + length > preview.Length) length = preview.Length - previewStart;

        var stored = line.Length > MaxStoredLine ? line[..MaxStoredLine] : line;

        return new GrepMatch(
            file, System.IO.Path.GetFileName(file), lineNumber,
            stored, preview, previewStart, length, lineLength, occurrences);
    }

    static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".csv", ".tsv", ".json", ".xml", ".html", ".htm", ".md",
        ".ini", ".conf", ".cfg", ".yaml", ".yml", ".sql", ".list", ".text", ".dat",
    };

    static bool LooksTextual(string file) => TextExtensions.Contains(System.IO.Path.GetExtension(file));

    static bool IsBinary(Stream stream)
    {
        int cap = (int)Math.Min(BinarySniffBytes, stream.Length);
        if (cap == 0) return false;
        var buffer = new byte[cap];
        int read = stream.Read(buffer, 0, cap);
        stream.Seek(0, SeekOrigin.Begin);
        if (read == 0) return false;

        // Ratio-based: only a genuinely binary file (image, executable, archive) has a high
        // share of NUL bytes. Combo dumps with a little NUL noise stay searchable.
        int nul = 0;
        for (int i = 0; i < read; i++) if (buffer[i] == 0) nul++;
        return nul * 100L / read > 30;
    }

    /// <summary>Literal substring search over raw bytes with ASCII case folding.</summary>
    sealed class ByteMatcher
    {
        readonly byte[] _needle;
        readonly bool _ignoreCase;

        public ByteMatcher(string text, bool ignoreCase)
        {
            _needle = Encoding.UTF8.GetBytes(text);
            _ignoreCase = ignoreCase;
            if (_ignoreCase)
                for (int i = 0; i < _needle.Length; i++) _needle[i] = Lower(_needle[i]);
        }

        static byte Lower(byte b) => (byte)(b >= (byte)'A' && b <= (byte)'Z' ? b + 32 : b);

        public int IndexIn(byte[] hay, int hayLen)
        {
            int n = _needle.Length;
            if (n == 0) return 0;
            if (n > hayLen) return -1;

            byte first = _needle[0];
            int last = hayLen - n;
            for (int i = 0; i <= last; i++)
            {
                if ((_ignoreCase ? Lower(hay[i]) : hay[i]) != first) continue;
                int j = 1;
                for (; j < n; j++)
                {
                    byte h = _ignoreCase ? Lower(hay[i + j]) : hay[i + j];
                    if (h != _needle[j]) break;
                }
                if (j == n) return i;
            }
            return -1;
        }
    }
}
