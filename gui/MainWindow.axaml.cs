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
    const int ComboPreviewMax = 200;

    readonly ObservableCollection<GrepMatch> _grepRows = new();
    readonly ObservableCollection<string> _comboFiles = new();

    CancellationTokenSource? _grepCancel, _comboCancel;
    bool _grepBusy, _comboBusy, _grepCapped;

    // Live grep progress state. Written from worker threads, read by the UI render timer,
    // so guard the struct copy with a lock to avoid tearing.
    readonly object _grepProgLock = new();
    GrepProgress _grepProg;
    readonly Stopwatch _grepSw = new();
    readonly DispatcherTimer _grepTimer;

    GrepProgress GrepProg { get { lock (_grepProgLock) return _grepProg; } }
    void SetGrepProg(GrepProgress p) { lock (_grepProgLock) _grepProg = p; }

    public MainWindow()
    {
        InitializeComponent();

        ResultsGrid.ItemsSource = _grepRows;

        _grepTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _grepTimer.Tick += (_, _) => RenderGrepStats();

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DragDrop.SetAllowDrop(this, true);
    }

    // ───────────────────────────────── Grep ─────────────────────────────────

    async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders.Count > 0)
            PathBox.Text = folders[0].Path.LocalPath;
    }

    async void OnBrowseFile(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Text & logs") { Patterns = new[] { "*.txt", "*.log", "*.csv" } },
                FilePickerFileTypes.All,
            },
        });
        if (files.Count > 0)
            PathBox.Text = files[0].Path.LocalPath;
    }

    void OnPathChanged(object? sender, TextChangedEventArgs e) => UpdatePathChip();

    void UpdatePathChip()
    {
        var path = (PathBox.Text ?? "").Trim();
        if (path.Length == 0)
        {
            TypeChip.IsVisible = false;
            FolderFilters.IsVisible = true;
            return;
        }

        if (File.Exists(path) && !GrepEngine.IsDirectory(path))
        {
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            TypeChipText.Text = $"📄 {Path.GetFileName(path)} · {HumanBytes(size)}";
            TypeChip.IsVisible = true;
            FolderFilters.IsVisible = false;   // globs/recurse don't apply to a single file
        }
        else if (Directory.Exists(path))
        {
            TypeChipText.Text = "📁 Folder";
            TypeChip.IsVisible = true;
            FolderFilters.IsVisible = true;
        }
        else
        {
            TypeChipText.Text = "⚠ not found";
            TypeChip.IsVisible = true;
            FolderFilters.IsVisible = true;
        }
    }

    void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnRunGrep(sender, e);
    }

    async void OnRunGrep(object? sender, RoutedEventArgs e)
    {
        if (_grepBusy) return;

        var path = (PathBox.Text ?? "").Trim();
        var text = SearchTextBox.Text ?? "";

        bool isFile = File.Exists(path) && !GrepEngine.IsDirectory(path);
        bool isDir = Directory.Exists(path);
        if (!isFile && !isDir) { GrepStatus.Text = "⚠ Pick an existing file or folder first."; return; }
        if (text.Length == 0) { GrepStatus.Text = "⚠ Enter some text to search for."; return; }

        long maxMatches = LimitChk.IsChecked == true ? (long)(LimitBox.Value ?? 0m) : 0;

        var options = new GrepOptions
        {
            Path = path,
            IsSingleFile = isFile,
            FilePatterns = string.IsNullOrWhiteSpace(FilesBox.Text) ? "*" : FilesBox.Text!.Trim(),
            Text = text,
            Regex = RegexChk.IsChecked == true,
            IgnoreCase = IgnoreCaseChk.IsChecked == true,
            Recurse = RecurseChk.IsChecked == true,
            InvertMatch = InvertChk.IsChecked == true,
            MaxMatches = maxMatches,
        };

        if (options.Regex)
        {
            try { GrepEngine.BuildRegex(options); }
            catch (Exception ex) { GrepStatus.Text = $"⚠ Invalid regex: {ex.Message}"; return; }
        }

        _grepBusy = true;
        _grepCapped = false;
        _grepCancel = new CancellationTokenSource();
        _grepRows.Clear();
        SearchBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        GrepProgress.Value = 0;
        SetGrepProg(default);
        _grepSw.Restart();
        _grepTimer.Start();
        StatCurrent.Text = "Searching…";

        void OnFileMatches(List<GrepMatch> matches) => Dispatcher.UIThread.Post(() =>
        {
            foreach (var m in matches)
            {
                if (_grepRows.Count >= MaxDisplayedMatches) { _grepCapped = true; break; }
                _grepRows.Add(m);
            }
        });

        void OnProgress(GrepProgress p) => SetGrepProg(p);

        try
        {
            await GrepEngine.SearchAsync(options, OnFileMatches, OnProgress, _grepCancel.Token);
            _grepSw.Stop();
            _grepTimer.Stop();
            RenderGrepStats();
            GrepProgress.Value = 1;
            StatPercent.Text = "100%";

            var fin = GrepProg;
            var capNote = _grepCapped ? $" · showing first {MaxDisplayedMatches:N0}" : "";
            var filesNote = fin.FilesTotal > 1 ? $" across {fin.FilesWithMatches:N0}/{fin.FilesTotal:N0} files" : "";
            StatCurrent.Text = _grepCancel.IsCancellationRequested
                ? $"Stopped — {fin.MatchCount:N0} matches so far{capNote}."
                : $"Done — {fin.MatchCount:N0} matches{filesNote} in {_grepSw.Elapsed.TotalSeconds:F2}s{capNote}.";
        }
        catch (Exception ex)
        {
            _grepSw.Stop();
            _grepTimer.Stop();
            StatCurrent.Text = $"⚠ {ex.Message}";
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

    void RenderGrepStats()
    {
        var p = GrepProg;
        double secs = _grepSw.Elapsed.TotalSeconds;
        double pct = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0;

        GrepProgress.Value = pct;
        StatPercent.Text = $"{pct * 100:F0}%";
        StatData.Text = p.BytesTotal > 0 ? $"{HumanBytes(p.BytesScanned)} / {HumanBytes(p.BytesTotal)}" : HumanBytes(p.BytesScanned);
        StatSpeed.Text = secs > 0.05 ? $"{HumanBytes((long)(p.BytesScanned / secs))}/s" : "—";
        StatMatches.Text = p.MatchCount.ToString("N0");
        StatElapsed.Text = secs < 60 ? $"{secs:F1}s" : $"{(int)(secs / 60)}m {secs % 60:F0}s";

        if (_grepBusy && !string.IsNullOrEmpty(p.CurrentFile))
            StatCurrent.Text = p.FilesTotal > 1
                ? $"Scanning {p.CurrentFile}  ·  file {p.FilesDone:N0}/{p.FilesTotal:N0}"
                : $"Scanning {p.CurrentFile}…";
    }

    void OnStopGrep(object? sender, RoutedEventArgs e)
    {
        _grepCancel?.Cancel();
        StatCurrent.Text = "Stopping…";
    }

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
        FileCount.Text = "0";
        SizeCount.Text = "0 B";
        ComboProgress.Value = 0;
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

        // Size only — never read whole files on the UI thread (a 19 GB line-count would freeze it).
        long size = 0;
        foreach (var f in _comboFiles)
            try { size += new FileInfo(f).Length; } catch { }
        SizeCount.Text = HumanBytes(size);

        // Preview is cheap: ReadLines is lazy, Take(15) stops after 15 lines.
        var sb = new StringBuilder();
        foreach (var f in _comboFiles.Take(2))
        {
            sb.AppendLine($"=== {Path.GetFileName(f)} ===");
            try { foreach (var l in File.ReadLines(f).Take(15)) sb.AppendLine(l.Length > 200 ? l[..200] + "…" : l); }
            catch { }
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
        _comboCancel = new CancellationTokenSource();
        ComboGoBtn.IsEnabled = false;
        ComboStopBtn.IsEnabled = true;
        ComboGoBtn.Content = "Processing…";
        ComboProgress.Value = 0;
        ComboStatus.Text = "Working…";

        var sw = Stopwatch.StartNew();
        var preview = new List<string>();

        void OnProgress(long done, long total) => Dispatcher.UIThread.Post(() =>
        {
            ComboProgress.Value = total > 0 ? (double)done / total : 0;
            ComboStatus.Text = $"Filtering… {HumanBytes(done)} / {HumanBytes(total)}";
        });

        try
        {
            var stats = await Task.Run(() => ComboFilterEngine.Process(
                files, outPath, dedup, emailOnly, userOnly, preview, ComboPreviewMax,
                OnProgress, _comboCancel.Token));
            sw.Stop();

            ValidStat.Text = stats.Valid.ToString("N0");
            EmailStat.Text = stats.Emails.ToString("N0");
            UserStat.Text = stats.Users.ToString("N0");
            PhoneStat.Text = stats.Phones.ToString("N0");

            Output.Text = string.Join("\n", preview) +
                          (stats.Valid > preview.Count ? $"\n\n… +{stats.Valid - preview.Count:N0} more (written to file)" : "");
            ComboProgress.Value = 1;
            ComboStatus.Text = $"Done — {stats.Valid:N0} combos ({stats.Rate:F1}%), " +
                               $"{stats.Duplicates:N0} dupes removed in {sw.Elapsed.TotalSeconds:F1}s → {Path.GetFileName(outPath)}";
        }
        catch (OperationCanceledException)
        {
            ComboStatus.Text = "Stopped (partial output saved).";
        }
        catch (Exception ex)
        {
            ComboStatus.Text = $"⚠ {ex.Message}";
        }
        finally
        {
            _comboBusy = false;
            ComboGoBtn.IsEnabled = true;
            ComboStopBtn.IsEnabled = false;
            ComboGoBtn.Content = "🚀 Start";
            _comboCancel?.Dispose();
            _comboCancel = null;
        }
    }

    void OnStopCombo(object? sender, RoutedEventArgs e)
    {
        _comboCancel?.Cancel();
        ComboStatus.Text = "Stopping…";
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

    static string HumanBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return v >= 100 ? $"{v:F0} {units[i]}" : $"{v:F1} {units[i]}";
    }

    static void OpenInDefaultApp(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { path }, UseShellExecute = false });
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { path }, UseShellExecute = false });
        }
        catch { /* best effort */ }
    }
}
