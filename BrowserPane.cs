using System.Collections;

namespace RobocopyGui;

public class BrowserPane : UserControl
{
    const string DragFormat = "RobocopyGui.Folders";

    readonly bool remote;
    readonly AppSettings settings;
    readonly ThemedListView list = new();
    readonly AddressBar address;
    readonly Label status = new();
    readonly EntrySorter sorter = new();
    bool discovering, discovered, fitting, nameSizedByUser;
    int loadId;
    bool loaded;
    long singleClickTicks;
    ListViewItem dropRow;

    public string CurrentPath { get; private set; } = "";

    /// <summary>Raised on the destination pane: source folders, target folder.</summary>
    public event Action<string[], string> FoldersDropped;

    public BrowserPane(bool isRemote, string title, AppSettings appSettings)
    {
        remote = isRemote;
        settings = appSettings;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 0) }, 0, 0);

        var nav = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = new Padding(0) };
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nav.Controls.Add(MakeButton("Up", (_, _) => GoUp()), 0, 0);
        nav.Controls.Add(MakeButton("Refresh", (_, _) => Reload()), 1, 0);
        if (!remote) nav.Controls.Add(MakeButton("New Folder", (_, _) => NewFolder()), 2, 0);
        address = new AddressBar(remote ? "Network" : "This PC") { Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 3) };
        address.PathRequested += Navigate;
        nav.Controls.Add(address, 3, 0);
        layout.Controls.Add(nav, 0, 1);

        list.Dock = DockStyle.Fill;
        list.MultiSelect = true;
        list.ShowItemToolTips = true;
        list.SmallImageList = FileSource.CreateIcons();
        list.ListViewItemSorter = sorter;
        float scale = DeviceDpi / 96f;
        list.Columns.Add("Name", (int)(120 * scale)); // FitNameColumn widens it. Starting wide leaves a stuck scrollbar.
        list.Columns.Add("Date modified", (int)(155 * scale));
        list.Columns.Add("Type", (int)(80 * scale));
        list.Columns.Add("Size", (int)(62 * scale), HorizontalAlignment.Right);
        list.ColumnClick += (_, e) =>
        {
            sorter.Descending = sorter.Column == e.Column && !sorter.Descending;
            sorter.Column = e.Column;
            list.Sort();
        };
        list.ColumnWidthChanged += (_, e) =>
        {
            if (fitting || !loaded) return; // Windows also raises this while creating the list.
            if (e.ColumnIndex == 0) nameSizedByUser = true;
            else FitNameColumn();
        };
        list.ItemActivate += (_, _) => OnActivate();
        list.MouseClick += OnMouseClick;
        list.KeyDown += OnListKeyDown;
        list.Resize += (_, _) => FitNameColumn();
        if (remote) list.ItemDrag += OnItemDrag;
        else
        {
            list.AllowDrop = true;
            list.DragEnter += OnDragOver;
            list.DragOver += OnDragOver;
            list.DragLeave += (_, _) => SetDropRow(null);
            list.DragDrop += OnDragDrop;
        }
        layout.Controls.Add(list, 0, 2);

        status.AutoSize = true;
        status.Margin = new Padding(3);
        status.AutoEllipsis = false;
        layout.Controls.Add(status, 0, 3);
        Controls.Add(layout);
    }

    Button MakeButton(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(3), Padding = new Padding(4, 0, 4, 0) };
        button.Click += click;
        return button;
    }

    public PaneLayout GetLayout() => new()
    {
        Columns = list.Columns.Cast<ColumnHeader>().Select((c, i) => i == 0 && !nameSizedByUser ? 0 : c.Width).ToArray(),
        SortColumn = sorter.Column,
        SortDescending = sorter.Descending
    };

    public void SetLayout(PaneLayout layout)
    {
        if (layout == null) return;
        sorter.Column = Math.Clamp(layout.SortColumn, 0, list.Columns.Count - 1);
        sorter.Descending = layout.SortDescending;
        if (layout.Columns?.Length != list.Columns.Count) return;
        fitting = true;
        for (int i = 0; i < layout.Columns.Length; i++)
            if (layout.Columns[i] > 0) list.Columns[i].Width = Math.Max(30, layout.Columns[i]);
        fitting = false;
        nameSizedByUser = layout.Columns[0] > 0;
        FitNameColumn();
    }

    void FitNameColumn()
    {
        if (nameSizedByUser) return;
        int others = list.Columns[1].Width + list.Columns[2].Width + list.Columns[3].Width;
        // Always reserve the vertical scrollbar's width so a horizontal one never appears.
        int width = list.ClientSize.Width - others - SystemInformation.VerticalScrollBarWidth - 4;
        if (width <= 120) return;
        fitting = true;
        list.Columns[0].Width = width;
        fitting = false;
        list.DropUnneededHorizontalBar();
    }

    public void GoUp()
    {
        if (CurrentPath != "") Navigate(FileSource.Parent(CurrentPath));
    }

    public void Reload()
    {
        if (CurrentPath == "") discovered = false;
        Navigate(CurrentPath);
    }

    public void ReloadIfAtRoot()
    {
        if (remote && CurrentPath == "") Navigate("");
    }

    // Windows takes about 30 seconds to enumerate the network, so the last known PCs are shown meanwhile.
    async void DiscoverHosts()
    {
        if (discovering || discovered) return;
        discovering = true;
        status.Text = "Searching the network, this can take 30 seconds...";
        var found = await Task.Run(FileSource.DiscoverHosts);
        discovering = false;
        discovered = true;
        if (found.Count > 0)
        {
            settings.KnownPCs = found;
            try { settings.Save(); } catch { }
        }
        if (remote && CurrentPath == "") Navigate("");
    }

    public async void Navigate(string path)
    {
        path = FileSource.Normalize(path);
        int id = ++loadId;
        status.Text = "Loading...";
        try
        {
            var entries = await Task.Run(() => LoadEntries(path));
            if (id != loadId) return;
            Show(path, entries);
        }
        catch (Exception ex)
        {
            if (id != loadId) return;
            status.Text = $"{path}: {ex.Message}";
            if (!loaded) { loaded = true; ShowRootAfterFailure(status.Text); }
        }
    }

    async void ShowRootAfterFailure(string message)
    {
        var entries = await Task.Run(() => LoadEntries(""));
        Show("", entries);
        status.Text = message;
    }

    List<Entry> LoadEntries(string path)
    {
        if (path == "")
        {
            if (!remote) return FileSource.ListDrives();
            return FileSource.HostEntries(settings.KnownPCs.Concat(settings.ManualPCs));
        }
        if (FileSource.IsHostPath(path)) return FileSource.ListShares(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Enter a full path, like Z:\\AI or \\\\PC\\share.");
        return FileSource.ListDirectory(path);
    }

    void Show(string path, List<Entry> entries)
    {
        loaded = true;
        CurrentPath = path;
        address.SetPath(path);
        RememberHost(path);
        list.BeginUpdate();
        list.Items.Clear();
        dropRow = null;
        list.Items.AddRange(entries.Select(MakeRow).ToArray());
        list.EndUpdate();
        FitNameColumn();
        status.Text = entries.Count == 0 && remote && path == ""
            ? "No PCs found. Add one in Settings."
            : $"{entries.Count} items";
        if (remote && path == "") DiscoverHosts();
    }

    // A PC reached by typing its name may never show up in Windows discovery, so keep it in the list.
    void RememberHost(string path)
    {
        if (!path.StartsWith(@"\\")) return;
        string host = FileSource.MachineOf(path);
        if (settings.KnownPCs.Concat(settings.ManualPCs).Contains(host, StringComparer.OrdinalIgnoreCase)) return;
        settings.ManualPCs.Add(host);
        try { settings.Save(); } catch { }
    }

    static ListViewItem MakeRow(Entry e)
    {
        var row = new ListViewItem(e.Name, e.Icon) { Tag = e };
        row.SubItems.Add(e.Modified?.ToString("g") ?? "");
        row.SubItems.Add(e.Type);
        row.SubItems.Add(e.Size >= 0 ? FileSource.FormatBytes(e.Size) : "");
        return row;
    }

    void OnActivate()
    {
        if (Environment.TickCount64 - singleClickTicks < SystemInformation.DoubleClickTime) return;
        if (list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is Entry { IsContainer: true } entry)
            Navigate(entry.Path);
    }

    void OnMouseClick(object sender, MouseEventArgs e)
    {
        // PCs open on a single click. Folders use double-click so they can be selected and dragged.
        if (e.Button != MouseButtons.Left || ModifierKeys != Keys.None) return;
        if (list.GetItemAt(e.X, e.Y)?.Tag is Entry { Kind: EntryKind.Host } entry)
        {
            singleClickTicks = Environment.TickCount64;
            Navigate(entry.Path);
        }
    }

    void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Back) GoUp();
        else if (e.KeyCode == Keys.F5) Reload();
        else if (e.Control && e.KeyCode == Keys.A)
        {
            foreach (ListViewItem item in list.Items) item.Selected = true;
        }
    }

    void NewFolder()
    {
        if (CurrentPath == "") { status.Text = "Open a drive or folder first."; return; }
        string name = Dialogs.Prompt(FindForm(), "New Folder", "Folder name:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(CurrentPath, name.Trim()));
            Navigate(CurrentPath);
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    void OnItemDrag(object sender, ItemDragEventArgs e)
    {
        var selected = list.SelectedItems.Cast<ListViewItem>().Select(i => (Entry)i.Tag).ToList();
        string[] folders = selected.Where(s => s.Kind is EntryKind.Folder or EntryKind.Share).Select(s => s.Path).ToArray();
        if (folders.Length == 0) { status.Text = "Only folders can be dragged."; return; }
        if (folders.Length < selected.Count) status.Text = "Files in the selection are skipped. Only folders are copied.";
        list.DoDragDrop(new DataObject(DragFormat, folders), DragDropEffects.Copy);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat)) { e.Effect = DragDropEffects.None; return; }
        var (row, target) = DropTargetAt(new Point(e.X, e.Y));
        SetDropRow(row);
        e.Effect = target != null ? DragDropEffects.Copy : DragDropEffects.None;
        status.Text = target != null ? $"Copy into {target}" : "Open a drive or folder first.";
    }

    void OnDragDrop(object sender, DragEventArgs e)
    {
        var (_, target) = DropTargetAt(new Point(e.X, e.Y));
        SetDropRow(null);
        status.Text = $"{list.Items.Count} items";
        if (target != null) FoldersDropped?.Invoke((string[])e.Data.GetData(DragFormat), target);
    }

    // Over a folder's name: into that folder. Anywhere else: the folder being shown (Explorer's behavior).
    (ListViewItem row, string target) DropTargetAt(Point screen)
    {
        var point = list.PointToClient(screen);
        var row = point.X < list.Columns[0].Width ? list.GetItemAt(point.X, point.Y) : null;
        if (row?.Tag is Entry { IsContainer: true } entry) return (row, entry.Path);
        return (null, CurrentPath == "" ? null : CurrentPath);
    }

    void SetDropRow(ListViewItem row)
    {
        if (row == dropRow) return;
        if (dropRow != null) { dropRow.BackColor = Color.Empty; dropRow.ForeColor = Color.Empty; }
        dropRow = row;
        if (row != null) { row.BackColor = Theme.Accent; row.ForeColor = Theme.AccentText; }
    }

    class EntrySorter : IComparer
    {
        public int Column;
        public bool Descending;

        public int Compare(object x, object y)
        {
            var a = (Entry)((ListViewItem)x).Tag;
            var b = (Entry)((ListViewItem)y).Tag;
            if (a.IsContainer != b.IsContainer) return a.IsContainer ? -1 : 1;
            int result = Column switch
            {
                1 => Nullable.Compare(a.Modified, b.Modified),
                2 => string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase),
                3 => a.Size.CompareTo(b.Size),
                _ => 0
            };
            if (result == 0) result = FileSource.StrCmpLogicalW(a.Name, b.Name);
            return Descending ? -result : result;
        }
    }
}
