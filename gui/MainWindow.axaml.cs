using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RomboTool.Engine;

namespace RomboTool;

public partial class MainWindow : Window
{
    const int MaxDisplayedMatches = 50_000;

    readonly ObservableCollection<GrepMatch> _grepRows = new();
    readonly ObservableCollection<string> _comboFiles = new();
    CancellationTokenSource? _grepCancel;
    bool _grepBusy, _comboBusy;

    public MainWindow()
    {
        InitializeComponent();

        ResultsGrid.ItemsSource = _grepRows;

        // Drag & drop combo input files onto the window.
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DragDrop.SetAllowDrop(this, true);
    }

    // ───────────────────────────────── Grep ─────────────────────────────────

    async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders.Count > 0)
            FolderBox.Text = folders[0].Path.LocalPath;
    }

    void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnRunGrep(sender, e);
    }

    async void OnRunGrep(object? sender, RoutedEventArgs e)
    {
        if (_grepBusy) return;

        var folder = (FolderBox.Text ?? "").Trim();
        var text = SearchTextBox.Text ?? "";

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            GrepStatus.Text = "⚠ Pick an existing folder first.";
            return;
        }
        if (text.Length == 0)
        {
            GrepStatus.Text = "⚠ Enter some text to search for.";
            return;
        }

        var options = new GrepOptions
        {
            Folder = folder,
            FilePatterns = string.IsNullOrWhiteSpace(FilesBox.Text) ? "*" : FilesBox.Text!.Trim(),
            Text = text,
            Regex = RegexChk.IsChecked == true,
            IgnoreCase = IgnoreCaseChk.IsChecked == true,
            Recurse = RecurseChk.IsChecked == true,
            InvertMatch = InvertChk.IsChecked == true,
        };

        // Validate regex up-front so we fail with a clear message.
        if (options.Regex)
        {
            try { GrepEngine.BuildRegex(options); }
            catch (Exception ex) { GrepStatus.Text = $"⚠ Invalid regex: {ex.Message}"; return; }
        }

        _grepBusy = true;
        _grepCancel = new CancellationTokenSource();
        _grepRows.Clear();
        SearchBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        GrepProgress.Value = 0;
        GrepStatus.Text = "Searching…";

        int matchCount = 0;
        bool capped = false;
        var sw = Stopwatch.StartNew();

        void OnFileMatches(List<GrepMatch> matches) => Dispatcher.UIThread.Post(() =>
        {
            foreach (var m in matches)
            {
                if (_grepRows.Count >= MaxDisplayedMatches) { capped = true; break; }
                _grepRows.Add(m);
            }
            matchCount += matches.Count;
        });

        void OnProgress(int done, int total, int filesWithMatches) => Dispatcher.UIThread.Post(() =>
        {
            GrepProgress.Value = total > 0 ? (double)done / total : 0;
            GrepStatus.Text = $"Searching… {done:N0}/{total:N0} files, {filesWithMatches:N0} with matches";
        });

        try
        {
            await Task.Run(() => GrepEngine.SearchAsync(options, OnFileMatches, OnProgress, _grepCancel.Token));
            sw.Stop();
            var capNote = capped ? $" (showing first {MaxDisplayedMatches:N0})" : "";
            var fileCount = _grepRows.Select(r => r.FullPath).Distinct().Count();
            GrepStatus.Text = _grepCancel.IsCancellationRequested
                ? $"Stopped. {_grepRows.Count:N0} matches so far{capNote}."
                : $"Found {matchCount:N0} matches in {fileCount:N0} files — {sw.Elapsed.TotalSeconds:F2}s{capNote}.";
            GrepProgress.Value = 1;
        }
        catch (Exception ex)
        {
            GrepStatus.Text = $"⚠ {ex.Message}";
        }
        finally
        {
            _grepBusy = false;
            SearchBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            _grepCancel?.Dispose();
            _grepCancel = null;
        }
    }

    void OnStopGrep(object? sender, RoutedEventArgs e) => _grepCancel?.Cancel();

    void OnOpenResult(object? sender, TappedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is GrepMatch m)
            OpenInDefaultApp(m.FullPath);
    }

    // ─────────────────────────────── Combo filter ───────────────────────────

    async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Text") { Patterns = new[] { "*.txt" } },
                FilePickerFileTypes.All,
            },
        });
        AddComboFiles(files.Select(f => f.Path.LocalPath));
    }

    void OnClearFiles(object? sender, RoutedEventArgs e)
    {
        _comboFiles.Clear();
        InputBox.Text = "No files…";
        Preview.Text = "Preview…";
        Output.Text = "Output preview…";
        ValidStat.Text = EmailStat.Text = UserStat.Text = PhoneStat.Text = "0";
        FileCount.Text = LineCount.Text = "0";
        ComboStatus.Text = "Ready";
    }

    async void OnChooseOutput(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "filtered.txt",
            DefaultExtension = "txt",
        });
        if (file != null)
            OutBox.Text = file.Path.LocalPath;
    }

    void AddComboFiles(IEnumerable<string> files)
    {
        foreach (var f in files.Where(File.Exists).Where(f => !_comboFiles.Contains(f)))
            _comboFiles.Add(f);

        InputBox.Text = _comboFiles.Count switch
        {
            0 => "No files…",
            1 => Path.GetFileName(_comboFiles[0]),
            _ => $"{_comboFiles.Count} files",
        };

        FileCount.Text = _comboFiles.Count.ToString("N0");
        long lines = 0;
        foreach (var f in _comboFiles)
            try { lines += File.ReadLines(f).Count(); } catch { /* ignore */ }
        LineCount.Text = lines.ToString("N0");

        var sb = new StringBuilder();
        foreach (var f in _comboFiles.Take(2))
        {
            sb.AppendLine($"=== {Path.GetFileName(f)} ===");
            try { foreach (var l in File.ReadLines(f).Take(15)) sb.AppendLine(l); } catch { }
            sb.AppendLine();
        }
        Preview.Text = sb.Length > 0 ? sb.ToString() : "Preview…";
    }

    async void OnRunCombo(object? sender, RoutedEventArgs e)
    {
        if (_comboBusy) return;
        if (_comboFiles.Count == 0) { ComboStatus.Text = "⚠ Add files first."; return; }

        var outPath = string.IsNullOrWhiteSpace(OutBox.Text) ? "filtered.txt" : OutBox.Text!.Trim();
        bool dedup = DedupChk.IsChecked == true;
        bool emailOnly = EmailOnlyChk.IsChecked == true;
        bool userOnly = UserOnlyChk.IsChecked == true;
        var files = _comboFiles.ToList();

        _comboBusy = true;
        ComboGoBtn.IsEnabled = false;
        ComboGoBtn.Content = "Processing…";
        ComboStatus.Text = "Working…";

        try
        {
            var results = new List<string>();
            var stats = await Task.Run(() =>
                ComboFilterEngine.Process(files, outPath, dedup, emailOnly, userOnly, results));

            ValidStat.Text = stats.Valid.ToString("N0");
            EmailStat.Text = stats.Emails.ToString("N0");
            UserStat.Text = stats.Users.ToString("N0");
            PhoneStat.Text = stats.Phones.ToString("N0");

            Output.Text = string.Join("\n", results.Take(50)) +
                          (results.Count > 50 ? $"\n\n… +{results.Count - 50:N0} more" : "");
            ComboStatus.Text = $"Done — {stats.Valid:N0} combos ({stats.Rate:F1}%), " +
                               $"{stats.Duplicates:N0} dupes removed → {outPath}";
        }
        catch (Exception ex)
        {
            ComboStatus.Text = $"⚠ {ex.Message}";
        }
        finally
        {
            _comboBusy = false;
            ComboGoBtn.IsEnabled = true;
            ComboGoBtn.Content = "🚀 Start";
        }
    }

    // ─────────────────────────────── Drag & drop ────────────────────────────

    void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.Data.GetFiles();
        if (items == null) return;
        AddComboFiles(items.Select(i => i.Path.LocalPath).Where(p => File.Exists(p)));
    }

    // ──────────────────────────────── Helpers ───────────────────────────────

    static void OpenInDefaultApp(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start("open", $"\"{path}\"");
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
                Process.Start("xdg-open", $"\"{path}\"");
        }
        catch { /* best effort */ }
    }
}
