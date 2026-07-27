using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using RomboTool.Engine;

namespace RomboTool;

public partial class MainWindow : Window
{
    const int MaxDisplayedMatches = 100_000;
    const int ComboPreviewMax = 200;

    readonly ObservableCollection<GrepMatch> _grepRows = new();
    readonly ObservableCollection<string> _comboFiles = new();

    CancellationTokenSource? _grepCancel, _comboCancel;
    readonly ManualResetEventSlim _pauseGate = new(true);   // set = running, reset = paused
    bool _grepBusy, _comboBusy, _grepCapped, _paused, _dark = true;

    readonly object _grepProgLock = new();
    GrepProgress _grepProg;
    readonly Stopwatch _grepSw = new();
    readonly DispatcherTimer _grepTimer;
    readonly Process _proc = Process.GetCurrentProcess();

    string _sourcePath = "";
    bool _sourceIsFile;

    readonly AppState _state = AppState.Load();

    GrepProgress GrepProg { get { lock (_grepProgLock) return _grepProg; } }
    void SetGrepProg(GrepProgress p) { lock (_grepProgLock) _grepProg = p; }

    public MainWindow()
    {
        InitializeComponent();

        ResultsGrid.ItemsSource = _grepRows;
        _grepRows.CollectionChanged += (_, _) => { UpdateResultCount(); UpdateEmptyState(); };

        _grepTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _grepTimer.Tick += (_, _) => RenderGrepStats();

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DragDrop.SetAllowDrop(this, true);

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        _dark = !string.Equals(_state.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        ApplyTheme(_dark);

        UpdateResultCount();
        UpdateEmptyState();
    }

    // ─────────────────────────────── Theme / About ──────────────────────────

    void OnToggleTheme(object? sender, RoutedEventArgs e) { _dark = !_dark; ApplyTheme(_dark); _state.Save(); }

    void ApplyTheme(bool dark)
    {
        Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (ThemeBtn != null) ThemeBtn.Content = dark ? "☾" : "☀";
        _state.Theme = dark ? "Dark" : "Light";
    }

    void OnAbout(object? sender, RoutedEventArgs e)
    {
        StatCurrent.Text = "RomboTool 3.1 — fast search & combo filtering for multi-GB files. Built with Avalonia.";
    }

    // ─────────────────────────────── Source ─────────────────────────────────

    async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders.Count > 0) SetSource(folders[0].Path.LocalPath);
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
        if (files.Count > 0) SetSource(files[0].Path.LocalPath);
    }

    void SetSource(string path)
    {
        path = path.Trim();
        bool isFile = File.Exists(path) && !GrepEngine.IsDirectory(path);
        bool isDir = Directory.Exists(path);
        if (!isFile && !isDir) { HintText.Text = "⚠ That path doesn't exist."; return; }

        _sourcePath = path;
        _sourceIsFile = isFile;

        SourceHint.IsVisible = false;
        SourceChip.IsVisible = true;
        SourceName.Text = isFile ? Path.GetFileName(path) : path;
        SourceIcon.Text = isFile ? "📄" : "📁";
        if (isFile)
        {
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            SourceSize.Text = HumanBytes(size);
        }
        else SourceSize.Text = "folder";
        FolderFilters.IsEnabled = !isFile;
        FolderFilters.Opacity = isFile ? 0.4 : 1.0;

        _state.PushRecentFile(path);
        _state.Save();
        HintText.Text = "Ready — type a query and hit Search.";
    }

    void OnClearSource(object? sender, RoutedEventArgs e)
    {
        _sourcePath = "";
        SourceChip.IsVisible = false;
        SourceHint.IsVisible = true;
        FolderFilters.IsEnabled = true;
        FolderFilters.Opacity = 1.0;
    }

    void OnShowRecentFiles(object? sender, RoutedEventArgs e)
    {
        var items = _state.RecentFiles
            .Select(p => ((string label, Action act))(ShortPath(p), () => SetSource(p)))
            .ToList();
        ShowMenu(RecentBtn, items, "No recent files");
    }

    // ─────────────────────────────── Search box ─────────────────────────────

    void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
        => ClearSearchBtn.IsVisible = !string.IsNullOrEmpty(SearchBox.Text);

    void OnClearSearch(object? sender, RoutedEventArgs e) { SearchBox.Text = ""; SearchBox.Focus(); }

    void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnRunGrep(sender, e); e.Handled = true; }
    }

    void OnShowSearchHistory(object? sender, RoutedEventArgs e)
    {
        var items = _state.RecentSearches
            .Select(q => ((string label, Action act))(q, () => { SearchBox.Text = q; SearchBox.Focus(); }))
            .ToList();
        ShowMenu(HistoryBtn, items, "No recent searches");
    }

    // ─────────────────────────────── Run search ─────────────────────────────

    async void OnRunGrep(object? sender, RoutedEventArgs e)
    {
        if (_grepBusy) { OnStopGrep(); return; }   // the button doubles as Stop while running

        var path = _sourcePath;
        var text = SearchBox.Text ?? "";
        bool isFile = File.Exists(path) && !GrepEngine.IsDirectory(path);
        bool isDir = Directory.Exists(path);
        if (!isFile && !isDir) { HintText.Text = "⚠ Pick a file or folder first (Open File / Open Folder)."; return; }
        if (text.Length == 0) { HintText.Text = "⚠ Type something to search for."; SearchBox.Focus(); return; }

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
            WholeWord = WholeWordChk.IsChecked == true,
            Multiline = MultilineChk.IsChecked == true,
            MaxMatches = maxMatches,
        };

        if (options.Regex || options.WholeWord)
        {
            try { GrepEngine.BuildRegex(options); }
            catch (Exception ex) { HintText.Text = $"⚠ Invalid regex: {ex.Message}"; return; }
        }

        _state.PushRecentSearch(text);
        _state.Save();

        _grepBusy = true;
        _grepCapped = false;
        _paused = false;
        _pauseGate.Set();
        _grepCancel = new CancellationTokenSource();
        _grepRows.Clear();
        SearchBtn.Content = "Stop";
        PauseBtn.IsVisible = true;
        PauseBtn.Content = "Pause";
        GrepProgress.Value = 0;
        SetGrepProg(default);
        _grepSw.Restart();
        _grepTimer.Start();
        HintText.Text = "";
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
            await GrepEngine.SearchAsync(options, OnFileMatches, OnProgress, _grepCancel.Token, _pauseGate);
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

            if (fin.MatchCount == 0 && !_grepCancel.IsCancellationRequested)
            {
                EmptyTitle.Text = "No matches found";
                EmptyHint.Text = $"“{text}” wasn't found. Try turning off Whole word / case sensitivity, or check the source.";
            }
        }
        catch (Exception ex)
        {
            _grepSw.Stop(); _grepTimer.Stop();
            StatCurrent.Text = $"⚠ {ex.Message}";
        }
        finally
        {
            _grepBusy = false;
            SearchBtn.Content = "Search";
            PauseBtn.IsVisible = false;
            _grepCancel?.Dispose();
            _grepCancel = null;
        }
    }

    void OnStopGrep()
    {
        _pauseGate.Set();          // release a paused worker so it can observe cancellation
        _grepCancel?.Cancel();
        StatCurrent.Text = "Stopping…";
    }

    void OnPauseResume(object? sender, RoutedEventArgs e)
    {
        if (!_grepBusy) return;
        _paused = !_paused;
        if (_paused) { _pauseGate.Reset(); _grepSw.Stop(); PauseBtn.Content = "Resume"; StatCurrent.Text = "Paused."; }
        else { _pauseGate.Set(); _grepSw.Start(); PauseBtn.Content = "Pause"; }
    }

    void RenderGrepStats()
    {
        var p = GrepProg;
        double secs = _grepSw.Elapsed.TotalSeconds;
        double pct = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0;

        GrepProgress.Value = pct;
        StatPercent.Text = $"{pct * 100:F0}%";
        StatScanned.Text = p.BytesTotal > 0 ? $"{HumanBytes(p.BytesScanned)} / {HumanBytes(p.BytesTotal)}" : HumanBytes(p.BytesScanned);
        StatMatches.Text = p.MatchCount.ToString("N0");
        StatElapsed.Text = secs < 60 ? $"{secs:F1}s" : $"{(int)(secs / 60)}m {secs % 60:F0}s";

        if (secs > 0.05)
        {
            double bps = p.BytesScanned / secs;
            StatSpeed.Text = $"{HumanBytes((long)bps)}/s";
            StatLines.Text = $"{p.LinesScanned / secs / 1000:F0}k";
            long remaining = Math.Max(0, p.BytesTotal - p.BytesDone);
            StatEta.Text = _grepBusy && !_paused && bps > 1 && remaining > 0 ? FormatSeconds(remaining / bps) : (_grepBusy ? "—" : "0s");
        }

        try
        {
            _proc.Refresh();
            StatMem.Text = HumanBytes(_proc.WorkingSet64);
            StatThreads.Text = _proc.Threads.Count.ToString();
        }
        catch { }

        if (_grepBusy && !_paused && !string.IsNullOrEmpty(p.CurrentFile))
            StatCurrent.Text = p.FilesTotal > 1
                ? $"Scanning {p.CurrentFile} · file {p.FilesDone:N0}/{p.FilesTotal:N0} · {p.LinesScanned:N0} lines"
                : $"Scanning {p.CurrentFile} · {p.LinesScanned:N0} lines · last match line {p.CurrentLine:N0}";
    }

    // ─────────────────────────── Results: selection / toolbar ────────────────

    List<GrepMatch> Selected() => ResultsGrid.SelectedItems.OfType<GrepMatch>().ToList();

    void OnSelectAll(object? sender, RoutedEventArgs e) => ResultsGrid.SelectAll();

    void OnInvertSelection(object? sender, RoutedEventArgs e)
    {
        var selected = new HashSet<GrepMatch>(Selected());
        ResultsGrid.SelectedItems.Clear();
        foreach (var row in _grepRows)
            if (!selected.Contains(row)) ResultsGrid.SelectedItems.Add(row);
    }

    void OnClearResults(object? sender, RoutedEventArgs e)
    {
        _grepRows.Clear();
        EmptyTitle.Text = "Nothing to show yet";
        EmptyHint.Text = "Open a file or folder, type what to find, and press Search. Matches appear here with the line and a highlighted preview.";
        StatCurrent.Text = "Results cleared.";
    }

    void OnRemoveSelected(object? sender, RoutedEventArgs e)
    {
        foreach (var m in Selected()) _grepRows.Remove(m);
    }

    async void OnCopySelected(object? sender, RoutedEventArgs e)
    {
        var rows = Selected();
        if (rows.Count == 0) { StatCurrent.Text = "Nothing selected to copy."; return; }
        await CopyText(string.Join("\n", rows.Select(r => r.Line)));
        StatCurrent.Text = $"Copied {rows.Count:N0} line(s).";
    }

    // ─────────────────────────── Results: context menu ───────────────────────

    void OnOpenResult(object? sender, TappedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is GrepMatch m) OpenAtLine(m.FullPath, m.LineNumber);
    }

    void OnCtxOpen(object? sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is GrepMatch m) OpenAtLine(m.FullPath, m.LineNumber);
    }

    void OnCtxReveal(object? sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is GrepMatch m) RevealInFinder(m.FullPath);
    }

    async void OnCtxCopyLine(object? sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is GrepMatch m) { await CopyText(m.Line); StatCurrent.Text = "Copied line."; }
    }

    async void OnCtxCopyMatch(object? sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is GrepMatch m) { await CopyText(m.MatchText); StatCurrent.Text = "Copied match."; }
    }

    async void OnCtxCopyPath(object? sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is GrepMatch m) { await CopyText(m.FullPath); StatCurrent.Text = "Copied path."; }
    }

    void OnCtxSendToSearch(object? sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not GrepMatch m) return;
        var q = string.IsNullOrEmpty(m.MatchText) ? m.Line : m.MatchText;
        SearchBox.Text = q;
        OnRunGrep(sender, e);
    }

    void OnCtxExportSelected(object? sender, RoutedEventArgs e) => _ = ExportAsync(Selected());

    // ─────────────────────────────── Export ─────────────────────────────────

    void OnShowExportMenu(object? sender, RoutedEventArgs e)
    {
        var items = new List<(string, Action)>
        {
            ("All results…", () => _ = ExportAsync(_grepRows.ToList())),
            ("Selected only…", () => _ = ExportAsync(Selected())),
        };
        ShowMenu(ExportBtn, items, "");
    }

    async Task ExportAsync(List<GrepMatch> rows)
    {
        if (rows.Count == 0) { StatCurrent.Text = "Nothing to export."; return; }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "results.txt",
            DefaultExtension = "txt",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Text") { Patterns = new[] { "*.txt" } },
                new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } },
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
            },
        });
        if (file == null) return;

        var path = file.Path.LocalPath;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            await Task.Run(() => WriteRows(path, rows, ext));
            StatCurrent.Text = $"Exported {rows.Count:N0} row(s) → {Path.GetFileName(path)}";
        }
        catch (Exception ex) { StatCurrent.Text = $"⚠ Export failed: {ex.Message}"; }
    }

    static void WriteRows(string path, List<GrepMatch> rows, string ext)
    {
        using var w = new StreamWriter(path, false);
        switch (ext)
        {
            case ".json":
                var arr = rows.Select(r => new
                {
                    file = r.FullPath, line = r.LineNumber, length = r.LineLength,
                    occurrences = r.Occurrences, match = r.MatchText, text = r.Line,
                });
                w.Write(JsonSerializer.Serialize(arr, new JsonSerializerOptions { WriteIndented = true }));
                break;
            case ".csv":
                w.WriteLine("File,Line,Length,Occurrences,Match,Text");
                foreach (var r in rows)
                    w.WriteLine($"{Csv(r.FullPath)},{r.LineNumber},{r.LineLength},{r.Occurrences},{Csv(r.MatchText)},{Csv(r.Line)}");
                break;
            default: // txt
                foreach (var r in rows) w.WriteLine($"{r.FileName}:{r.LineNumber}: {r.Line}");
                break;
        }
    }

    static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    // ─────────────────────────── Move to Combo Filter ────────────────────────

    void OnMoveAllToCombo(object? sender, RoutedEventArgs e)
    {
        // Move everything, no selection needed: every file that produced a match,
        // or the current source file when there are no results yet.
        var files = _grepRows.Select(r => r.FullPath).Distinct().Where(File.Exists).ToList();
        if (files.Count == 0 && _sourceIsFile && File.Exists(_sourcePath))
            files.Add(_sourcePath);
        if (files.Count == 0) { StatCurrent.Text = "Nothing to send — run a search first."; return; }
        AddComboFiles(files);
        Tabs.SelectedIndex = 1;
        ComboStatus.Text = $"Added {files.Count} file(s) from Search.";
    }

    // ─────────────────────────────── Keyboard ───────────────────────────────

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool searchFocused = SearchBox.IsFocused;

        if (cmd && e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { OnBrowseFolder(sender, e); e.Handled = true; }
        else if (cmd && e.Key == Key.O) { OnBrowseFile(sender, e); e.Handled = true; }
        else if (cmd && (e.Key == Key.F || e.Key == Key.L)) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (cmd && e.Key == Key.Enter) { OnRunGrep(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape && _grepBusy) { OnStopGrep(); e.Handled = true; }
        else if (Tabs.SelectedIndex == 0 && !searchFocused)
        {
            if (cmd && e.Key == Key.A) { ResultsGrid.SelectAll(); e.Handled = true; }
            else if (cmd && e.Key == Key.C && Selected().Count > 0) { OnCopySelected(sender, e); e.Handled = true; }
            else if ((e.Key == Key.Delete || e.Key == Key.Back) && ResultsGrid.IsKeyboardFocusWithin && Selected().Count > 0)
            { OnRemoveSelected(sender, e); e.Handled = true; }
        }
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
        if (file != null) OutBox.Text = file.Path.LocalPath;
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

        long size = 0;
        foreach (var f in _comboFiles)
            try { size += new FileInfo(f).Length; } catch { }
        SizeCount.Text = HumanBytes(size);

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
                files, outPath, dedup, emailOnly, userOnly, preview, ComboPreviewMax, OnProgress, _comboCancel.Token));
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
        catch (OperationCanceledException) { ComboStatus.Text = "Stopped (partial output saved)."; }
        catch (Exception ex) { ComboStatus.Text = $"⚠ {ex.Message}"; }
        finally
        {
            _comboBusy = false;
            ComboGoBtn.IsEnabled = true;
            ComboStopBtn.IsEnabled = false;
            ComboGoBtn.Content = "Start";
            _comboCancel?.Dispose();
            _comboCancel = null;
        }
    }

    void OnStopCombo(object? sender, RoutedEventArgs e) { _comboCancel?.Cancel(); ComboStatus.Text = "Stopping…"; }

    // ─────────────────────────────── Drag & drop ────────────────────────────

    void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;

    void OnDrop(object? sender, DragEventArgs e)
    {
        var items = e.Data.GetFiles();
        if (items == null) return;
        var paths = items.Select(i => i.Path.LocalPath).ToList();

        if (Tabs.SelectedIndex == 1)   // Combo tab: add all files
        {
            AddComboFiles(paths.Where(File.Exists));
        }
        else                            // Search tab: use the first as source
        {
            var first = paths.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
            if (first != null) SetSource(first);
        }
    }

    // ──────────────────────────────── Helpers ───────────────────────────────

    void UpdateResultCount()
    {
        int n = _grepRows.Count;
        ResultCount.Text = n == 0 ? "No results"
            : $"{n:N0} result{(n == 1 ? "" : "s")}" + (_grepCapped ? " (capped)" : "");
    }

    void UpdateEmptyState() => EmptyState.IsVisible = _grepRows.Count == 0;

    async Task CopyText(string text)
    {
        var cb = TopLevel.GetTopLevel(this)?.Clipboard;
        if (cb != null) await cb.SetTextAsync(text);
    }

    void ShowMenu(Control target, List<(string label, Action act)> items, string emptyLabel)
    {
        var menu = new MenuFlyout();
        if (items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = emptyLabel, IsEnabled = false });
        }
        else
        {
            foreach (var (label, act) in items)
            {
                var mi = new MenuItem { Header = label };
                mi.Click += (_, _) => act();
                menu.Items.Add(mi);
            }
        }
        menu.ShowAt(target);
    }

    static string ShortPath(string path)
    {
        var name = Path.GetFileName(path);
        var dir = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        return string.IsNullOrEmpty(dir) ? name : $"{dir}/{name}";
    }

    static string FormatSeconds(double s)
    {
        if (s < 1) return "<1s";
        if (s < 60) return $"{s:F0}s";
        if (s < 3600) return $"{(int)(s / 60)}m {(int)(s % 60)}s";
        return $"{(int)(s / 3600)}h {(int)(s % 3600 / 60)}m";
    }

    static string HumanBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = { "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return v >= 100 ? $"{v:F0} {units[i]}" : $"{v:F1} {units[i]}";
    }

    void OpenAtLine(string path, long line)
    {
        // Best effort: open at the line in VS Code if present, else the default app.
        try
        {
            if (TryStart("code", $"--goto \"{path}:{line}\"")) return;
        }
        catch { }
        OpenInDefaultApp(path);
    }

    static bool TryStart(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi);
            return p != null;
        }
        catch { return false; }
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
        catch { }
    }

    static void RevealInFinder(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path }, UseShellExecute = false });
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path) ?? path }, UseShellExecute = false });
        }
        catch { }
    }
}
