using System.Runtime.InteropServices;
using System.Text;
using BETFC.Engine;

namespace BETFC.UI;

/// <summary>
/// The log is the tool's trust surface, so it has to stay readable under load.
///
/// Three problems with appending straight to a TextBox: every AppendText plus
/// ScrollToCaret is a synchronous repaint (a clean that reboot-schedules
/// thousands of locked files will stutter badly); the caret jump fights a tech
/// who scrolled up to read something; and the control's own buffer is the only
/// copy, so trimming it for speed would lose log history.
///
/// So: writes are queued and flushed on a timer, auto-scroll engages only when
/// the view is already at the bottom, the visible buffer is trimmed while the
/// full transcript is retained separately for Save/Copy.
/// </summary>
public sealed class LogPane : IDisposable
{
    private const int MaxVisibleLines = 4000;
    private const int TrimTo          = 3000;
    private const int FlushMs         = 120;

    private readonly RichTextBox _box;
    private readonly System.Windows.Forms.Timer _flush;
    private readonly Lock _gate = new();
    private readonly List<string> _queue = new();
    private readonly StringBuilder _transcript = new();

    public Control Control => _box;

    /// <summary>Full transcript including anything trimmed from the view.</summary>
    public string Transcript { get { lock (_gate) return _transcript.ToString(); } }

    public LogPane()
    {
        _box = new RichTextBox
        {
            Dock        = DockStyle.Fill,
            ReadOnly    = true,
            BorderStyle = BorderStyle.FixedSingle,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            WordWrap    = false,
            DetectUrls  = false,
            Font        = new Font("Cascadia Mono", 8.75f),
            BackColor   = Theme.Current.LogBack,
            ForeColor   = Theme.Current.LogText,
            Tag         = Theme.SkipTag,   // manages its own colours
        };

        _box.ContextMenuStrip = BuildContextMenu();

        _flush = new System.Windows.Forms.Timer { Interval = FlushMs };
        _flush.Tick += (_, _) => Flush();
        _flush.Start();
    }

    /// <summary>Queue a line. Safe to call from any thread — no marshalling cost
    /// per line, the UI timer picks the batch up.</summary>
    public void Append(string line)
    {
        lock (_gate)
        {
            _queue.Add(line);
            _transcript.Append(line).Append(Environment.NewLine);
        }
    }

    public void Clear()
    {
        lock (_gate) { _queue.Clear(); _transcript.Clear(); }
        _box.Clear();
    }

    // ─────────────────────────── rendering ───────────────────────────

    private void Flush()
    {
        List<string> batch;
        lock (_gate)
        {
            if (_queue.Count == 0) return;
            batch = new List<string>(_queue);
            _queue.Clear();
        }

        var stickToBottom = IsScrolledToBottom();

        _box.SuspendLayout();
        SendMessage(_box.Handle, WM_SETREDRAW, 0, IntPtr.Zero);
        try
        {
            foreach (var line in batch)
            {
                _box.SelectionStart  = _box.TextLength;
                _box.SelectionLength = 0;
                _box.SelectionColor  = ColorFor(line);
                _box.AppendText(line + Environment.NewLine);
            }
            TrimIfNeeded();
        }
        finally
        {
            SendMessage(_box.Handle, WM_SETREDRAW, 1, IntPtr.Zero);
            _box.ResumeLayout();
            _box.Invalidate();
        }

        if (stickToBottom) ScrollToBottom();
    }

    /// <summary>Severity colouring so a tech can find the one line that matters
    /// in a thousand-line sweep. Deliberately a short, literal rule set.</summary>
    private static Color ColorFor(string line)
    {
        if (line.Contains("FAILED", StringComparison.Ordinal) ||
            line.Contains("error", StringComparison.OrdinalIgnoreCase))
            return Theme.Current.Danger;

        if (line.Contains("SKIP", StringComparison.Ordinal) ||
            line.Contains("NOT rollbackable", StringComparison.Ordinal) ||
            line.StartsWith("warning", StringComparison.OrdinalIgnoreCase))
            return Theme.Current.Warning;

        if (line.StartsWith("──", StringComparison.Ordinal))
            return Theme.Current.Accent;

        if (line.Contains("[dry]", StringComparison.Ordinal))
            return Theme.Current.SubtleText;

        return Theme.Current.LogText;
    }

    private void TrimIfNeeded()
    {
        if (_box.Lines.Length <= MaxVisibleLines) return;

        // Keep the tail; the full text is still in _transcript for Save/Copy.
        var kept = _box.Lines[^TrimTo..];
        _box.Clear();
        _box.SelectionColor = Theme.Current.SubtleText;
        _box.AppendText($"… earlier lines trimmed from view (Save log writes the full transcript) …{Environment.NewLine}");
        foreach (var line in kept)
        {
            _box.SelectionStart  = _box.TextLength;
            _box.SelectionLength = 0;
            _box.SelectionColor  = ColorFor(line);
            _box.AppendText(line + Environment.NewLine);
        }
    }

    // ─────────────────────────── scroll state ───────────────────────────

    private const int WM_SETREDRAW           = 0x000B;
    private const int WM_VSCROLL             = 0x0115;
    private const int SB_BOTTOM              = 7;
    private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
    private const int EM_GETLINECOUNT        = 0x00BA;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

    /// <summary>True when the last line is (about to be) in view. Only then do we
    /// steal the scroll position from the user.</summary>
    private bool IsScrolledToBottom()
    {
        if (!_box.IsHandleCreated) return true;
        try
        {
            int firstVisible = (int)SendMessage(_box.Handle, EM_GETFIRSTVISIBLELINE, 0, IntPtr.Zero);
            int totalLines   = (int)SendMessage(_box.Handle, EM_GETLINECOUNT, 0, IntPtr.Zero);
            int lineHeight   = Math.Max(1, _box.Font.Height);
            int visibleLines = Math.Max(1, _box.ClientSize.Height / lineHeight);
            return firstVisible + visibleLines >= totalLines - 1;
        }
        catch { return true; }
    }

    private void ScrollToBottom()
    {
        try { SendMessage(_box.Handle, WM_VSCROLL, SB_BOTTOM, IntPtr.Zero); } catch { }
    }

    // ─────────────────────────── save / copy ───────────────────────────

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy all", null, (_, _) => CopyAll());
        menu.Items.Add("Save log…", null, (_, _) => SaveWithPrompt());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Clear", null, (_, _) => Clear());
        return menu;
    }

    public void CopyAll()
    {
        var text = Transcript;
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); } catch { /* clipboard owned elsewhere */ }
    }

    /// <summary>Default filename a tech can drop straight into a ticket.</summary>
    public static string SuggestedFileName =>
        $"BE-TFC-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.log";

    /// <summary>
    /// Preferred save location: next to the exe, so a run's log travels with the
    /// stick it was launched from. Falls back to Desktop when the exe lives on
    /// read-only media (write-protected USB, a mounted ISO, a locked share).
    /// </summary>
    public static string DefaultSaveDirectory()
    {
        var exeDir = Path.GetDirectoryName(AppInfo.ExePath);
        if (!string.IsNullOrEmpty(exeDir) && IsWritable(exeDir)) return exeDir;
        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".betfc-write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Save the full transcript. Returns the path written, or null if
    /// the user cancelled. Throws only on a genuine write failure.</summary>
    public string? SaveWithPrompt()
    {
        using var dlg = new SaveFileDialog
        {
            FileName         = SuggestedFileName,
            InitialDirectory = DefaultSaveDirectory(),
            Filter           = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Title            = "Save BE-TFC run log",
            OverwritePrompt  = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return null;

        var header =
            $"{AppInfo.Banner}{Environment.NewLine}" +
            $"Machine : {Environment.MachineName}{Environment.NewLine}" +
            $"Run at  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"Exe     : {AppInfo.ExePath}{Environment.NewLine}" +
            new string('─', 60) + Environment.NewLine;

        File.WriteAllText(dlg.FileName, header + Transcript);
        return dlg.FileName;
    }

    public void Dispose()
    {
        _flush.Stop();
        _flush.Dispose();
    }
}
