using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Serilog;

namespace VirtualDesktopTracker.Components;

public sealed class TaskViewWindow : Form
{
    private readonly TaskViewManager _manager;
    private readonly Action<TaskViewInfo> _onConfigChanged;

    private readonly DataGridView _grid;
    private readonly Button _createButton;
    private readonly Button _refreshButton;
    private readonly Button _switchButton;
    private readonly Button _removeButton;
    private readonly Button _editButton;
    private readonly CheckBox _keepOnTopCheck;
    private readonly Label _statusLabel;

    private List<TaskViewInfo> _items = new();

    public TaskViewWindow(TaskViewManager manager, Action<TaskViewInfo> onConfigChanged)
    {
        _manager = manager;
        _onConfigChanged = onConfigChanged;

        Text = "VDesk Tracker - Task Views";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 420);
        MinimumSize = new Size(560, 360);
        Font = new Font("Segoe UI", 9F);

        var top = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };
        _createButton = new Button { Text = "Create New Task View", Size = new Size(170, 28), Location = new Point(8, 8) };
        _createButton.Click += OnCreateClicked;
        _refreshButton = new Button { Text = "Refresh", Size = new Size(80, 28), Location = new Point(186, 8) };
        _refreshButton.Click += (_, _) => Reload();
        _keepOnTopCheck = new CheckBox { Text = "Keep on Top", AutoSize = true, Location = new Point(280, 14) };
        _keepOnTopCheck.CheckedChanged += (_, _) => TopMost = _keepOnTopCheck.Checked;
        top.Controls.Add(_createButton);
        top.Controls.Add(_refreshButton);
        top.Controls.Add(_keepOnTopCheck);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            ReadOnly = false,
            EditMode = DataGridViewEditMode.EditOnEnter,
            BackgroundColor = SystemColors.Window,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        var colName = new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Name", FillWeight = 35 };
        var colEnabled = new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "Enabled", FillWeight = 15 };
        var colPrograms = new DataGridViewTextBoxColumn { Name = "Programs", HeaderText = "Auto-launch programs", ReadOnly = true, FillWeight = 50 };
        _grid.Columns.AddRange(colName, colEnabled, colPrograms);
        _grid.CellValueChanged += OnCellValueChanged;
        _grid.CellDoubleClick += OnCellDoubleClick;
        _grid.SelectionChanged += (_, _) => UpdateButtonState();
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.CurrentCell is DataGridViewCheckBoxCell)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(8) };
        _switchButton = new Button { Text = "Switch to selected", Size = new Size(150, 32), Location = new Point(8, 12) };
        _switchButton.Click += OnSwitchClicked;
        _editButton = new Button { Text = "Edit...", Size = new Size(80, 32), Location = new Point(166, 12) };
        _editButton.Click += OnEditClicked;
        _removeButton = new Button { Text = "Remove", Size = new Size(80, 32), Location = new Point(254, 12) };
        _removeButton.Click += OnRemoveClicked;
        _statusLabel = new Label
        {
            Text = "",
            AutoSize = false,
            Location = new Point(350, 20),
            Size = new Size(290, 20),
            ForeColor = Color.FromArgb(120, 130, 145)
        };
        bottom.Controls.Add(_switchButton);
        bottom.Controls.Add(_editButton);
        bottom.Controls.Add(_removeButton);
        bottom.Controls.Add(_statusLabel);

        Controls.Add(_grid);
        Controls.Add(top);
        Controls.Add(bottom);

        _manager.DesktopsChanged += OnDesktopsChangedFromManager;
        FormClosed += (_, _) => _manager.DesktopsChanged -= OnDesktopsChangedFromManager;

        Reload();
    }

    public void Reload()
    {
        _items = _manager.Enumerate().ToList();
        _grid.Rows.Clear();
        foreach (var t in _items)
        {
            var idx = _grid.Rows.Add(t.DisplayName, t.IsEnabled, FormatPrograms(t.AutoLaunch));
            _grid.Rows[idx].Tag = t;
        }
        UpdateButtonState();
        _statusLabel.Text = $"{_items.Count} task view(s)";
    }

    private void UpdateButtonState()
    {
        var hasSelection = _grid.SelectedRows.Count > 0;
        _switchButton.Enabled = hasSelection;
        _editButton.Enabled = hasSelection;
        _removeButton.Enabled = hasSelection;
    }

    private static string FormatPrograms(IReadOnlyList<string> programs)
    {
        if (programs.Count == 0) return "(none)";
        if (programs.Count == 1) return programs[0];
        return $"{programs.Count} programs: " + string.Join(", ", programs.Take(3)) + (programs.Count > 3 ? "..." : "");
    }

    private void OnCreateClicked(object? sender, EventArgs e)
    {
        using var dlg = new NewTaskViewPrompt();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var info = _manager.Create(dlg.EnteredName);
            Reload();
            _statusLabel.Text = $"Created '{info.DisplayName}'";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Create task view failed");
            MessageBox.Show(this, ex.Message, "Create Task View", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnSwitchClicked(object? sender, EventArgs e)
    {
        var info = SelectedInfo();
        if (info is null) return;
        _manager.SwitchTo(info.DesktopId);
        _statusLabel.Text = $"Switched to '{info.DisplayName}'";
    }

    private void OnEditClicked(object? sender, EventArgs e)
    {
        OpenEditDialog();
    }

    private void OnCellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var enabledCol = _grid.Columns["Enabled"];
        if (enabledCol is not null && e.ColumnIndex == enabledCol.Index) return;
        OpenEditDialog();
    }

    private void OnRemoveClicked(object? sender, EventArgs e)
    {
        var info = SelectedInfo();
        if (info is null) return;
        var dr = MessageBox.Show(this,
            $"Remove task view '{info.DisplayName}' and close all programs on it?\r\n\r\nNote: Windows only allows removing the right-most desktop. This may fail for other desktops.",
            "Remove Task View",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (dr != DialogResult.Yes) return;
        if (_manager.Remove(info.DesktopId, closeWindows: true))
        {
            _statusLabel.Text = $"Removed '{info.DisplayName}'";
        }
        else
        {
            MessageBox.Show(this, "Only the right-most desktop can be removed in Windows.", "Remove Task View", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void OpenEditDialog()
    {
        var info = SelectedInfo();
        if (info is null) return;
        using var dlg = new TaskViewEditDialog(info);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _manager.ApplyConfig(info.DesktopId, dlg.DisplayName, dlg.IsEnabled, dlg.AutoLaunch);
        _onConfigChanged(info with { DisplayName = dlg.DisplayName, IsEnabled = dlg.IsEnabled, AutoLaunch = dlg.AutoLaunch });
        Reload();
        _statusLabel.Text = $"Updated '{dlg.DisplayName}'";
    }

    private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var info = SelectedInfo(e.RowIndex);
        if (info is null) return;
        var enabledCol = _grid.Columns["Enabled"];
        var nameCol = _grid.Columns["Name"];
        if (enabledCol is not null && e.ColumnIndex == enabledCol.Index)
        {
            var enabled = (bool)(_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value ?? false);
            _manager.SetEnabled(info.DesktopId, enabled);
            _onConfigChanged(info with { IsEnabled = enabled });
            _statusLabel.Text = enabled ? $"Enabled '{info.DisplayName}'" : $"Disabled '{info.DisplayName}'";
        }
        else if (nameCol is not null && e.ColumnIndex == nameCol.Index)
        {
            var name = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString() ?? info.DisplayName;
            var progs = info.AutoLaunch;
            _manager.ApplyConfig(info.DesktopId, name, info.IsEnabled, progs);
            _onConfigChanged(info with { DisplayName = name });
        }
    }

    private TaskViewInfo? SelectedInfo() => SelectedInfo(_grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Index : -1);

    private TaskViewInfo? SelectedInfo(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _grid.Rows.Count) return null;
        return _grid.Rows[rowIndex].Tag as TaskViewInfo;
    }

    private void OnDesktopsChangedFromManager()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(Reload));
            return;
        }
        Reload();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _manager.DesktopsChanged -= OnDesktopsChangedFromManager; } catch { }
        }
        base.Dispose(disposing);
    }
}

public sealed class NewTaskViewPrompt : Form
{
    private readonly TextBox _nameBox;
    public string EnteredName => _nameBox.Text;

    public NewTaskViewPrompt()
    {
        Text = "New Task View";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 110);
        Font = new Font("Segoe UI", 9F);

        var lbl = new Label { Text = "Name:", AutoSize = true, Location = new Point(12, 14) };
        _nameBox = new TextBox { Location = new Point(12, 36), Width = 336 };
        var ok = new Button { Text = "Create", DialogResult = DialogResult.OK, Location = new Point(192, 70), Size = new Size(75, 28) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(273, 70), Size = new Size(75, 28) };
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(lbl);
        Controls.Add(_nameBox);
        Controls.Add(ok);
        Controls.Add(cancel);
    }
}

public sealed class TaskViewEditDialog : Form
{
    private readonly TextBox _nameBox;
    private readonly CheckBox _enabledCheck;
    private readonly ListBox _programList;
    private readonly Button _addButton;
    private readonly Button _removeButton;
    private readonly Button _upButton;
    private readonly Button _downButton;

    public string DisplayName => _nameBox.Text.Trim();
    public bool IsEnabled => _enabledCheck.Checked;
    public IReadOnlyList<string> AutoLaunch => _programList.Items.Cast<string>().ToArray();

    public TaskViewEditDialog(TaskViewInfo info)
    {
        Text = $"Edit Task View - {info.DisplayName}";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(540, 360);
        MinimumSize = new Size(420, 320);
        Font = new Font("Segoe UI", 9F);

        var nameLabel = new Label { Text = "Name:", AutoSize = true, Location = new Point(12, 14) };
        _nameBox = new TextBox { Location = new Point(12, 34), Width = 510, Text = info.DisplayName };
        _enabledCheck = new CheckBox { Text = "Enabled (track time on this desktop)", AutoSize = true, Location = new Point(12, 64), Checked = info.IsEnabled };

        var progsLabel = new Label { Text = "Auto-launch programs:", AutoSize = true, Location = new Point(12, 96) };
        _programList = new ListBox { Location = new Point(12, 118), Size = new Size(380, 180) };
        foreach (var p in info.AutoLaunch) _programList.Items.Add(p);

        _addButton = new Button { Text = "Add...", Location = new Point(400, 118), Size = new Size(122, 28) };
        _addButton.Click += OnAdd;
        _removeButton = new Button { Text = "Remove", Location = new Point(400, 152), Size = new Size(122, 28) };
        _removeButton.Click += (_, _) => { if (_programList.SelectedItem is string s) _programList.Items.Remove(s); };
        _upButton = new Button { Text = "Up", Location = new Point(400, 186), Size = new Size(58, 28) };
        _upButton.Click += (_, _) => MoveItem(-1);
        _downButton = new Button { Text = "Down", Location = new Point(464, 186), Size = new Size(58, 28) };
        _downButton.Click += (_, _) => MoveItem(1);

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(370, 320), Size = new Size(75, 28) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(451, 320), Size = new Size(75, 28) };
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(nameLabel);
        Controls.Add(_nameBox);
        Controls.Add(_enabledCheck);
        Controls.Add(progsLabel);
        Controls.Add(_programList);
        Controls.Add(_addButton);
        Controls.Add(_removeButton);
        Controls.Add(_upButton);
        Controls.Add(_downButton);
        Controls.Add(ok);
        Controls.Add(cancel);
    }

    private void OnAdd(object? sender, EventArgs e)
    {
        using var dlg = new AppPickerDialog();
        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
        {
            if (!_programList.Items.Contains(dlg.SelectedPath))
            {
                _programList.Items.Add(dlg.SelectedPath);
            }
        }
    }

    private void MoveItem(int delta)
    {
        var i = _programList.SelectedIndex;
        if (i < 0) return;
        var j = i + delta;
        if (j < 0 || j >= _programList.Items.Count) return;
        var item = _programList.Items[i];
        _programList.Items.RemoveAt(i);
        _programList.Items.Insert(j, item);
        _programList.SelectedIndex = j;
    }
}
