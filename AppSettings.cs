using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobocopyGui;

public class PaneLayout
{
    // Columns[0] is 0 while the Name column is still auto-filling the pane.
    public int[] Columns { get; set; }
    public int SortColumn { get; set; }
    public bool SortDescending { get; set; }
}

public class WindowLayout
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public int MainSplit { get; set; }
    public int PaneSplit { get; set; }
    public PaneLayout Left { get; set; }
    public PaneLayout Right { get; set; }
    public int[] QueueColumns { get; set; }
}

public class AppSettings
{
    public WindowLayout Layout { get; set; }
    public bool DarkMode { get; set; } = true;
    public int Threads { get; set; } = 32;
    public int Retries { get; set; } = 3;
    public int WaitSeconds { get; set; } = 5;
    public bool CopySubfolders { get; set; } = true;
    public bool ExcludeJunctions { get; set; } = true;
    public string IncludeFiles { get; set; } = "";
    public string ExcludeFiles { get; set; } = "";
    public string ExtraArgs { get; set; } = "";
    public string DefaultLocalPath { get; set; } = @"Z:\AI\Track Images";
    public bool ShortenNames { get; set; } = true;
    // "Feb-22-2026-Nasa AZ (Sun) [[50bc3da25c]]_half_q78_m3000" -> "Feb-22-2026-Nasa AZ"
    public string ShortenPattern { get; set; } = @"\s*\([A-Za-z]{3}\)\s*\[\[[0-9a-fA-F]+\]\].*$";
    public List<string> ManualPCs { get; set; } = new();
    public List<string> KnownPCs { get; set; } = new();

#if DEBUG
    // Keeps test runs away from the real settings file.
    static string FilePath => Environment.GetEnvironmentVariable("ROBOCOPYGUI_SETTINGS") ?? DefaultFilePath;
#else
    static string FilePath => DefaultFilePath;
#endif

    static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobocopyGUI", "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public string DestinationName(string sourceName)
    {
        if (!ShortenNames || string.IsNullOrWhiteSpace(ShortenPattern)) return sourceName;
        try
        {
            string shortened = Regex.Replace(sourceName, ShortenPattern, "").Trim().TrimEnd('.');
            return shortened.Length > 0 ? shortened : sourceName;
        }
        catch (ArgumentException) { return sourceName; }
    }

    // Accepts "*.jpg, *.png", "*.jpg;*.png", "*.jpg *.png" and bare extensions like "jpg".
    public static List<string> ParseFilters(string text)
    {
        var result = new List<string>();
        foreach (string raw in Regex.Split(text ?? "", @"[,;]+|\s+(?=\*)"))
        {
            string token = raw.Trim().Trim('"');
            if (token.Length == 0) continue;
            result.Add(token.IndexOfAny(new[] { '*', '?', '.' }) < 0 ? "*." + token : token);
        }
        return result;
    }

    public List<string> BuildArgs(string source, string dest, bool scanOnly)
    {
        var args = new List<string> { source, dest };
        args.AddRange(ParseFilters(IncludeFiles));
        if (CopySubfolders) args.Add("/E");
        var excludes = ParseFilters(ExcludeFiles);
        if (excludes.Count > 0)
        {
            args.Add("/XF");
            args.AddRange(excludes);
        }
        if (scanOnly) args.AddRange(new[] { "/L", "/R:0", "/W:0" });
        else args.AddRange(new[] { $"/MT:{Threads}", $"/R:{Retries}", $"/W:{WaitSeconds}" });
        if (ExcludeJunctions) args.Add("/XJ");
        // Output format the progress parser depends on.
        args.AddRange(new[] { "/NP", "/BYTES", "/NDL", "/NJH", "/NJS", "/NC" });
        foreach (Match m in Regex.Matches(ExtraArgs ?? "", "\"[^\"]*\"|\\S+"))
            args.Add(m.Value.Trim('"'));
        return args;
    }

    public static string FormatCommand(IEnumerable<string> args) =>
        "robocopy " + string.Join(" ", args.Select(a => a.Contains(' ') || a.Contains('<') ? $"\"{a}\"" : a));
}
