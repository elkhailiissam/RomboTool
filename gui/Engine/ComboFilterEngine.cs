using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RomboTool.Engine;

/// <summary>Result counters for a combo-filter run.</summary>
public sealed class ComboStats
{
    public long Total, Valid, Emails, Users, Phones, Duplicates, Invalid;
    public double Rate => Total > 0 ? 100.0 * Valid / Total : 0;
}

/// <summary>
/// Pure C# port of the rombofilter.c parsing logic. Extracts clean user:pass
/// pairs from messy combo lists (URLs, garbage, duplicates stripped).
/// </summary>
public static class ComboFilterEngine
{
    public const int TypeUser = 0, TypeEmail = 1, TypePhone = 2;

    /// <summary>
    /// Stream every line of <paramref name="files"/> through the parser, writing accepted
    /// combos straight to <paramref name="outPath"/> so memory stays flat even on multi-GB
    /// inputs. Only the first <paramref name="previewMax"/> combos are kept in
    /// <paramref name="preview"/>. <paramref name="onProgress"/> reports (bytesDone, bytesTotal);
    /// pass a cancellation token to allow a Stop button.
    /// </summary>
    public static ComboStats Process(
        IEnumerable<string> files,
        string? outPath,
        bool dedup,
        bool emailOnly,
        bool userOnly,
        List<string> preview,
        int previewMax,
        Action<long, long>? onProgress,
        CancellationToken ct)
    {
        var st = new ComboStats();
        var seen = dedup ? new HashSet<string>() : null;
        var list = files.ToList();

        long bytesTotal = 0;
        foreach (var f in list)
            try { bytesTotal += new FileInfo(f).Length; } catch { }

        StreamWriter? writer = null;
        if (!string.IsNullOrWhiteSpace(outPath))
            writer = new StreamWriter(outPath!, false) { AutoFlush = false };

        long completedBytes = 0;
        try
        {
            foreach (var file in list)
            {
                long fileLen = 0;
                try { fileLen = new FileInfo(file).Length; } catch { }

                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan))
                using (var reader = new StreamReader(fs, Encoding.UTF8, true, 1 << 20))
                {
                    string? line;
                    int sinceReport = 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        st.Total++;
                        if (Parse(line, out var u, out var p, out var t))
                        {
                            bool skip = (emailOnly && t != TypeEmail) || (userOnly && t != TypeUser);
                            if (!skip)
                            {
                                var combo = u + ":" + p;
                                bool dup = seen != null && !seen.Add(combo.ToLowerInvariant());
                                if (dup) st.Duplicates++;
                                else
                                {
                                    writer?.WriteLine(combo);
                                    if (preview.Count < previewMax) preview.Add(combo);
                                    st.Valid++;
                                    if (t == TypeEmail) st.Emails++;
                                    else if (t == TypePhone) st.Phones++;
                                    else st.Users++;
                                }
                            }
                        }
                        else st.Invalid++;

                        if (++sinceReport >= 50_000)
                        {
                            sinceReport = 0;
                            onProgress?.Invoke(completedBytes + fs.Position, bytesTotal);
                            ct.ThrowIfCancellationRequested();
                        }
                    }
                }
                completedBytes += fileLen;
                onProgress?.Invoke(completedBytes, bytesTotal);
            }
        }
        finally
        {
            writer?.Dispose();
        }

        onProgress?.Invoke(bytesTotal, bytesTotal);
        return st;
    }

    public static bool Parse(string line, out string user, out string pass, out int type)
    {
        user = pass = ""; type = TypeUser;
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) return false;
        if (line.Contains("@kingulp") || line.Contains("t.me/+") || line.Contains("MonkeyBase") || line.Contains("You can buy")) return false;
        if (line.StartsWith("//") || line.Contains("Browser/") || line.Contains("Chrome_") || line.Contains(".txt:") || line.Contains(';')) return false;

        line = line.Replace('|', ':');
        var parts = line.Split(':').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p) && !IsGarbage(p)).ToArray();
        if (parts.Length < 2) return false;

        for (int i = 0; i < parts.Length - 1; i++)
            if (IsEmail(parts[i]) && ValidPass(parts[i + 1])) { user = parts[i]; pass = parts[i + 1]; type = TypeEmail; return true; }

        for (int i = 0; i < parts.Length - 1; i++)
            if (IsPhone(parts[i]) && ValidPass(parts[i + 1])) { user = parts[i]; pass = parts[i + 1]; type = TypePhone; return true; }

        for (int i = parts.Length - 1; i >= 1; i--)
            if (ValidPass(parts[i]) && ValidUser(parts[i - 1]))
            {
                user = parts[i - 1]; pass = parts[i];
                type = IsEmail(parts[i - 1]) ? TypeEmail : IsPhone(parts[i - 1]) ? TypePhone : TypeUser;
                return true;
            }

        return false;
    }

    static bool IsEmail(string s) =>
        s.Length >= 5 && s.Contains('@') && s.LastIndexOf('.') > s.IndexOf('@') + 1 && !s.Contains(' ');

    static bool IsPhone(string s)
    {
        int d = s.Count(char.IsDigit);
        return s.Length >= 8 && s.Length <= 20 && d >= 8 && d <= 15;
    }

    static bool IsGarbage(string s) =>
        s.Contains("http://") || s.Contains("https://") || s.Contains("www.") ||
        s.Contains(".com/") || s.Contains("/auth") || s.Contains("/login") || s.Contains("/signup");

    static bool ValidPass(string s) =>
        s.Length >= 4 && s.Length <= 128 && !IsGarbage(s) && s != "https" && s != "http" &&
        !s.EndsWith("https") && !s.EndsWith(".com") && !s.EndsWith(".net") && s.Any(char.IsLetterOrDigit);

    static bool ValidUser(string s) =>
        s.Length >= 2 && s.Length <= 100 && !IsGarbage(s) && s != "https" && s != "http" && !s.Contains("//");
}
