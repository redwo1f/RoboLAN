using System.Runtime.InteropServices;

namespace RoboLAN;

public static class Theme
{
    public static bool Dark = true;

    public static Color Back => Pick(0x1E1F22, 0xF3F3F3);
    public static Color Field => Pick(0x2B2D30, 0xFFFFFF);
    public static Color Text => Pick(0xE3E3E3, 0x1A1A1A);
    public static Color Muted => Pick(0x9AA0A6, 0x6B6B6B);
    public static Color Border => Pick(0x43464B, 0xC8C8C8);
    public static Color Hover => Pick(0x3A3D42, 0xE1E1E1);
    public static Color Header => Pick(0x191919, 0xFFFFFF); // Matches what Windows paints past the last column.
    public static Color Accent => Pick(0x2A63B8, 0x0A64C8);
    public static Color AccentText => Color.White;
    public static Color SelectionIdle => Pick(0x3A3D42, 0xD9D9D9);
    public static Color ButtonBack => Pick(0x33363B, 0xFDFDFD);
    public static Color BarTrack => Pick(0x33363B, 0xE6E6E6);
    public static Color BarFill => Pick(0x3C9A4E, 0x4CC25A);
    public static Color BarText => Pick(0xFFFFFF, 0x000000);

    static Color Pick(int dark, int light) => Color.FromArgb(255, Color.FromArgb(Dark ? dark : light));

    public static void Apply(Control control)
    {
        switch (control)
        {
            case AddressBar bar:
                bar.ApplyTheme();
                return;
            case ThemedListView list:
                list.ApplyTheme();
                return;
            case Meter meter:
                meter.Invalidate();
                return;
            case Form form:
                form.BackColor = Back;
                form.ForeColor = Text;
                ApplyTitleBar(form);
                break;
            case TextBox or NumericUpDown:
                control.BackColor = Field;
                control.ForeColor = Text;
                if (control is TextBox box) box.BorderStyle = BorderStyle.FixedSingle;
                else ((NumericUpDown)control).BorderStyle = BorderStyle.FixedSingle;
                break;
            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.MouseOverBackColor = Hover;
                button.BackColor = ButtonBack;
                button.ForeColor = Text;
                break;
            default:
                control.BackColor = Back;
                control.ForeColor = Text;
                break;
        }
        foreach (Control child in control.Controls) Apply(child);
    }

    static void ApplyTitleBar(Form form)
    {
        if (form.IsHandleCreated) SetTitleBar(form);
        else form.HandleCreated += (_, _) => SetTitleBar(form);
    }

    static void SetTitleBar(Form form)
    {
        int value = Dark ? 1 : 0;
        DwmSetWindowAttribute(form.Handle, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

/// <summary>Progress bar drawn with the theme colors. The native one ignores them.</summary>
public class Meter : Control
{
    int current;
    public int Maximum { get; set; } = 1000;

    public Meter()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    public int Value
    {
        get => current;
        set { if (current != value) { current = value; Invalidate(); } }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var track = new SolidBrush(Theme.BarTrack);
        using var fill = new SolidBrush(Theme.BarFill);
        using var border = new Pen(Theme.Border);
        var area = new Rectangle(0, 0, Width - 1, Height - 1);
        e.Graphics.FillRectangle(track, area);
        e.Graphics.FillRectangle(fill, 0, 0, (int)((long)area.Width * current / Maximum), area.Height);
        e.Graphics.DrawRectangle(border, area);
    }
}

/// <summary>Details-view list drawn entirely with the theme colors, so both themes look the same way.</summary>
public class ThemedListView : ListView
{
    public ThemedListView()
    {
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        OwnerDraw = true;
        DoubleBuffered = true;
        DrawColumnHeader += OnDrawHeader;
        DrawItem += (_, _) => { };
        DrawSubItem += (_, e) =>
        {
            var cell = e.Bounds;
            if (e.ColumnIndex == 0) cell.Width = Columns[0].Width; // Column 0 reports the whole row.
            DrawCell(e, cell);
        };
    }

    public void ApplyTheme()
    {
        BackColor = Theme.Field;
        ForeColor = Theme.Text;
        if (IsHandleCreated) ApplyNativeTheme();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyNativeTheme();
    }

    // Windows can leave the bar showing after the list shrinks to fit its columns.
    public void DropUnneededHorizontalBar()
    {
        if (IsHandleCreated && Columns.Cast<ColumnHeader>().Sum(c => c.Width) <= ClientSize.Width)
            ShowScrollBar(Handle, 0 /*SB_HORZ*/, false);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    // Colors the scrollbars and the part of the header to the right of the last column.
    void ApplyNativeTheme()
    {
        SetWindowTheme(Handle, Theme.Dark ? "DarkMode_Explorer" : null, null);
        IntPtr header = SendMessage(Handle, 0x101F /*LVM_GETHEADER*/, IntPtr.Zero, IntPtr.Zero);
        if (header != IntPtr.Zero) SetWindowTheme(header, Theme.Dark ? "DarkMode_ItemsView" : null, null);
    }

    void OnDrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var back = new SolidBrush(Theme.Header);
        using var line = new Pen(Theme.Border);
        e.Graphics.FillRectangle(back, e.Bounds);
        e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
        e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (e.Header.TextAlign == HorizontalAlignment.Right) flags |= TextFormatFlags.Right;
        TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, Rectangle.Inflate(e.Bounds, -6, 0), Theme.Text, flags);
    }

    protected (Color back, Color text) RowColors(ListViewItem item)
    {
        if (item.BackColor != BackColor) return (item.BackColor, item.ForeColor); // Drop target highlight.
        if (item.Selected) return Focused ? (Theme.Accent, Theme.AccentText) : (Theme.SelectionIdle, Theme.Text);
        return (BackColor, ForeColor);
    }

    protected virtual void DrawCell(DrawListViewSubItemEventArgs e, Rectangle cell)
    {
        var (back, text) = RowColors(e.Item);
        using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, cell);

        var textArea = Rectangle.Inflate(cell, -6, 0);
        if (e.ColumnIndex == 0 && SmallImageList != null && e.Item.ImageIndex >= 0)
        {
            var size = SmallImageList.ImageSize;
            SmallImageList.Draw(e.Graphics, cell.X + 3, cell.Y + (cell.Height - size.Height) / 2, e.Item.ImageIndex);
            textArea = new Rectangle(cell.X + size.Width + 7, cell.Y, Math.Max(0, cell.Width - size.Width - 9), cell.Height);
        }

        var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right) flags |= TextFormatFlags.Right;
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, textArea, text, flags);
    }

    [DllImport("user32.dll")]
    static extern bool ShowScrollBar(IntPtr window, int bar, bool show);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr window, string subAppName, string subIdList);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
