using System.Diagnostics;
using BETFC.Engine;
using BETFC.Models;

namespace BETFC.UI;

/// <summary>
/// Shows exactly which paths a category resolved to and what each one weighs.
///
/// This is a trust feature, not a convenience one: a whitelist is only
/// reassuring if the tech can see what it expanded to on *this* machine before
/// authorising a delete. "Open in Explorer" lets them go look.
/// </summary>
public sealed class CategoryDetailForm : Form
{
    private readonly ListView _list = new();
    private readonly CategoryScanResult? _scan;
    private readonly CleanCategory _category;

    public CategoryDetailForm(CleanCategory category, CategoryScanResult? scan)
    {
        _category = category;
        _scan     = scan;

        Text            = $"{category.Name} — resolved locations";
        MinimumSize     = new Size(720, 380);
        Size            = new Size(880, 480);
        StartPosition   = FormStartPosition.CenterParent;
        ShowInTaskbar   = false;
        MinimizeBox     = false;
        Font            = new Font("Segoe UI", 9.5f);

        BuildLayout();
        Populate();

        Shown += (_, _) => Theme.ApplyTitleBar(this);
        Theme.Apply(this);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new Label
        {
            Dock      = DockStyle.Fill,
            AutoSize  = true,
            MaximumSize = new Size(840, 0),
            Padding   = new Padding(0, 0, 0, 8),
            Text      = _category.Description +
                        (_category.Dangerous ? "\n\nDANGEROUS — this category is destructive." : ""),
            ForeColor = _category.Dangerous ? Theme.Current.Danger : Theme.Current.Text,
        };

        _list.Dock          = DockStyle.Fill;
        _list.View          = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines     = false;
        _list.MultiSelect   = false;
        _list.HideSelection = false;
        _list.Columns.Add("Path", 520);
        _list.Columns.Add("Size", 100, HorizontalAlignment.Right);
        _list.Columns.Add("Files", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Mode", 130);
        _list.DoubleClick += (_, _) => OpenSelectedInExplorer();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
        };

        var close = new Button { Text = "Close", AutoSize = true, Padding = new Padding(14, 6, 14, 6) };
        close.Click += (_, _) => Close();

        var open = new Button { Text = "Open in Explorer", AutoSize = true, Padding = new Padding(14, 6, 14, 6) };
        open.Click += (_, _) => OpenSelectedInExplorer();

        var copy = new Button { Text = "Copy paths", AutoSize = true, Padding = new Padding(14, 6, 14, 6) };
        copy.Click += (_, _) => CopyPaths();

        buttons.Controls.AddRange([close, open, copy]);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        AcceptButton = close;
        CancelButton = close;
    }

    private void Populate()
    {
        if (_scan is null || _scan.Locations.Count == 0)
        {
            var scanner = new Scanner();
            var any = false;
            foreach (var target in _category.Targets)
            foreach (var path in scanner.ResolveTarget(target))
            {
                any = true;
                _list.Items.Add(new ListViewItem([path, "—", "—", DescribeMode(target)]));
            }

            if (!any)
            {
                _list.Items.Add(new ListViewItem(["(nothing on this machine matches this category)",
                                                  "—", "—", ""]));
            }
            else if (_scan is null)
            {
                // Paths resolve but sizes need a scan — say so rather than showing zeros.
                foreach (ListViewItem item in _list.Items) item.SubItems[1].Text = "run Scan";
            }
            return;
        }

        foreach (var loc in _scan.Locations.OrderByDescending(l => l.SizeBytes))
        {
            _list.Items.Add(new ListViewItem([
                loc.Path,
                Format.Bytes(loc.SizeBytes),
                loc.FileCount.ToString("N0"),
                DescribeMode(loc.Target),
            ]));
        }
    }

    private static string DescribeMode(CleanTarget target) => target.Mode switch
    {
        DeleteMode.Contents        => "contents only",
        DeleteMode.DirectoryItself => "folder + contents",
        DeleteMode.FilesMatching   => $"files: {target.FilePattern}",
        _                          => target.Mode.ToString(),
    };

    private string? SelectedPath()
    {
        if (_list.SelectedItems.Count == 0) return null;
        var path = _list.SelectedItems[0].Text;
        return path.StartsWith('(') || path.StartsWith("::", StringComparison.Ordinal) ? null : path;
    }

    private void OpenSelectedInExplorer()
    {
        if (SelectedPath() is not { } path) return;
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            MessageBox.Show(this, $"No longer present:\n\n{path}", "Path not found",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Absolute path, never resolved through PATH — this process is elevated.
        var explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try
        {
            Process.Start(new ProcessStartInfo(explorer, $"\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open Explorer: " + ex.Message, "Open failed",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CopyPaths()
    {
        var paths = _list.Items.Cast<ListViewItem>().Select(i => i.Text);
        try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); } catch { }
    }
}
