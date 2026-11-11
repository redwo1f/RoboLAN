using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RoboLAN;

public enum JobState { Waiting, Copying, Done, NothingToCopy, Failed, Cancelled }

public class CopyJob
{
    public string Source, Dest, Name, Machine;
    public AppSettings Options;
    public JobState State = JobState.Waiting;
    public bool InBatch = true, ScanStarted, ScanDone;
    public int ExitCode;
    public long TotalBytes, TotalFiles, CopiedBytes, CopiedFiles;
    public Task ScanTask;
    public ListViewItem Row;
    public readonly CancellationTokenSource Cts = new();
    readonly List<string> log = new();
    readonly Stopwatch clock = new();
    TimeSpan sampleTime;
    long sampleBytes;
    static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(10);
    public Process CopyProcess;
    readonly Queue<(TimeSpan At, long Bytes)> speedSamples = new();
    bool speedKnown;
    public long CurrentSpeed { get; private set; }

#if DEBUG
    static readonly TimeSpan SpeedEvery = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("ROBOLAN_SPEED_SECONDS"), out int s) ? s : 300);
#else
    static readonly TimeSpan SpeedEvery = TimeSpan.FromMinutes(5);
#endif

    public bool Finished => State >= JobState.Done;

    public string CopyCommand => AppSettings.FormatCommand(Options.BuildArgs(Source, Dest, false));

    public void AddFile(long size)
    {
        Interlocked.Increment(ref CopiedFiles);
        Interlocked.Add(ref CopiedBytes, size);
    }

    public void AddScanned(long size)
    {
        Interlocked.Increment(ref TotalFiles);
        Interlocked.Add(ref TotalBytes, size);
    }

    public void Log(string line)
    {
        lock (log)
        {
            if (log.Count >= 500) return;
            log.Add(line);
        }
        CopyLog.Write("ROBOCOPY", this, line);
    }

    // A retried file is counted again, so cap at the scanned total.
    long FilesDone => Math.Min(Interlocked.Read(ref CopiedFiles), Interlocked.Read(ref TotalFiles));

    // Called on every UI refresh. Robocopy's output arrives in delayed chunks of about 30 files on a slow link,
    // so speed comes from the bytes the process has actually read. A stall reads as 0 B/s.
    public void SampleSpeed()
    {
        speedKnown = false;
        CurrentSpeed = 0;
        if (State != JobState.Copying) { speedSamples.Clear(); return; }

        long bytes = Robocopy.BytesRead(CopyProcess);
        if (bytes < 0) return;
        var now = clock.Elapsed;
        speedSamples.Enqueue((now, bytes));
        while (now - speedSamples.Peek().At > SpeedWindow) speedSamples.Dequeue();

        var oldest = speedSamples.Peek();
        double seconds = (now - oldest.At).TotalSeconds;
        if (seconds < 3) return;
        speedKnown = true;
        CurrentSpeed = (long)((bytes - oldest.Bytes) / seconds);
    }

    public string SpeedText => speedKnown ? FileSource.FormatBytes(CurrentSpeed) + "/s" : "";

    public void LogStart()
    {
        clock.Restart();
        CopyLog.Write("START", this, $"to {Dest}, {TotalFiles:N0} files, {FileSource.FormatBytes(TotalBytes)}");
    }

    public void LogSpeedIfDue()
    {
        var elapsed = clock.Elapsed;
        if (State != JobState.Copying || elapsed - sampleTime < SpeedEvery) return;

        long bytes = DoneBytes;
        var span = elapsed - sampleTime;
        CopyLog.Write("SPEED", this, $"{FileSource.FormatBytes((long)((bytes - sampleBytes) / span.TotalSeconds))}/s over {FileSource.FormatDuration(span.TotalSeconds)}, " +
            $"{FileSource.FormatBytes(bytes)} of {FileSource.FormatBytes(TotalBytes)}, {FilesDone:N0} of {TotalFiles:N0} files");
        sampleTime = elapsed;
        sampleBytes = bytes;
    }

    public void LogEnd()
    {
        double seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
        long bytes = DoneBytes;
        string exit = State == JobState.Cancelled ? "" : $", exit code {ExitCode}";
        CopyLog.Write(State.ToString().ToUpperInvariant(), this, $"{FilesDone:N0} of {TotalFiles:N0} files, {FileSource.FormatBytes(bytes)} " +
            $"in {FileSource.FormatDuration(seconds)}, average {FileSource.FormatBytes((long)(bytes / seconds))}/s{exit}");
    }

    public string LogText { get { lock (log) return string.Join(Environment.NewLine, log); } }

    public long DoneBytes => State is JobState.Done or JobState.NothingToCopy
        ? TotalBytes
        : Math.Min(Interlocked.Read(ref CopiedBytes), Interlocked.Read(ref TotalBytes));

    public int Percent
    {
        get
        {
            if (State is JobState.Done or JobState.NothingToCopy) return 100;
            long total = Interlocked.Read(ref TotalBytes);
            if (!ScanDone || total == 0) return 0;
            return (int)Math.Min(100, Math.Min(Interlocked.Read(ref CopiedBytes), total) * 100 / total);
        }
    }

    public string StatusText => State switch
    {
        JobState.Waiting => ScanDone ? "Queued..." : ScanStarted ? "Scanning..." : "Waiting...",
        JobState.Copying => Cts.IsCancellationRequested ? "Cancelling..." : "Copying",
        JobState.Done => "Done",
        JobState.NothingToCopy => "Nothing to copy",
        JobState.Failed => $"Failed (code {ExitCode})",
        _ => "Cancelled"
    };
}

// Appends to a text file beside the exe. A failed write never interrupts a copy.
public static class CopyLog
{
    static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "RoboLAN-log.txt");
    static readonly object gate = new();

    public static void Write(string label, CopyJob job, string detail)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {label,-13}  {job.Machine,-14}  {job.Name}  {detail}{Environment.NewLine}";
        try { lock (gate) File.AppendAllText(FilePath, line); } catch { }
    }
}

public static class Robocopy
{
    static readonly Regex FileLine = new(@"^\s*(\d+)\t(.+)$", RegexOptions.Compiled);

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [DllImport("kernel32.dll")]
    static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    // Returns -1 once the process is gone.
    public static long BytesRead(Process process)
    {
        try { return process != null && GetProcessIoCounters(process.Handle, out var c) ? (long)c.ReadBytes : -1; }
        catch { return -1; }
    }

    public static async Task<int> RunAsync(IEnumerable<string> args, Action<long> onFile, Action<string> onOther, CancellationToken token, Action<Process> onStarted = null)
    {
        token.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "robocopy.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        onStarted?.Invoke(process);
        using var kill = token.Register(() => { try { process.Kill(true); } catch { } });

        string line;
        while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            var match = FileLine.Match(line);
            if (match.Success) onFile(long.Parse(match.Groups[1].Value));
            else if (!string.IsNullOrWhiteSpace(line)) onOther(line.Trim());
        }
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }
}
