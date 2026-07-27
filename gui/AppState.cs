using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RomboTool;

/// <summary>Small persisted state: recent files, recent searches, and the chosen theme.</summary>
public sealed class AppState
{
    public List<string> RecentFiles { get; set; } = new();
    public List<string> RecentSearches { get; set; } = new();
    public string Theme { get; set; } = "Dark";

    const int MaxRecentFiles = 10;
    const int MaxRecentSearches = 15;

    static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RomboTool");
    static string StatePath => Path.Combine(Dir, "state.json");

    public static AppState Load()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath)) ?? new AppState();
        }
        catch { /* corrupt or unreadable: start fresh */ }
        return new AppState();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    public void PushRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        RecentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }

    public void PushRecentSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        RecentSearches.RemoveAll(q => string.Equals(q, query, StringComparison.Ordinal));
        RecentSearches.Insert(0, query);
        if (RecentSearches.Count > MaxRecentSearches) RecentSearches.RemoveRange(MaxRecentSearches, RecentSearches.Count - MaxRecentSearches);
    }
}
