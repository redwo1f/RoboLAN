using System.Text.RegularExpressions;

namespace RobocopyGui;

public static class Dialogs
{
    public static string Prompt(IWin32Window owner, string title, string label, string initial)
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(8), Dock = DockStyle.Fill };
        var box = new TextBox { Text = initial, Width = form.LogicalToDeviceUnits(340) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right, AutoSize = true };
        layout.Controls.Add(new Label { Text = label, AutoSize = true });
        layout.Controls.Add(box);
        layout.Controls.Add(ok);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        Theme.Apply(form);
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }

    public static void ShowText(IWin32Window owner, string title, string text)
    {
        using var form = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
        form.ClientSize = new Size(form.LogicalToDeviceUnits(760), form.LogicalToDeviceUnits(360));
        form.Controls.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9), Text = text
        });
        Theme.Apply(form);
        form.ShowDialog(owner);
    }
}

public class SettingsForm : Form
{
    readonly AppSettings settings;
    readonly CheckBox darkMode = new() { Text = "Dark mode", AutoSize = true };
    readonly NumericUpDown threads = new() { Minimum = 1, Maximum = 128 };
    readonly NumericUpDown retries = new() { Minimum = 0, Maximum = 9999 };
    readonly NumericUpDown wait = new() { Minimum = 0, Maximum = 9999 };
    readonly CheckBox subfolders = new() { Text = "Copy subfolders, including empty ones (/E)", AutoSize = true };
    readonly CheckBox junctions = new() { Text = "Skip junction points (/XJ)", AutoSize = true };
    readonly TextBox extra = new() { Dock = DockStyle.Fill };
    readonly TextBox localPath = new() { Dock = DockStyle.Fill };
    readonly CheckBox shorten = new() { Text = "Shorten destination folder names by removing:", AutoSize = true };
    readonly TextBox pattern = new() { Dock = DockStyle.Fill };
    readonly TextBox manualPcs = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    readonly TextBox preview = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true };

    public SettingsForm(AppSettings appSettings)
    {
        settings = appSettings;
        Text = "Settings";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        ClientSize = new Size(LogicalToDeviceUnits(620), LogicalToDeviceUnits(550));

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddRow(grid, "Appearance", darkMode);
        AddRow(grid, "Threads (/MT)", threads);
        AddRow(grid, "Retries (/R)", retries);
        AddRow(grid, "Wait seconds (/W)", wait);
        AddRow(grid, "", subfolders);
        AddRow(grid, "", junctions);
        AddRow(grid, "Extra switches", extra);
        AddRow(grid, "Default local folder", localPath);
        AddRow(grid, "", shorten);
        AddRow(grid, "Pattern (regex)", pattern);
        AddRow(grid, "Extra PCs (one per line)", manualPcs, 70);
        AddRow(grid, "Command", preview, 80);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "OK", AutoSize = true };
        ok.Click += OnOk;
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        grid.Controls.Add(buttons, 1, grid.RowCount);
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;

        darkMode.Checked = settings.DarkMode;
        threads.Value = Math.Clamp(settings.Threads, 1, 128);
        retries.Value = Math.Clamp(settings.Retries, 0, 9999);
        wait.Value = Math.Clamp(settings.WaitSeconds, 0, 9999);
        subfolders.Checked = settings.CopySubfolders;
        junctions.Checked = settings.ExcludeJunctions;
        extra.Text = settings.ExtraArgs;
        localPath.Text = settings.DefaultLocalPath;
        shorten.Checked = settings.ShortenNames;
        pattern.Text = settings.ShortenPattern;
        manualPcs.Text = string.Join(Environment.NewLine, settings.ManualPCs);

        EventHandler update = (_, _) => UpdatePreview();
        threads.ValueChanged += update;
        retries.ValueChanged += update;
        wait.ValueChanged += update;
        subfolders.CheckedChanged += update;
        junctions.CheckedChanged += update;
        extra.TextChanged += update;
        UpdatePreview();
        Theme.Apply(this);
    }

    void AddRow(TableLayoutPanel grid, string label, Control control, int height = 0)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(height > 0 ? new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(height)) : new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) }, 0, row);
        grid.Controls.Add(control, 1, row);
    }

    void ApplyTo(AppSettings target)
    {
        target.Threads = (int)threads.Value;
        target.Retries = (int)retries.Value;
        target.WaitSeconds = (int)wait.Value;
        target.CopySubfolders = subfolders.Checked;
        target.ExcludeJunctions = junctions.Checked;
        target.ExtraArgs = extra.Text.Trim();
    }

    void UpdatePreview()
    {
        var sample = settings.Clone();
        ApplyTo(sample);
        preview.Text = AppSettings.FormatCommand(sample.BuildArgs("<source>", "<destination>", false));
    }

    void OnOk(object sender, EventArgs e)
    {
        try { _ = new Regex(pattern.Text); }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, "Invalid pattern", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        ApplyTo(settings);
        settings.DarkMode = darkMode.Checked;
        settings.DefaultLocalPath = localPath.Text.Trim();
        settings.ShortenNames = shorten.Checked;
        settings.ShortenPattern = pattern.Text;
        settings.ManualPCs = manualPcs.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        DialogResult = DialogResult.OK;
    }
}
