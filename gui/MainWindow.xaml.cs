using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace RomboTool;

public partial class MainWindow : Window
{
    readonly ObservableCollection<string> _files = new();
    bool _busy;

    public MainWindow() => InitializeComponent();

    void OnDrag(object s, DragEventArgs e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    void OnDrop(object s, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] f) AddFiles(f); }
    void Browse(object s, RoutedEventArgs e) { var d = new OpenFileDialog { Multiselect = true, Filter = "Text|*.txt|All|*.*" }; if (d.ShowDialog() == true) AddFiles(d.FileNames); }
    void Clear(object s, RoutedEventArgs e) { _files.Clear(); Preview.Text = "Preview..."; Output.Text = "Output preview..."; InputBox.Text = "No files..."; UpdateStats(); Reset(); }

    void AddFiles(string[] files)
    {
        foreach (var f in files.Where(File.Exists).Where(f => !_files.Contains(f))) _files.Add(f);
        InputBox.Text = $"{_files.Count} file(s)";
        UpdateStats();
        var sb = new StringBuilder();
        foreach (var f in _files.Take(2)) { sb.AppendLine($"=== {Path.GetFileName(f)} ==="); foreach (var l in File.ReadLines(f).Take(15)) sb.AppendLine(l); sb.AppendLine(); }
        Preview.Text = sb.ToString();
    }

    void UpdateStats()
    {
        FileCount.Text = _files.Count.ToString();
        long lines = 0; foreach (var f in _files) try { lines += File.ReadLines(f).Count(); } catch { }
        LineCount.Text = lines.ToString("N0");
    }

    void Reset() { Valid.Text = Emails.Text = Users.Text = Phones.Text = "0"; }

    async void Start(object s, RoutedEventArgs e)
    {
        if (_busy || _files.Count == 0) { MessageBox.Show("Add files first"); return; }
        _busy = true; GoBtn.IsEnabled = false; GoBtn.Content = "Processing..."; Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = true;
        try { await ProcessAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
        finally { _busy = false; GoBtn.IsEnabled = true; GoBtn.Content = "🚀 Start"; Progress.Visibility = Visibility.Collapsed; }
    }

    async Task ProcessAsync()
    {
        string outPath = OutBox.Text; if (string.IsNullOrWhiteSpace(outPath)) outPath = "filtered.txt";
        bool dedup = Dedup.IsChecked == true, emailOnly = EmailOnly.IsChecked == true, userOnly = UserOnly.IsChecked == true;
        var results = new List<string>(); var seen = new HashSet<string>();
        long total = 0, valid = 0, emails = 0, users = 0, phones = 0, dups = 0;

        await Task.Run(() =>
        {
            foreach (var file in _files)
            {
                foreach (var line in File.ReadLines(file))
                {
                    total++;
                    if (Parse(line, out var u, out var p, out var t))
                    {
                        if (emailOnly && t != 1) continue;
                        if (userOnly && t != 0) continue;
                        var combo = $"{u}:{p}";
                        if (dedup) { var k = combo.ToLowerInvariant(); if (seen.Contains(k)) { dups++; continue; } seen.Add(k); }
                        results.Add(combo); valid++;
                        if (t == 1) emails++; else if (t == 2) phones++; else users++;
                    }
                }
            }
            File.WriteAllLines(outPath, results);
        });

        Valid.Text = valid.ToString("N0"); Emails.Text = emails.ToString("N0"); Users.Text = users.ToString("N0"); Phones.Text = phones.ToString("N0");
        Output.Text = string.Join("\n", results.Take(50)) + (results.Count > 50 ? $"\n\n... +{results.Count - 50:N0} more" : "");
        Status.Text = $"Done! {valid:N0} combos ({(total > 0 ? 100.0 * valid / total : 0):F1}%)";
        MessageBox.Show($"Extracted {valid:N0} combos\nEmails: {emails:N0}\nUsers: {users:N0}\nPhones: {phones:N0}\nDuplicates: {dups:N0}\n\nSaved: {outPath}", "Complete");
    }

    static bool Parse(string line, out string user, out string pass, out int type)
    {
        user = pass = ""; type = 0;
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) return false;
        if (line.Contains("@kingulp") || line.Contains("t.me/+") || line.Contains("MonkeyBase") || line.Contains("You can buy")) return false;
        if (line.StartsWith("//") || line.Contains("Browser/") || line.Contains("Chrome_") || line.Contains(".txt:") || line.Contains(';')) return false;
        line = line.Replace('|', ':');
        var parts = line.Split(':').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p) && !IsGarbage(p)).ToArray();
        if (parts.Length < 2) return false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (IsEmail(parts[i]) && ValidPass(parts[i + 1])) { user = parts[i]; pass = parts[i + 1]; type = 1; return true; }
        }
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (IsPhone(parts[i]) && ValidPass(parts[i + 1])) { user = parts[i]; pass = parts[i + 1]; type = 2; return true; }
        }
        for (int i = parts.Length - 1; i >= 1; i--)
        {
            if (ValidPass(parts[i]) && ValidUser(parts[i - 1])) { user = parts[i - 1]; pass = parts[i]; type = IsEmail(parts[i - 1]) ? 1 : IsPhone(parts[i - 1]) ? 2 : 0; return true; }
        }
        return false;
    }

    static bool IsEmail(string s) => s.Length >= 5 && s.Contains('@') && s.LastIndexOf('.') > s.IndexOf('@') + 1 && !s.Contains(' ');
    static bool IsPhone(string s) { int d = s.Count(char.IsDigit); return s.Length >= 8 && s.Length <= 20 && d >= 8 && d <= 15; }
    static bool IsGarbage(string s) => s.Contains("http://") || s.Contains("https://") || s.Contains("www.") || s.Contains(".com/") || s.Contains("/auth") || s.Contains("/login") || s.Contains("/signup");
    static bool ValidPass(string s) => s.Length >= 4 && s.Length <= 128 && !IsGarbage(s) && s != "https" && s != "http" && !s.EndsWith("https") && !s.EndsWith(".com") && !s.EndsWith(".net") && s.Any(char.IsLetterOrDigit);
    static bool ValidUser(string s) => s.Length >= 2 && s.Length <= 100 && !IsGarbage(s) && s != "https" && s != "http" && !s.Contains("//");
}
