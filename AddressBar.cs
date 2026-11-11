namespace RobocopyGui;

/// <summary>Explorer-style address bar: clickable path segments, or click empty space to type or paste a path.</summary>
public class AddressBar : Panel
{
    readonly FlowLayoutPanel crumbs = new() { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
    readonly TextBox box = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Visible = false };
    readonly string rootName;
    string path = "";

    public event Action<string> PathRequested;

    public AddressBar(string rootName)
    {
        this.rootName = rootName;
        BorderStyle = BorderStyle.FixedSingle;
        Height = box.PreferredHeight + 10;
        Padding = new Padding(4, 3, 4, 0);
        Controls.Add(box);
        Controls.Add(crumbs);

        crumbs.Click += (_, _) => BeginEdit();
        Click += (_, _) => BeginEdit();
        Resize += (_, _) => FitCrumbs();
        box.Leave += (_, _) => EndEdit();
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                string typed = box.Text;
                EndEdit();
                PathRequested?.Invoke(typed);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                EndEdit();
            }
        };
    }

    public void ApplyTheme()
    {
        BackColor = crumbs.BackColor = box.BackColor = Theme.Field;
        ForeColor = crumbs.ForeColor = box.ForeColor = Theme.Text;
        SetPath(path);
    }

    public void SetPath(string newPath)
    {
        path = newPath;
        crumbs.SuspendLayout();
        while (crumbs.Controls.Count > 0) crumbs.Controls[0].Dispose();
        bool first = true;
        foreach (var (name, target) in Segments(newPath))
        {
            if (!first) crumbs.Controls.Add(MakeSeparator());
            crumbs.Controls.Add(MakeCrumb(name, target));
            first = false;
        }
        crumbs.ResumeLayout();
        FitCrumbs();
    }

    IEnumerable<(string name, string target)> Segments(string p)
    {
        yield return (rootName, "");
        if (p == "") yield break;

        if (p.StartsWith(@"\\"))
        {
            string[] parts = p.Substring(2).Split('\\', StringSplitOptions.RemoveEmptyEntries);
            string current = @"\\" + parts[0];
            yield return (parts[0], current);
            foreach (string part in parts.Skip(1))
            {
                current += "\\" + part;
                yield return (part, current);
            }
        }
        else
        {
            string current = p.Substring(0, 3);
            yield return (p.Substring(0, 2), current);
            foreach (string part in p.Substring(3).Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                yield return (part, current);
            }
        }
    }

    Label MakeCrumb(string name, string target)
    {
        var label = new Label { Text = name, AutoSize = true, Cursor = Cursors.Hand, Margin = new Padding(0, 1, 0, 0), Padding = new Padding(3, 1, 3, 1), UseMnemonic = false };
        label.MouseEnter += (_, _) => label.BackColor = Theme.Hover;
        label.MouseLeave += (_, _) => label.BackColor = Color.Transparent;
        label.Click += (_, _) => PathRequested?.Invoke(target);
        return label;
    }

    Label MakeSeparator()
    {
        var label = new Label { Text = ">", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 1, 0, 0), Padding = new Padding(0, 1, 0, 1) };
        label.Click += (_, _) => BeginEdit();
        return label;
    }

    // When the path is too long, hide segments after the root, oldest first. The current folder stays visible.
    void FitCrumbs()
    {
        var items = crumbs.Controls.Cast<Control>().ToList();
        foreach (var item in items) item.Visible = true;
        int width = items.Sum(c => c.PreferredSize.Width);
        for (int i = 2; i + 1 < items.Count - 1 && width > crumbs.ClientSize.Width; i += 2)
        {
            width -= items[i].PreferredSize.Width + items[i + 1].PreferredSize.Width;
            items[i].Visible = items[i + 1].Visible = false;
        }
    }

    void BeginEdit()
    {
        crumbs.Visible = false;
        box.Visible = true;
        box.Text = path;
        box.Focus();
        box.SelectAll();
    }

    void EndEdit()
    {
        box.Visible = false;
        crumbs.Visible = true;
    }
}
