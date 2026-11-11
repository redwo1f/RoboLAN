using System.Diagnostics;

namespace RoboLAN;

public class MainForm : Form
{
    readonly AppSettings settings = AppSettings.Load();
    readonly BrowserPane left, right;
    readonly TextBox includeBox = new() { PlaceholderText = "all files, or e.g. *.jpg, *.png" };
    readonly TextBox excludeBox = new() { PlaceholderText = "none, or e.g. *.mp4, *.txt" };
    readonly QueueList queue = new();
    readonly Meter overallBar = new() { Dock = DockStyle.Fill, Maximum = 1000, Margin = new Padding(3, 5, 3, 5) };
    readonly Label overallLabel = new() { AutoSize = true, Margin = new Padding(3, 7, 3, 3), Text = "Drag folders from the left pane to the right pane to copy." };
    readonly Button cancelSelected, cancelAll, clearFinished;
    readonly SplitContainer outer = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    readonly SplitContainer panes = new() { Dock = DockStyle.Fill };
    readonly List<CopyJob> jobs = new();
    readonly Dictionary<string, SemaphoreSlim> scanGates = new(StringComparer.OrdinalIgnoreCase); // 3 scans per machine.
    readonly Stopwatch batchClock = new();
    readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 250 };
    readonly string[] startPaths;
    readonly HashSet<string> pumping = new(StringComparer.OrdinalIgnoreCase); // Machines with a copy queue running.

    public MainForm(string[] args)
    {
        startPaths = args;
        Theme.Dark = settings.DarkMode;
        Text = "RoboLAN";
        ClientSize = new Size(LogicalToDeviceUnits(1180), LogicalToDeviceUnits(760));
        MinimumSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(520));
        StartPosition = FormStartPosition.CenterScreen;

        left = new BrowserPane(true, "Network PCs (source)", settings) { Dock = DockStyle.Fill };
        right = new BrowserPane(false, "This computer (destination)", settings) { Dock = DockStyle.Fill };
        right.FoldersDropped += OnFoldersDropped;
        panes.Panel1.Controls.Add(left);
        panes.Panel2.Controls.Add(right);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildFilterBar(), 0, 0);
        root.Controls.Add(outer, 0, 1);
        Controls.Add(root);

        outer.Panel1.Controls.Add(panes);

        float scale = DeviceDpi / 96f;
        queue.Columns.Add("Machine", (int)(140 * scale));
        queue.Columns.Add("Parent folder", (int)(175 * scale));
        queue.Columns.Add("Destination", (int)(190 * scale));
        queue.Columns.Add("Status", (int)(110 * scale));
        queue.Columns.Add("Files", (int)(90 * scale), HorizontalAlignment.Right);
        queue.Columns.Add("Size", (int)(150 * scale), HorizontalAlignment.Right);
        queue.Columns.Add("Speed", (int)(95 * scale), HorizontalAlignment.Right);
        queue.Columns.Add("Progress", (int)(190 * scale));
        queue.DoubleClick += (_, _) => ShowJobDetails();

        cancelSelected = MakeButton("Cancel Selected", (_, _) =>
        {
            foreach (ListViewItem row in queue.SelectedItems) CancelJob((CopyJob)row.Tag);
        });
        cancelAll = MakeButton("Cancel All", (_, _) => CancelEverything());
        clearFinished = MakeButton("Clear Finished", (_, _) => ClearFinished());

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.Controls.Add(queue, 0, 0);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, RowCount = 2, Margin = new Padding(0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(new Label { Text = "Overall", AutoSize = true, Margin = new Padding(3, 7, 3, 3), Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        footer.Controls.Add(overallBar, 1, 0);
        footer.Controls.Add(cancelSelected, 2, 0);
        footer.Controls.Add(cancelAll, 3, 0);
        footer.Controls.Add(clearFinished, 4, 0);
        footer.Controls.Add(overallLabel, 0, 1);
        footer.SetColumnSpan(overallLabel, 5);
        bottom.Controls.Add(footer, 0, 1);
        outer.Panel2.Controls.Add(bottom);

        uiTimer.Tick += (_, _) => RefreshProgress();
        uiTimer.Start();
        UpdateButtons();
        Load += OnFormLoad;
        Shown += (_, _) => RestoreSplitters();
        FormClosing += OnFormClosing;
        Theme.Apply(this);
        RestoreWindowLayout();
#if DEBUG
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-4000, 50);
        ShowInTaskbar = false;
#endif
    }

#if DEBUG
    // Test builds never take focus from whoever is using the PC.
    protected override bool ShowWithoutActivation => true;
#endif

    Control BuildFilterBar()
    {
        includeBox.Text = settings.IncludeFiles;
        excludeBox.Text = settings.ExcludeFiles;

        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, RowCount = 1, Margin = new Padding(0) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        includeBox.Dock = excludeBox.Dock = DockStyle.Fill;
        includeBox.Margin = excludeBox.Margin = new Padding(3, 5, 12, 3);
        bar.Controls.Add(new Label { Text = "Only copy:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 0);
        bar.Controls.Add(includeBox, 1, 0);
        bar.Controls.Add(new Label { Text = "Exclude:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 2, 0);
        bar.Controls.Add(excludeBox, 3, 0);
        bar.Controls.Add(MakeButton("Settings", (_, _) => OpenSettings()), 4, 0);
        return bar;
    }

    Button MakeButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(3), Padding = new Padding(4, 0, 4, 0) };
        button.Click += click;
        return button;
    }

    void RestoreWindowLayout()
    {
        var layout = settings.Layout;
        if (layout == null) return;

        left.SetLayout(layout.Left);
        right.SetLayout(layout.Right);
        if (layout.QueueColumns?.Length == queue.Columns.Count)
            for (int i = 0; i < layout.QueueColumns.Length; i++)
                queue.Columns[i].Width = Math.Max(30, layout.QueueColumns[i]);

        if (layout.Width <= 0 || layout.Height <= 0) return;
        Size = new Size(layout.Width, layout.Height);
        // Skip the saved position if that monitor is gone, so the window can't open out of reach.
        var saved = new Rectangle(layout.X, layout.Y, layout.Width, layout.Height);
        if (Screen.AllScreens.Any(s => { var overlap = Rectangle.Intersect(s.WorkingArea, saved); return overlap.Width >= 200 && overlap.Height >= 100; }))
        {
            StartPosition = FormStartPosition.Manual;
            Location = saved.Location;
        }
#if !DEBUG
        if (layout.Maximized) WindowState = FormWindowState.Maximized;
#endif
    }

    // Runs after the window is shown so a maximized window has its final size.
    void RestoreSplitters()
    {
        var layout = settings.Layout;
        SetSplitter(outer, outer.Height, layout?.MainSplit > 0 ? layout.MainSplit : (int)(ClientSize.Height * 0.62));
        SetSplitter(panes, panes.Width, layout?.PaneSplit > 0 ? layout.PaneSplit : panes.Width / 2);
    }

    static void SetSplitter(SplitContainer split, int total, int distance)
    {
        int max = Math.Max(split.Panel1MinSize, total - split.Panel2MinSize - split.SplitterWidth);
        split.SplitterDistance = Math.Clamp(distance, split.Panel1MinSize, max);
    }

    void SaveWindowLayout()
    {
        var previous = settings.Layout;
        bool minimized = WindowState == FormWindowState.Minimized;
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        settings.Layout = new WindowLayout
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == FormWindowState.Maximized,
            MainSplit = minimized ? previous?.MainSplit ?? 0 : outer.SplitterDistance,
            PaneSplit = minimized ? previous?.PaneSplit ?? 0 : panes.SplitterDistance,
            Left = left.GetLayout(),
            Right = right.GetLayout(),
            QueueColumns = queue.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray()
        };
    }

    void OnFormLoad(object sender, EventArgs e)
    {
        left.Navigate(startPaths.Length > 0 ? startPaths[0] : "");
        right.Navigate(startPaths.Length > 1 ? startPaths[1] : settings.DefaultLocalPath);
#if DEBUG
        // Test-only: "--drop <dest> <source>..." queues a copy without any real mouse input.
        int drop = Array.IndexOf(startPaths, "--drop");
        if (drop >= 0) OnFoldersDropped(startPaths[(drop + 2)..], startPaths[drop + 1]);
#endif
    }

    void OpenSettings()
    {
        SaveFilters();
        using var form = new SettingsForm(settings);
        if (form.ShowDialog(this) != DialogResult.OK) return;
        settings.Save();
        Theme.Dark = settings.DarkMode;
        Theme.Apply(this);
        left.ReloadIfAtRoot();
    }

    void SaveFilters()
    {
        settings.IncludeFiles = includeBox.Text.Trim();
        settings.ExcludeFiles = excludeBox.Text.Trim();
    }

    void OnFoldersDropped(string[] sources, string targetFolder)
    {
        SaveFilters();
        try { settings.Save(); } catch { /* losing the saved filter text must not block a copy */ }

        if (!jobs.Any(j => !j.Finished))
        {
            foreach (var old in jobs) old.InBatch = false;
            batchClock.Reset();
        }

        var options = settings.Clone();
        foreach (string source in sources)
        {
            string name = Path.GetFileName(source);
            string dest = Path.Combine(targetFolder, options.DestinationName(name));

            if (dest.StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase) || dest.Equals(source, StringComparison.OrdinalIgnoreCase))
            {
                overallLabel.Text = $"Skipped {name}: the destination is inside the source.";
                continue;
            }
            if (jobs.Any(j => !j.Finished && Same(j.Source, source) && Same(j.Dest, dest))) continue;

            // Two sources in one batch can shorten to the same name. Keep them apart.
            string unique = dest;
            for (int n = 2; jobs.Any(j => !j.Finished && Same(j.Dest, unique)); n++) unique = $"{dest} ({n})";

            var job = new CopyJob { Source = source, Dest = unique, Name = name, Machine = FileSource.MachineOf(source), Options = options };
            job.Row = new ListViewItem(new[] { job.Machine, name, unique, "", "", "", "", "" }) { Tag = job };
            jobs.Add(job);
            queue.Items.Add(job.Row);
            job.ScanTask = ScanAsync(job);
        }
        UpdateButtons();
        foreach (string machine in jobs.Where(j => j.State == JobState.Waiting).Select(j => j.Machine).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            Pump(machine);
    }

    static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    async Task ScanAsync(CopyJob job)
    {
        try
        {
            if (!scanGates.TryGetValue(job.Machine, out var gate)) scanGates[job.Machine] = gate = new SemaphoreSlim(3);
            await gate.WaitAsync(job.Cts.Token);
            job.ScanStarted = true;
            try
            {
                await Robocopy.RunAsync(job.Options.BuildArgs(job.Source, job.Dest, true), job.AddScanned, _ => { }, job.Cts.Token);
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { job.Log("Scan failed: " + ex.Message); }
        job.ScanDone = true;
    }

    // One copy at a time per machine. Different machines run at the same time.
    async void Pump(string machine)
    {
        if (!pumping.Add(machine)) return;
        try
        {
            CopyJob job;
            while ((job = jobs.FirstOrDefault(j => j.State == JobState.Waiting && Same(j.Machine, machine))) != null) await RunJob(job);
        }
        finally
        {
            pumping.Remove(machine);
            if (pumping.Count == 0) batchClock.Stop();
            UpdateButtons();
        }
    }

    async Task RunJob(CopyJob job)
    {
        await job.ScanTask;
        if (job.State != JobState.Waiting) return;

        job.State = JobState.Copying;
        batchClock.Start();
        job.LogStart();
        job.Log(job.CopyCommand);
        try
        {
            job.ExitCode = await Robocopy.RunAsync(job.Options.BuildArgs(job.Source, job.Dest, false), job.AddFile, job.Log, job.Cts.Token, p => job.CopyProcess = p);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            job.Log(ex.Message);
            job.ExitCode = 16;
        }

        job.CopyProcess = null;
        if (job.Cts.IsCancellationRequested) job.State = JobState.Cancelled;
        else if (job.ExitCode >= 8) job.State = JobState.Failed;
        else if (job.ExitCode == 0) job.State = JobState.NothingToCopy;
        else job.State = JobState.Done;
        job.LogEnd();
    }

    void CancelJob(CopyJob job)
    {
        if (job.Finished) return;
        job.Cts.Cancel();
        if (job.State == JobState.Waiting) job.State = JobState.Cancelled;
    }

    void CancelEverything()
    {
        foreach (var job in jobs) CancelJob(job);
    }

    void ClearFinished()
    {
        foreach (var job in jobs.Where(j => j.Finished).ToList())
        {
            queue.Items.Remove(job.Row);
            jobs.Remove(job);
        }
        overallBar.Value = 0;
        overallLabel.Text = "";
        UpdateButtons();
    }

    void UpdateButtons()
    {
        bool active = jobs.Any(j => !j.Finished);
        cancelSelected.Enabled = cancelAll.Enabled = active;
        clearFinished.Enabled = !active && jobs.Count > 0;
    }

    void ShowJobDetails()
    {
        if (queue.SelectedItems.Count == 1 && queue.SelectedItems[0].Tag is CopyJob job)
            Dialogs.ShowText(this, job.Name, job.LogText.Length > 0 ? job.LogText : job.CopyCommand);
    }

    void RefreshProgress()
    {
        foreach (var job in jobs)
        {
            job.SampleSpeed();
            UpdateRow(job);
            job.LogSpeedIfDue();
        }
        UpdateButtons();

        var batch = jobs.Where(j => j.InBatch && j.State != JobState.Cancelled).ToList();
        if (batch.Count == 0)
        {
            overallBar.Value = 0;
            if (jobs.Any(j => j.InBatch)) overallLabel.Text = "Cancelled. Partly copied files stay in the destination. Drop the folder again to resume.";
            return;
        }

        long total = batch.Sum(j => Interlocked.Read(ref j.TotalBytes));
        // A failed event is over, so it counts as complete for the overall bar. Its row shows the real amount.
        long done = batch.Sum(j => j.Finished ? Interlocked.Read(ref j.TotalBytes) : j.DoneBytes);
        int finished = batch.Count(j => j.Finished);
        int failed = batch.Count(j => j.State == JobState.Failed);
        bool scanning = batch.Any(j => !j.ScanDone);

        int permille = total > 0 ? (int)Math.Min(1000, done * 1000 / total) : (finished == batch.Count ? 1000 : 0);
        overallBar.Value = permille;

        string text = $"{finished} of {batch.Count} folders, {FileSource.FormatBytes(done)} of {FileSource.FormatBytes(total)} ({permille / 10}%)";
        if (scanning) text += ", still scanning";
        double seconds = batchClock.Elapsed.TotalSeconds;
        if (finished < batch.Count && seconds > 2 && done > 0)
        {
            text += $", {FileSource.FormatBytes(batch.Sum(j => j.CurrentSpeed))}/s now";
            // Time left uses the whole-batch average so it doesn't jump around.
            double rate = batch.Sum(j => Interlocked.Read(ref j.CopiedBytes)) / seconds;
            if (!scanning && rate > 0) text += ", " + FileSource.FormatDuration((total - done) / rate) + " left";
        }
        if (failed > 0) text += $". {failed} failed, double-click the row for details";
        if (overallLabel.Text != text) overallLabel.Text = text;
    }

    void UpdateRow(CopyJob job)
    {
        SetCell(job.Row, 3, job.StatusText);
        long files = job.Finished && job.State != JobState.Failed && job.State != JobState.Cancelled ? job.TotalFiles : Math.Min(job.CopiedFiles, job.TotalFiles);
        SetCell(job.Row, 4, $"{files:N0} / {job.TotalFiles:N0}");
        SetCell(job.Row, 5, $"{FileSource.FormatBytes(job.DoneBytes)} / {FileSource.FormatBytes(job.TotalBytes)}");
        SetCell(job.Row, 6, job.SpeedText);
        SetCell(job.Row, 7, job.Percent + "%");
    }

    static void SetCell(ListViewItem row, int column, string text)
    {
        if (row.SubItems[column].Text != text) row.SubItems[column].Text = text;
    }

    void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (jobs.Any(j => !j.Finished))
        {
            var answer = MessageBox.Show(this, "Copies are still running. Stop them and exit?", "RoboLAN", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) { e.Cancel = true; return; }
            CancelEverything();
        }
        SaveFilters();
        SaveWindowLayout();
        try { settings.Save(); } catch { }
    }

    class QueueList : ThemedListView
    {
        public QueueList() => Dock = DockStyle.Fill;

        protected override void DrawCell(DrawListViewSubItemEventArgs e, Rectangle cell)
        {
            if (e.ColumnIndex != 7 || e.Item.Tag is not CopyJob job) { base.DrawCell(e, cell); return; }

            var (back, _) = RowColors(e.Item);
            var bar = Rectangle.Inflate(cell, -2, -2);
            var color = job.State == JobState.Failed ? Color.IndianRed : job.State == JobState.Cancelled ? Color.DarkGray : Theme.BarFill;
            using var backBrush = new SolidBrush(back);
            using var trackBrush = new SolidBrush(Theme.BarTrack);
            using var fillBrush = new SolidBrush(color);
            using var border = new Pen(Theme.Border);
            e.Graphics.FillRectangle(backBrush, cell);
            e.Graphics.FillRectangle(trackBrush, bar);
            e.Graphics.FillRectangle(fillBrush, bar.X, bar.Y, bar.Width * job.Percent / 100, bar.Height);
            e.Graphics.DrawRectangle(border, bar);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, bar, Theme.BarText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
