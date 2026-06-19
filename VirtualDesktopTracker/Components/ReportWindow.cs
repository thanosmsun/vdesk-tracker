using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using Serilog;
using VirtualDesktopTracker.Utilities;

namespace VirtualDesktopTracker.Components;

public sealed class ReportWindow : Form
{
    private readonly DataStore _store;
    private readonly Action<Guid, string> _onRenameDesktop;

    private RadioButton _todayRadio = null!;
    private RadioButton _7dRadio = null!;
    private RadioButton _30dRadio = null!;
    private RadioButton _customRadio = null!;
    private DateTimePicker _fromPicker = null!;
    private DateTimePicker _toPicker = null!;
    private Button _generateButton = null!;
    private DataGridView _grid = null!;
    private CheckBox _includeRecoveryCheck = null!;
    private CheckBox _keepOnTopCheck = null!;
    private Button _exportCsvButton = null!;
    private Label _statusLabel = null!;
    private CheckedListBox _desktopFilter = null!;
    private Button _selectAllButton = null!;

    private List<ReportRow> _lastRows = new();

    public ReportWindow(DataStore store, Action<Guid, string> onRenameDesktop)
    {
        _store = store;
        _onRenameDesktop = onRenameDesktop;
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Text = "VDesk Tracker - Report";
        FormBorderStyle = FormBorderStyle.Sizable;
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
        MinimizeBox = true;
        MaximizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 540);
        Font = new Font("Segoe UI", 9F);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 110,
            ColumnCount = 4,
            RowCount = 3,
            Padding = new Padding(8)
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _todayRadio = new RadioButton { Text = "Today", AutoSize = true, Checked = true };
        _7dRadio = new RadioButton { Text = "7 Days", AutoSize = true };
        _30dRadio = new RadioButton { Text = "30 Days", AutoSize = true };
        _customRadio = new RadioButton { Text = "Custom", AutoSize = true };

        top.Controls.Add(_todayRadio, 0, 0);
        top.Controls.Add(_7dRadio, 1, 0);
        top.Controls.Add(_30dRadio, 2, 0);
        top.Controls.Add(_customRadio, 3, 0);

        var fromLabel = new Label { Text = "From:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) };
        _fromPicker = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 120 };
        var toLabel = new Label { Text = "To:", AutoSize = true, Margin = new Padding(12, 8, 4, 0) };
        _toPicker = new DateTimePicker { Format = DateTimePickerFormat.Short, Width = 120 };

        top.Controls.Add(fromLabel, 0, 1);
        top.Controls.Add(_fromPicker, 1, 1);
        top.Controls.Add(toLabel, 2, 1);
        top.Controls.Add(_toPicker, 3, 1);

        _generateButton = new Button { Text = "Generate Report", Width = 140, Height = 28 };
        _generateButton.Click += (_, _) => GenerateReport();
        top.Controls.Add(_generateButton, 0, 2);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.Fixed3D
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Date", FillWeight = 15, Name = "Date" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Task View", FillWeight = 25, Name = "TaskView" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "App", FillWeight = 40, Name = "App" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time", FillWeight = 10, Name = "Time" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "%", FillWeight = 10, Name = "Pct" });
        _grid.CellDoubleClick += OnGridDoubleClick;

        var filterPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 80,
            Padding = new Padding(8, 4, 8, 4),
            AutoScroll = true
        };
        var filterLabel = new Label { Text = "Visible task views:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) };
        _desktopFilter = new CheckedListBox
        {
            CheckOnClick = true,
            IntegralHeight = true,
            Height = 60,
            Width = 360,
            MaximumSize = new Size(700, 120),
            MinimumSize = new Size(200, 60)
        };
        _selectAllButton = new Button { Text = "All", Width = 50, Height = 24, Margin = new Padding(4, 0, 0, 0) };
        _selectAllButton.Click += (_, _) => SelectAllDesktops(true);
        _desktopFilter.ItemCheck += (_, _) =>
        {
            BeginInvoke(new Action(() =>
            {
                var filtered = ApplyDesktopFilter(_lastRows);
                RenderGrid(filtered);
            }));
        };
        filterPanel.Controls.Add(filterLabel);
        filterPanel.Controls.Add(_desktopFilter);
        filterPanel.Controls.Add(_selectAllButton);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            ColumnCount = 4,
            Padding = new Padding(8, 4, 8, 4)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _includeRecoveryCheck = new CheckBox { Text = "Include recovery entries", Checked = true, AutoSize = true };
        _keepOnTopCheck = new CheckBox { Text = "Keep on Top", AutoSize = true };
        _keepOnTopCheck.CheckedChanged += (_, _) => TopMost = _keepOnTopCheck.Checked;
        _exportCsvButton = new Button { Text = "Export CSV...", Width = 100, Height = 28 };
        _exportCsvButton.Click += (_, _) => ExportCsv();
        _statusLabel = new Label { Text = "Ready.", AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill };

        bottom.Controls.Add(_includeRecoveryCheck, 0, 0);
        bottom.Controls.Add(_keepOnTopCheck, 1, 0);
        bottom.Controls.Add(_statusLabel, 2, 0);
        bottom.Controls.Add(_exportCsvButton, 3, 0);

        Controls.Add(_grid);
        Controls.Add(filterPanel);
        Controls.Add(bottom);
        Controls.Add(top);

        _todayRadio.CheckedChanged += (_, _) => UpdatePickerEnabled();
        _7dRadio.CheckedChanged += (_, _) => UpdatePickerEnabled();
        _30dRadio.CheckedChanged += (_, _) => UpdatePickerEnabled();
        _customRadio.CheckedChanged += (_, _) => UpdatePickerEnabled();

        Load += (_, _) =>
        {
            UpdatePickerEnabled();
            SetPresetRange();
            GenerateReport();
        };
    }

    private void UpdatePickerEnabled()
    {
        _fromPicker.Enabled = _customRadio.Checked;
        _toPicker.Enabled = _customRadio.Checked;
        if (!_customRadio.Checked)
        {
            SetPresetRange();
        }
    }

    private void SetPresetRange()
    {
        var today = DateTime.Today;
        if (_todayRadio.Checked)
        {
            _fromPicker.Value = today;
            _toPicker.Value = today;
        }
        else if (_7dRadio.Checked)
        {
            _fromPicker.Value = today.AddDays(-6);
            _toPicker.Value = today;
        }
        else if (_30dRadio.Checked)
        {
            _fromPicker.Value = today.AddDays(-29);
            _toPicker.Value = today;
        }
    }

    private (DateTimeOffset, DateTimeOffset) GetRange()
    {
        var from = _fromPicker.Value.Date;
        var to = _toPicker.Value.Date;
        var start = new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Local)).ToUniversalTime();
        var endExclusive = new DateTimeOffset(DateTime.SpecifyKind(to.AddDays(1), DateTimeKind.Local)).ToUniversalTime();
        return (start, endExclusive);
    }

    private void GenerateReport()
    {
        try
        {
            _statusLabel.Text = "Generating...";
            Application.DoEvents();
            var (start, end) = GetRange();
            var includeRecovery = _includeRecoveryCheck.Checked;
            _lastRows = _store.QueryReport(start, end, includeRecovery);
            PopulateDesktopFilter();
            var filtered = ApplyDesktopFilter(_lastRows);
            RenderGrid(filtered);
            _statusLabel.Text = $"Rows: {_lastRows.Count} | {start.LocalDateTime:yyyy-MM-dd} to {end.AddSeconds(-1).LocalDateTime:yyyy-MM-dd}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GenerateReport failed");
            _statusLabel.Text = "Error: " + ex.Message;
        }
    }

    private void PopulateDesktopFilter()
    {
        var names = _lastRows.Select(r => r.TaskViewName).Distinct().OrderBy(s => s).ToList();
        var previouslyChecked = GetCheckedDesktopNames();
        _desktopFilter.Items.Clear();
        foreach (var n in names)
        {
            var isChecked = previouslyChecked.Count == 0 || previouslyChecked.Contains(n);
            _desktopFilter.Items.Add(n, isChecked);
        }
    }

    private HashSet<string> GetCheckedDesktopNames()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _desktopFilter.CheckedItems)
        {
            if (item is string s) set.Add(s);
        }
        return set;
    }

    private List<ReportRow> ApplyDesktopFilter(List<ReportRow> rows)
    {
        var checkedNames = GetCheckedDesktopNames();
        if (checkedNames.Count == 0) return rows;
        return rows.Where(r => checkedNames.Contains(r.TaskViewName)).ToList();
    }

    private void SelectAllDesktops(bool on)
    {
        for (int i = 0; i < _desktopFilter.Items.Count; i++)
        {
            _desktopFilter.SetItemChecked(i, on);
        }
        GenerateReport();
    }

    private void RenderGrid(List<ReportRow> rows)
    {
        _grid.Rows.Clear();
        if (rows.Count == 0)
        {
            _grid.Visible = false;
            ShowEmptyState(true);
            return;
        }
        _grid.Visible = true;
        ShowEmptyState(false);
        long totalMs = rows.Sum(r => r.TotalMs);
        if (totalMs == 0) totalMs = 1;

        var dateGroups = rows.GroupBy(r => r.Date).OrderBy(g => g.Key);
        foreach (var dg in dateGroups)
        {
            long dateMs = dg.Sum(x => x.TotalMs);
            var datePct = dateMs * 100.0 / totalMs;
            var dateLabel = dg.Key.ToString("yyyy-MM-dd (ddd)", System.Globalization.CultureInfo.InvariantCulture);
            var dateRowIdx = _grid.Rows.Add(dateLabel, $"Day total: {FormatTime(dateMs)} ({datePct.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}%)", "", "", "");
            _grid.Rows[dateRowIdx].DefaultCellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
            _grid.Rows[dateRowIdx].DefaultCellStyle.BackColor = Color.FromArgb(220, 230, 245);

            var taskGroups = dg.GroupBy(r => r.TaskViewName).OrderBy(g => g.Key);
            foreach (var tg in taskGroups)
            {
                long taskMs = tg.Sum(x => x.TotalMs);
                var taskPct = taskMs * 100.0 / totalMs;
                var taskRowIdx = _grid.Rows.Add("", tg.Key, "", FormatTime(taskMs), taskPct.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%");
                _grid.Rows[taskRowIdx].DefaultCellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
                _grid.Rows[taskRowIdx].DefaultCellStyle.BackColor = Color.FromArgb(238, 240, 245);
                _grid.Rows[taskRowIdx].Cells[1].Style.Padding = new Padding(16, 0, 0, 0);

                foreach (var r in tg.OrderByDescending(x => x.TotalMs))
                {
                    var pct = r.TotalMs * 100.0 / totalMs;
                    var idx = _grid.Rows.Add("", "", r.AppName, FormatTime(r.TotalMs), pct.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%");
                    _grid.Rows[idx].Cells[1].Style.Padding = new Padding(32, 0, 0, 0);
                }
            }
        }
        if (rows.Count > 0)
        {
            var totalIdx = _grid.Rows.Add("TOTAL", "", "", FormatTime(totalMs), "100.0%");
            _grid.Rows[totalIdx].DefaultCellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
            _grid.Rows[totalIdx].DefaultCellStyle.BackColor = Color.FromArgb(200, 215, 240);
        }
    }

    private Label _emptyStateLabel = null!;

    private void ShowEmptyState(bool show)
    {
        if (_emptyStateLabel is null)
        {
            _emptyStateLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.FromArgb(110, 120, 135),
                Padding = new Padding(24)
            };
            Controls.Add(_emptyStateLabel);
            _emptyStateLabel.BringToFront();
        }
        if (show)
        {
            var (start, end) = GetRange();
            _emptyStateLabel.Text =
                "No time entries in the selected range.\r\n\r\n" +
                $"Range: {start.LocalDateTime:yyyy-MM-dd} to {end.AddSeconds(-1).LocalDateTime:yyyy-MM-dd}\r\n\r\n" +
                "Tracking writes here as soon as you click \"Start Tracking\" on the main window. " +
                "Try a wider date range, or start tracking and switch between windows.";
            _emptyStateLabel.Visible = true;
        }
        else
        {
            _emptyStateLabel.Visible = false;
        }
    }

    private static string FormatTime(long ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
        }
        if (ts.TotalMinutes >= 1)
        {
            return $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s";
        }
        return $"{(int)ts.TotalSeconds}s";
    }

    private void OnGridDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var taskViewCell = _grid.Rows[e.RowIndex].Cells[1].Value as string;
        if (string.IsNullOrEmpty(taskViewCell)) return;
        var configs = _store.GetAllTaskViewConfigs();
        var match = configs.FirstOrDefault(c => string.Equals(c.DisplayName, taskViewCell, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            MessageBox.Show(this, $"No task view in task_view_config for '{taskViewCell}'.", "Rename", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var prompt = new RenamePrompt(taskViewCell, match.DisplayName);
        if (prompt.ShowDialog(this) == DialogResult.OK)
        {
            _onRenameDesktop(match.DesktopId, prompt.NewName);
            GenerateReport();
        }
    }

    private void ExportCsv()
    {
        if (_lastRows.Count == 0)
        {
            MessageBox.Show(this, "Generate a report first.", "Export CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = $"vdesk-report-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var filteredRows = ApplyDesktopFilter(_lastRows);
        try
        {
            using var sw = new StreamWriter(dlg.FileName, false, new System.Text.UTF8Encoding(true));
            sw.WriteLine("Date,TaskView,App,Time,Percent");
            long totalMs = filteredRows.Sum(r => r.TotalMs);
            if (totalMs == 0) totalMs = 1;
            foreach (var r in filteredRows)
            {
                var pct = r.TotalMs * 100.0 / totalMs;
                var dateStr = r.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                sw.WriteLine(dateStr + "," + Csv(r.TaskViewName) + "," + Csv(r.AppName) + "," + Csv(FormatTime(r.TotalMs)) + "," + pct.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            }
            _statusLabel.Text = "Exported: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Export CSV failed");
            MessageBox.Show(this, ex.Message, "Export CSV", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string Csv(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        return s;
    }
}

public sealed class RenamePrompt : Form
{
    public string NewName { get; private set; } = "";

    public RenamePrompt(string desktopDisplay, string currentName)
    {
        Text = "Rename Desktop (report label)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(360, 130);
        Font = new Font("Segoe UI", 9F);

        var label = new Label
        {
            Text = $"Rename '{desktopDisplay}' (report label):",
            Location = new Point(12, 10),
            AutoSize = true
        };
        var text = new TextBox
        {
            Location = new Point(12, 38),
            Width = 336,
            Text = currentName
        };
        var ok = new Button
        {
            Text = "OK",
            Location = new Point(212, 78),
            Width = 64
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(284, 78),
            Width = 64
        };
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            var n = text.Text.Trim();
            if (string.IsNullOrEmpty(n))
            {
                MessageBox.Show(this, "Name cannot be empty.", "Rename", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            NewName = n;
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(label);
        Controls.Add(text);
        Controls.Add(ok);
        Controls.Add(cancel);

        Load += (_, _) => text.Select();
        Shown += (_, _) => text.Focus();
    }
}
