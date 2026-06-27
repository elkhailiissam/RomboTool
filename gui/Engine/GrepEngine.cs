using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RomboTool.Engine;

/// <summary>Search parameters for a grep run (mirrors the BareGrep fields).</summary>
public sealed class GrepOptions
{
    public string Folder = "";
    public string FilePatterns = "*";   // space-separated DOS-style globs, e.g. "*.txt *.log"
    public string Text = "";
    public bool Regex;
    public bool IgnoreCase = true;
    public bool Recurse = true;
    public bool InvertMatch;
}

/// <summary>A single matching line.</summary>
public readonly record struct GrepMatch(
    string FullPath,
    string FileName,
    int LineNumber,
    string Line,
    int MatchStart,
    int MatchLength);

/// <summary>
/// Cross-platform regex file searcher. C# port of GrepRipper.Engine's FileSearcher,
/// minus the Windows-only libmagic dependency (binary files are detected with a
/// null-byte heuristic instead).
/// </summary>
public static class GrepEngine
{
    public const long MaxFileSize = 256L * 1024 * 1024;
    const int MaxLineDisplayLength = 2000;
    const int BinarySniffBytes = 8000;

    public static Regex BuildRegex(GrepOptions o)
    {
        var opts = RegexOptions.None;
        if (o.IgnoreCase) opts |= RegexOptions.IgnoreCase;
        var pattern = o.Regex ? o.Text : Regex.Escape(o.Text);
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

    public static List<string> EnumerateFiles(GrepOptions o)
    {
        var glob = BuildGlob(o.FilePatterns);
        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = o.Recurse,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        };

        var result = new List<string>();
        foreach (var path in Directory.EnumerateFiles(o.Folder, "*", enumOpts))
            if (glob.IsMatch(Path.GetFileName(path)))
                result.Add(path);
        return result;
    }

    /// <summary>
    /// Search every matching file in parallel. <paramref name="onFileMatches"/> is invoked
    /// once per file that has matches; <paramref name="onProgress"/> reports (done, total,
    /// filesWithMatches). Both callbacks may run on worker threads.
    /// </summary>
    public static async Task SearchAsync(
        GrepOptions o,
        Action<List<GrepMatch>> onFileMatches,
        Action<int, int, int> onProgress,
        CancellationToken ct)
    {
        var regex = BuildRegex(o);
        var files = EnumerateFiles(o);
        int total = files.Count, done = 0, filesWithMatches = 0;

        onProgress(0, total, 0);

        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
        };

        try
        {
            await Parallel.ForEachAsync(files, options, async (file, token) =>
            {
                List<GrepMatch>? matches = null;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length is > MaxFileSize or 0) return;

                    await using (var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (await IsBinaryAsync(stream, token)) return;
                        stream.Seek(0, SeekOrigin.Begin);

                        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                        int lineNumber = 0;
                        string? line;
                        while ((line = await reader.ReadLineAsync(token)) != null)
                        {
                            lineNumber++;
                            if (o.InvertMatch)
                            {
                                if (!regex.IsMatch(line))
                                    (matches ??= new()).Add(MakeMatch(file, lineNumber, line, 0, 0));
                                continue;
                            }

                            foreach (Match m in regex.Matches(line))
                            {
                                if (!m.Success) continue;
                                (matches ??= new()).Add(MakeMatch(file, lineNumber, line, m.Index, m.Length));
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch { /* unreadable file: skip, keep searching */ }
                finally
                {
                    if (matches is { Count: > 0 })
                    {
                        Interlocked.Increment(ref filesWithMatches);
                        onFileMatches(matches);
                    }
                    onProgress(Interlocked.Increment(ref done), total, Volatile.Read(ref filesWithMatches));
                }
            });
        }
        catch (OperationCanceledException) { }
    }

    static GrepMatch MakeMatch(string file, int lineNumber, string line, int start, int length)
    {
        var display = line.Length > MaxLineDisplayLength ? line[..MaxLineDisplayLength] : line;
        return new GrepMatch(file, Path.GetFileName(file), lineNumber, display, start, length);
    }

    static async Task<bool> IsBinaryAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(BinarySniffBytes, (int)Math.Min(stream.Length, int.MaxValue))];
        int read = await stream.ReadAsync(buffer, ct);
        for (int i = 0; i < read; i++)
            if (buffer[i] == 0) return true;   // a NUL byte ⇒ treat as binary
        return false;
    }
}
