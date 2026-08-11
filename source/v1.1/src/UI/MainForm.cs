using System.Diagnostics;
using BETFC.Engine;
using BETFC.Models;

namespace BETFC.UI;

public sealed class MainForm : Form
{
    private readonly TreeView _tree = new();
    private readonly LogPane _log = new();

    private readonly Button _btnScan     = new();
    private readonly Button _btnClean    = new();
    private readonly Button _btnCancel   = new();
    private readonly Button _btnRollback = new();
    private readonly Button _btnCommit   = new();
    private readonly Button _btnSaveLog  = new();

    private readonly Button _btnAll      = new();
    private readonly Button _btnNone     = new();
    private readonly Button _btnDefaults = new();
    private readonly CheckBox _chkHideEmpty = new() { Text = "Hide empty", AutoSize = true };

    private readonly RadioButton _rbDry    = new() { Text = "Dry run" };
    private readonly RadioButton _rbSafe   = new() { Text = "Safe (rollbackable)", Checked = true };
    private readonly RadioButton _rbDirect = new() { Text = "Direct (no undo)" };

    private readonly Label _status = new();
    private readonly Label _totals = new();
    private readonly Label _disk   = new();
    private readonly ProgressBar _progress = new();
    private readonly System.Windows.Forms.Timer _elapsedTimer = new() { Interval = 500 };

    private CleanMode CurrentMode =>
        _rbDry.Checked    ? CleanMode.Dry    :
        _rbDirect.Checked ? CleanMode.Direct :
                            CleanMode.Quarantine;

    private Scanner? _scanner;
    private List<CategoryScanResult>? _scan;
    private CancellationTokenSource? _cts;
    private Stopwatch? _opWatch;
    private string _opLabel = "";
    private bool _busy;

    public MainForm()
    {
        Text = $"BE-TFC {AppInfo.Version} — Temp File Cleaner ({AppInfo.Architecture})";
        MinimumSize = new Size(860, 660);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        BuildLayout();
        Theme.Apply(this);
        PopulateTree(scanResults: null);

        _elapsedTimer.Tick += (_, _) => TickElapsed();

        Shown += (_, _) =>
        {
            Theme.ApplyTitleBar(this);
            Log(AppInfo.Banner + $"  —  {Environment.MachineName}");
            Log($"Running from: {AppInfo.ExePath}");
            foreach (var root in SelfProtection.ProtectedRoots)
                Log($"Self-protected (never cleaned): {root}");
            RefreshPendingTransactions(announce: true);
            PromptForStaleQuarantines();
            PromptForOrphanedQuarantines();
            UpdateDiskLabel();
        };

        FormClosed += (_, _) => { _log.Dispose(); _elapsedTimer.Dispose(); };
    }

    /// <summary>Days after which pending quarantine is considered stale and
    /// the user is offered a purge on launch. Keeps clients from silently
    /// accumulating quarantine data on disk.</summary>
    private const int StaleQuarantineDays = 7;

    private void PromptForStaleQuarantines()
    {
        var pending = QuarantineStore.FindPending();
        var cutoff  = DateTime.UtcNow.AddDays(-StaleQuarantineDays);
        var stale   = pending.Where(p => p.Journal.StartedUtc < cutoff).ToList();
        if (stale.Count == 0) return;

        var bytes = stale.Sum(p => p.Journal.Entries.Sum(e => e.SizeBytes));
        var oldest = stale.Min(p => p.Journal.StartedUtc);
        var ageDays = (int)Math.Floor((DateTime.UtcNow - oldest).TotalDays);

        var choice = MessageBox.Show(
            $"{stale.Count} quarantine transaction(s) are older than {StaleQuarantineDays} days " +
            $"(oldest: {ageDays} days), holding {Format.Bytes(bytes)} of client disk.\n\n" +
            "Commit them now (purge quarantine, free the space)?\n\n" +
            "Yes  → commit stale transactions, keep newer ones.\n" +
            "No   → leave everything; you can Commit/Rollback manually.",
            "Stale quarantine detected",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (choice != DialogResult.Yes) return;

        long freed = 0; int errors = 0;
        foreach (var t in stale)
        {
            Log($"── Auto-commit stale {t.Journal.TxnId} " +
                $"(age {(int)Math.Floor((DateTime.UtcNow - t.Journal.StartedUtc).TotalDays)}d)");
            var (b, e) = QuarantineStore.Commit(t, Log);
            freed += b; errors += e;
        }
        Log($"── Stale purge complete. Freed {Format.Bytes(freed)}, {errors} errors.");
        RefreshPendingTransactions(announce: false);
        UpdateDiskLabel();
    }

    /// <summary>
    /// Surface quarantine directories that have no usable journal. These cannot
    /// be rolled back — the mapping back to original paths is gone — so the only
    /// disposition is to discard them and reclaim the space. Kept strictly
    /// separate from the rollbackable count: never imply recovery is possible.
    /// </summary>
    private void PromptForOrphanedQuarantines()
    {
        var orphans = QuarantineStore.FindOrphans();
        if (orphans.Count == 0) return;

        var bytes = orphans.Sum(o => o.TotalBytes);
        var files = orphans.Sum(o => o.FileCount);

        Log($"WARNING: {orphans.Count} quarantine folder(s) have no usable journal " +
            $"({files:N0} files, {Format.Bytes(bytes)}).");
        foreach (var o in orphans) Log($"   orphaned: {o.TxnDir}");

        var choice = MessageBox.Show(this,
            $"Found {orphans.Count} quarantine folder(s) with no usable journal, holding " +
            $"{Format.Bytes(bytes)} in {files:N0} files.\n\n" +
            "These were left by a crash or by an older build. Without the journal " +
            "there is no record of where the files came from, so they CANNOT be " +
            "restored — discarding them is the only option.\n\n" +
            "The files are all from cleanup categories, so discarding is normally safe.\n\n" +
            "Discard them now and reclaim the space?",
            "Orphaned quarantine detected",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (choice != DialogResult.Yes)
        {
            Log("   left in place — rerun to be prompted again.");
            return;
        }

        long freed = 0; int failed = 0;
        foreach (var o in orphans)
        {
            var (ok, _) = QuarantineStore.DiscardOrphan(o, Log);
            if (ok) freed += o.TotalBytes; else failed++;
        }
        Log($"── Orphan sweep complete. Reclaimed {Format.Bytes(freed)}" +
            (failed > 0 ? $", {failed} could not be removed." : "."));
        UpdateDiskLabel();
    }

    private void RefreshPendingTransactions(bool announce)
    {
        var pending = QuarantineStore.FindPending();
        var enabled = pending.Count > 0 && !_busy;
        _btnRollback.Enabled = enabled;
        _btnCommit.Enabled = enabled;

        if (pending.Count == 0)
        {
            _btnRollback.Text = "Rollback";
            _btnCommit.Text = "Commit (free space)";
            return;
        }

        var files = pending.Sum(p => p.Journal.Entries.Count);
        var bytes = pending.Sum(p => p.Journal.Entries.Sum(e => e.SizeBytes));
        _btnRollback.Text = $"Rollback ({files:N0} files)";
        _btnCommit.Text = $"Commit — free {Format.Bytes(bytes)}";
        if (announce)
            Log($"Pending quarantine found: {pending.Count} transaction(s), " +
                $"{files:N0} files, {Format.Bytes(bytes)} awaiting Commit or Rollback.");
    }

    // ───────────────────────────── layout ─────────────────────────────

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));           // selection row
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));        // tree
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));        // log
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));           // buttons
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));           // status

        // ── selection helpers ──
        var selectRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        SmallButton(_btnAll,      "Select all",   (_, _) => SetAllChecked(true));
        SmallButton(_btnNone,     "Select none",  (_, _) => SetAllChecked(false));
        SmallButton(_btnDefaults, "Defaults",     (_, _) => RestoreDefaults());
        _chkHideEmpty.Margin = new Padding(16, 6, 4, 0);
        _chkHideEmpty.CheckedChanged += (_, _) => PopulateTree(_scan);
        selectRow.Controls.AddRange([_btnAll, _btnNone, _btnDefaults, _chkHideEmpty]);

        // ── tree ──
        _tree.Dock = DockStyle.Fill;
        _tree.CheckBoxes = true;
        _tree.HideSelection = false;
        _tree.BorderStyle = BorderStyle.FixedSingle;
        _tree.ShowNodeToolTips = true;
        _tree.AfterCheck += Tree_AfterCheck;
        _tree.NodeMouseDoubleClick += (_, e) => ShowDetail(e.Node);
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { ShowDetail(_tree.SelectedNode); e.Handled = true; }
        };

        // ── buttons ──
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };

        MainButton(_btnScan,  "Scan  (F5)", async (_, _) => await RunScanAsync());
        MainButton(_btnClean, "Clean selected", async (_, _) => await RunCleanAsync());
        _btnClean.Enabled = false;
        MainButton(_btnCancel, "Cancel  (Esc)", (_, _) => CancelCurrent());
        _btnCancel.Enabled = false;

        var modeBox = new GroupBox
        {
            Text = "Mode",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(6, 2, 6, 2),
            Margin = new Padding(10, 0, 6, 0),
        };
        var modeRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        foreach (var rb in new[] { _rbDry, _rbSafe, _rbDirect })
        {
            rb.AutoSize = true;
            rb.Margin = new Padding(4, 2, 4, 2);
            modeRow.Controls.Add(rb);
        }
        modeBox.Controls.Add(modeRow);

        MainButton(_btnRollback, "Rollback", async (_, _) => await RunRollbackAsync());
        _btnRollback.Enabled = false;
        MainButton(_btnCommit, "Commit (free space)", async (_, _) => await RunCommitAsync());
        _btnCommit.Enabled = false;
        MainButton(_btnSaveLog, "Save log  (Ctrl+S)", (_, _) => SaveLog());

        _totals.AutoSize = true; _totals.Padding = new Padding(16, 10, 0, 0);
        _totals.Text = "";

        buttons.Controls.AddRange([_btnScan, _btnClean, _btnCancel, modeBox,
                                   _btnRollback, _btnCommit, _btnSaveLog, _totals]);

        // ── status ──
        var statusRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _status.AutoSize = true;
        _status.Text = "Ready. Scan to enumerate cleanable locations.";
        _disk.AutoSize = true;
        _disk.Padding = new Padding(0, 0, 12, 0);
        _disk.ForeColor = Theme.Current.SubtleText;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Visible = false;
        _progress.Width = 160;

        statusRow.Controls.Add(_status, 0, 0);
        statusRow.Controls.Add(_disk, 1, 0);
        statusRow.Controls.Add(_progress, 2, 0);

        root.Controls.Add(selectRow, 0, 0);
        root.Controls.Add(_tree, 0, 1);
        root.Controls.Add(_log.Control, 0, 2);
        root.Controls.Add(buttons, 0, 3);
        root.Controls.Add(statusRow, 0, 4);
        Controls.Add(root);
    }

    private static void MainButton(Button b, string text, EventHandler onClick)
    {
        b.Text = text;
        b.AutoSize = true;
        b.Padding = new Padding(14, 6, 14, 6);
        b.Click += onClick;
    }

    private static void SmallButton(Button b, string text, EventHandler onClick)
    {
        b.Text = text;
        b.AutoSize = true;
        b.Padding = new Padding(8, 2, 8, 2);
        b.Margin = new Padding(0, 2, 4, 2);
        b.Click += onClick;
    }

    // ─────────────────────────── keyboard ───────────────────────────

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.F5 when _btnScan.Enabled:
                _ = RunScanAsync();
                return true;
            case Keys.Escape when _btnCancel.Enabled:
                CancelCurrent();
                return true;
            case Keys.Control | Keys.S:
                SaveLog();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ─────────────────────────── tree handling ───────────────────────────

    private bool _suppressCheckEvents;

    /// <summary>Check states survive re-scans and re-sorts; the tree is rebuilt
    /// often and a tech must never lose a selection they just made.</summary>
    private readonly Dictionary<string, bool> _checkState = new(StringComparer.Ordinal);

    private void PopulateTree(List<CategoryScanResult>? scanResults)
    {
        CaptureCheckState();

        _suppressCheckEvents = true;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();

        // After a scan, lead with the biggest win — both groups and categories
        // ordered by size. Before a scan there are no sizes, so keep catalog order.
        var groups = CategoryCatalog.All.GroupBy(c => c.Group).ToList();
        IEnumerable<IGrouping<string, CleanCategory>> orderedGroups = scanResults is null
            ? groups
            : groups.OrderByDescending(g => g.Sum(c => BytesFor(scanResults, c)));

        foreach (var group in orderedGroups)
        {
            IEnumerable<CleanCategory> members = scanResults is null
                ? group
                : group.OrderByDescending(c => BytesFor(scanResults, c));

            var groupNode = new TreeNode(group.Key) { Name = "group:" + group.Key };
            long groupBytes = 0;

            foreach (var cat in members)
            {
                var scan  = scanResults?.FirstOrDefault(r => r.Category.Id == cat.Id);
                var bytes = scan?.TotalBytes ?? 0;
                groupBytes += bytes;

                if (scanResults is not null && bytes == 0 && _chkHideEmpty.Checked)
                    continue;

                var label = scan is null
                    ? cat.Name
                    : $"{cat.Name}  —  {Format.Bytes(bytes)} ({scan.TotalFiles:N0} files)";

                var isEmpty = scanResults is not null && bytes == 0;
                var node = new TreeNode(label)
                {
                    Name = cat.Id,
                    Tag = cat,
                    Checked = _checkState.TryGetValue(cat.Id, out var wasChecked)
                                  ? wasChecked
                                  : cat.DefaultChecked,
                    ToolTipText = cat.Description + "\n\nDouble-click to see resolved paths.",
                    ForeColor = cat.Dangerous ? Theme.Current.Danger
                              : isEmpty       ? Theme.Current.SubtleText
                                              : Theme.Current.Text,
                };
                groupNode.Nodes.Add(node);
            }

            if (groupNode.Nodes.Count == 0) continue;

            if (scanResults is not null)
                groupNode.Text = $"{group.Key}  —  {Format.Bytes(groupBytes)}";
            groupNode.ForeColor = Theme.Current.Text;
            groupNode.Expand();
            groupNode.Checked = groupNode.Nodes.Cast<TreeNode>().All(n => n.Checked);
            _tree.Nodes.Add(groupNode);
        }

        _tree.EndUpdate();
        _suppressCheckEvents = false;
        UpdateTotals();
    }

    private static long BytesFor(List<CategoryScanResult> scan, CleanCategory cat) =>
        scan.FirstOrDefault(r => r.Category.Id == cat.Id)?.TotalBytes ?? 0;

    /// <summary>Fold the visible tree's states back into the persistent map.
    /// Categories hidden by "Hide empty" keep whatever state they already had.</summary>
    private void CaptureCheckState()
    {
        foreach (var node in CategoryNodes())
            if (node.Tag is CleanCategory cat) _checkState[cat.Id] = node.Checked;
    }

    private void Tree_AfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_suppressCheckEvents || e.Node is null) return;
        _suppressCheckEvents = true;
        try
        {
            if (e.Node.Tag is null)
            {
                // Group node → cascade to children. Capture which children we are
                // actually turning ON, because each Dangerous one still needs its
                // own confirmation: ticking a group must never be a silent way to
                // arm Windows.old.
                var newlyEnabled = new List<TreeNode>();
                foreach (TreeNode child in e.Node.Nodes)
                {
                    if (e.Node.Checked && !child.Checked) newlyEnabled.Add(child);
                    child.Checked = e.Node.Checked;
                }

                foreach (var child in newlyEnabled)
                {
                    if (child.Tag is CleanCategory cat && !ConfirmSelection(cat))
                        child.Checked = false;
                }

                SyncGroupState(e.Node);
            }
            else
            {
                if (e.Node.Checked &&
                    e.Node.Tag is CleanCategory cat &&
                    !ConfirmSelection(cat))
                    e.Node.Checked = false;

                if (e.Node.Parent is { } parent) SyncGroupState(parent);
            }
        }
        finally { _suppressCheckEvents = false; }

        CaptureCheckState();
        UpdateTotals();
    }

    private static void SyncGroupState(TreeNode groupNode) =>
        groupNode.Checked = groupNode.Nodes.Count > 0 &&
                            groupNode.Nodes.Cast<TreeNode>().All(n => n.Checked);

    /// <summary>
    /// Gate for arming a category. Dangerous ones get the destructive prompt;
    /// categories carrying a <see cref="CleanCategory.SelectWarning"/> get their
    /// own consent text. Everything else arms silently. Returns true to keep it
    /// checked.
    /// </summary>
    private bool ConfirmSelection(CleanCategory cat)
    {
        if (cat.Dangerous)
            return MessageBox.Show(this,
                $"\"{cat.Name}\" is destructive:\n\n{cat.Description}\n\nEnable it?",
                "Confirm dangerous category",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

        if (cat.SelectWarning is { } warning)
            return MessageBox.Show(this,
                $"{warning}\n\nEnable \"{cat.Name}\"?",
                "Confirm — permanent deletion",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

        return true;
    }

    private void SetAllChecked(bool value)
    {
        _suppressCheckEvents = true;
        try
        {
            foreach (var node in CategoryNodes())
            {
                if (value && node.Tag is CleanCategory cat)
                {
                    // "Select all" is a convenience, not an authorisation.
                    // Dangerous categories are never swept in by it, and one
                    // carrying a warning still has to be consented to.
                    if (cat.Dangerous) continue;
                    if (!node.Checked && !ConfirmSelection(cat)) continue;
                }
                node.Checked = value;
            }
            foreach (TreeNode group in _tree.Nodes) SyncGroupState(group);
        }
        finally { _suppressCheckEvents = false; }

        CaptureCheckState();
        UpdateTotals();
    }

    private void RestoreDefaults()
    {
        _suppressCheckEvents = true;
        try
        {
            foreach (var node in CategoryNodes())
                if (node.Tag is CleanCategory cat) node.Checked = cat.DefaultChecked;
            foreach (TreeNode group in _tree.Nodes) SyncGroupState(group);
        }
        finally { _suppressCheckEvents = false; }

        CaptureCheckState();
        UpdateTotals();
    }

    private void ShowDetail(TreeNode? node)
    {
        if (node?.Tag is not CleanCategory cat) return;
        var scan = _scan?.FirstOrDefault(r => r.Category.Id == cat.Id);
        using var dlg = new CategoryDetailForm(cat, scan);
        dlg.ShowDialog(this);
    }

    private IEnumerable<TreeNode> CategoryNodes() =>
        _tree.Nodes.Cast<TreeNode>().SelectMany(g => g.Nodes.Cast<TreeNode>());

    private void UpdateTotals()
    {
        if (_scan is null) { _totals.Text = ""; return; }
        var selected = SelectedResults();
        var dangerous = selected.Count(r => r.Category.Dangerous);
        _totals.Text = $"Selected: {Format.Bytes(selected.Sum(r => r.TotalBytes))} " +
                       $"in {selected.Sum(r => r.TotalFiles):N0} files" +
                       (dangerous > 0 ? $"  ({dangerous} dangerous)" : "");
        _totals.ForeColor = dangerous > 0 ? Theme.Current.Danger : Theme.Current.Text;
    }

    private List<CategoryScanResult> SelectedResults()
    {
        if (_scan is null) return [];
        CaptureCheckState();
        return _scan.Where(r => _checkState.TryGetValue(r.Category.Id, out var on)
                                    ? on
                                    : r.Category.DefaultChecked)
                    .ToList();
    }

    // ─────────────────────────── scan / clean ───────────────────────────

    private async Task RunScanAsync()
    {
        if (_busy) return;

        _cts = new CancellationTokenSource();
        SetBusy(true, "Scanning…", CategoryCatalog.All.Count);
        try
        {
            _scanner = new Scanner();
            Log($"Profiles found: {string.Join(", ", _scanner.Profiles.Select(p => p.UserName))}");

            var step = 0;
            var progress = new Progress<string>(s =>
            {
                _opLabel = s;
                _progress.Value = Math.Min(_progress.Maximum, ++step);
            });

            _scan = await _scanner.ScanAsync(CategoryCatalog.All, progress, _cts.Token);

            PopulateTree(_scan);
            Log($"Scan complete in {_opWatch?.Elapsed.TotalSeconds:F1}s. " +
                $"Total cleanable: {Format.Bytes(_scan.Sum(r => r.TotalBytes))} " +
                $"in {_scan.Sum(r => r.TotalFiles):N0} files.");
        }
        catch (OperationCanceledException) { Log("Scan cancelled."); }
        catch (Exception ex) { Log("Scan error: " + ex.Message); }
        finally
        {
            SetBusy(false, "Ready.");
            _btnClean.Enabled = _scan is not null;
            RefreshPendingTransactions(announce: false);
            UpdateDiskLabel();
        }
    }

    private async Task RunCleanAsync()
    {
        if (_busy) return;

        var selected = SelectedResults();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Nothing selected.", "Clean",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var mode = CurrentMode;
        var hasDangerous = selected.Any(r => r.Category.Dangerous);
        var modeNote = mode switch
        {
            CleanMode.Dry        => "DRY RUN: nothing will be changed — output logged only.",
            CleanMode.Quarantine => "Safe clean: files move to quarantine and can be rolled back.\n" +
                                    "Disk space is freed when you Commit.",
            _                    => "DIRECT clean: files are deleted immediately. NO rollback.",
        };
        if (hasDangerous && mode != CleanMode.Dry)
            modeNote += "\n\nDangerous categories detected — a VSS snapshot will be taken " +
                        "first as a safety net (restore via Previous Versions).";

        // Restate anything permanent at the last point of no return. In Safe mode
        // these categories are skipped rather than run (Cleaner refuses them),
        // so the warning would be misleading there.
        if (mode == CleanMode.Direct)
        {
            var permanent = selected.Where(r => r.Category.SelectWarning is not null)
                                    .Select(r => r.Category.Name)
                                    .ToList();
            if (permanent.Count > 0)
                modeNote += $"\n\nPermanent, not rollbackable: {string.Join(", ", permanent)}.";
        }

        var icon = mode == CleanMode.Direct ? MessageBoxIcon.Warning :
                   mode == CleanMode.Dry    ? MessageBoxIcon.Information :
                                              MessageBoxIcon.Question;

        var confirm = MessageBox.Show(this,
            $"Clean {Format.Bytes(selected.Sum(r => r.TotalBytes))} across " +
            $"{selected.Count} categories?\n\n{modeNote}\n\n" +
            "Close all browsers and apps first for best results.",
            "Confirm clean", MessageBoxButtons.YesNo, icon);
        if (confirm != DialogResult.Yes) return;

        var freeBefore = DiskSpace.Snapshot();

        _cts = new CancellationTokenSource();
        SetBusy(true, "Cleaning…", selected.Count);
        try
        {
            var cleaner = new Cleaner(Log, mode, vssForDangerous: hasDangerous);
            var step = 0;
            var progress = new Progress<string>(s =>
            {
                _opLabel = s;
                _progress.Value = Math.Min(_progress.Maximum, ++step);
            });

            var report = await cleaner.CleanAsync(selected, progress, _cts.Token);

            var freeAfter = DiskSpace.Snapshot();
            if (DiskSpace.Describe(freeBefore, freeAfter) is { } delta)
                Log("── Disk: " + delta);
            else if (mode == CleanMode.Quarantine)
                Log("── Disk: unchanged — quarantine still holds the bytes. " +
                    "Commit to actually free them.");

            if (report.RebootRecommended)
            {
                var reboot = MessageBox.Show(this,
                    $"{report.FilesScheduledForReboot} locked files are scheduled for " +
                    "deletion at next boot.\n\nReboot now?",
                    "Reboot recommended", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (reboot == DialogResult.Yes) RebootNow();
            }

            // Refresh sizes after clean
            await RunScanAsync();
            RefreshPendingTransactions(announce: false);
        }
        catch (OperationCanceledException)
        {
            Log("Clean cancelled — files already processed stay processed " +
                "(quarantined files are journaled and can still be rolled back).");
        }
        catch (Exception ex) { Log("Clean error: " + ex.Message); }
        finally
        {
            SetBusy(false, "Ready.");
            UpdateDiskLabel();
        }
    }

    private async Task RunRollbackAsync()
    {
        if (_busy) return;

        var pending = QuarantineStore.FindPending();
        if (pending.Count == 0) { RefreshPendingTransactions(false); return; }

        var files = pending.Sum(p => p.Journal.Entries.Count);
        var unrecoverable = pending.Sum(p => p.Journal.Unrecoverable.Count);
        var msg = $"Restore {files:N0} quarantined files to their original locations?";
        if (unrecoverable > 0)
            msg += $"\n\nNote: {unrecoverable} files were reboot-deleted and cannot be restored.";

        if (MessageBox.Show(this, msg, "Confirm rollback", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes) return;

        SetBusy(true, "Rolling back…");
        try
        {
            await Task.Run(() =>
            {
                foreach (var txn in pending)
                {
                    Log($"── Rollback {txn.Journal.TxnId}");
                    QuarantineStore.Rollback(txn, Log);
                }
            });
            await RunScanAsync();
        }
        catch (Exception ex) { Log("Rollback error: " + ex.Message); }
        finally
        {
            SetBusy(false, "Ready.");
            RefreshPendingTransactions(announce: false);
            UpdateDiskLabel();
        }
    }

    private async Task RunCommitAsync()
    {
        if (_busy) return;

        var pending = QuarantineStore.FindPending();
        if (pending.Count == 0) { RefreshPendingTransactions(false); return; }

        var bytes = pending.Sum(p => p.Journal.Entries.Sum(e => e.SizeBytes));
        if (MessageBox.Show(this,
                $"Permanently purge quarantine and free {Format.Bytes(bytes)}?\n\n" +
                "Rollback will no longer be possible.",
                "Confirm commit", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes) return;

        var freeBefore = DiskSpace.Snapshot();
        SetBusy(true, "Committing…");
        try
        {
            await Task.Run(() =>
            {
                foreach (var txn in pending)
                {
                    Log($"── Commit {txn.Journal.TxnId}");
                    QuarantineStore.Commit(txn, Log);
                }
            });

            if (DiskSpace.Describe(freeBefore, DiskSpace.Snapshot()) is { } delta)
                Log("── Disk: " + delta);
        }
        catch (Exception ex) { Log("Commit error: " + ex.Message); }
        finally
        {
            SetBusy(false, "Ready.");
            RefreshPendingTransactions(announce: false);
            UpdateDiskLabel();
        }
    }

    private void CancelCurrent()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        Log("Cancel requested — finishing the current item…");
        _status.Text = "Cancelling…";
        _btnCancel.Enabled = false;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Absolute path — never resolved through PATH, this process is elevated.</summary>
    private void RebootNow()
    {
        var shutdown = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
        try
        {
            Process.Start(new ProcessStartInfo(shutdown, "/r /t 5") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log("Reboot command failed: " + ex.Message);
        }
    }

    // ─────────────────────────── status plumbing ───────────────────────────

    private void SetBusy(bool busy, string status, int steps = 0)
    {
        _busy = busy;

        _btnScan.Enabled     = !busy;
        _btnClean.Enabled    = !busy && _scan is not null;
        _btnCancel.Enabled   = busy;
        _btnAll.Enabled      = !busy;
        _btnNone.Enabled     = !busy;
        _btnDefaults.Enabled = !busy;
        _tree.Enabled        = !busy;
        if (busy) { _btnRollback.Enabled = false; _btnCommit.Enabled = false; }

        if (busy)
        {
            _opLabel = status;
            _opWatch = Stopwatch.StartNew();
            _progress.Value = 0;
            _progress.Maximum = Math.Max(1, steps);
            _progress.Style = steps > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee;
            _progress.Visible = true;
            _elapsedTimer.Start();
        }
        else
        {
            _elapsedTimer.Stop();
            _opWatch?.Stop();
            _opWatch = null;
            _progress.Visible = false;
        }

        _status.Text = status;
    }

    private void TickElapsed()
    {
        if (_opWatch is null) return;
        var cancelling = _cts?.IsCancellationRequested == true ? " (cancelling)" : "";
        _status.Text = $"{_opLabel}{cancelling}   {_opWatch.Elapsed.TotalSeconds:F0}s";
    }

    private void UpdateDiskLabel()
    {
        var free = DiskSpace.SystemVolumeFree();
        var root = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "";
        _disk.Text = free > 0 ? $"{root} free: {Format.Bytes(free)}" : "";
    }

    private void SaveLog()
    {
        try
        {
            if (_log.SaveWithPrompt() is { } path)
                Log($"Log saved: {path}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the log:\n\n" + ex.Message,
                            "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Log(string line) => _log.Append(line);
}
