using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Serilog;

namespace VirtualDesktopTracker.Components;

public sealed class AppPickerDialog : Form
{
    public string? SelectedPath { get; private set; }

    private readonly ListView _list;
    private readonly TextBox _search;
    private readonly Button _browseButton;
    private readonly Button _okButton;
    private readonly Button _cancelButton;
    private readonly Label _statusLabel;
    private readonly ImageList _icons;
    private List<AppEntry> _all = new();
    private List<AppEntry> _visible = new();

    public AppPickerDialog()
    {
        Text = "Pick a program to auto-launch";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 480);
        MinimumSize = new Size(540, 360);
        Font = new Font("Segoe UI", 9F);

        var searchLabel = new Label { Text = "Search:", AutoSize = true, Location = new Point(12, 14) };
        _search = new TextBox { Location = new Point(56, 11), Width = 540, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        _search.TextChanged += (_, _) => ApplyFilter();

        _list = new ListView
        {
            Location = new Point(12, 44),
            Size = new Size(696, 360),
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        _list.Columns.Add("Name", 280);
        _list.Columns.Add("Publisher", 140);
        _list.Columns.Add("Path", 260);
        _icons = new ImageList
        {
            ImageSize = new Size(16, 16),
            ColorDepth = ColorDepth.Depth32Bit
        };
        _list.SmallImageList = _icons;
        _list.DoubleClick += (_, _) => { if (_list.SelectedItems.Count > 0) AcceptSelection(); };
        _list.SelectedIndexChanged += (_, _) => { if (_okButton is not null) _okButton.Enabled = _list.SelectedItems.Count > 0; };

        _browseButton = new Button { Text = "Browse for executable...", Location = new Point(12, 412), Size = new Size(170, 32), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
        _browseButton.Click += OnBrowse;
        _statusLabel = new Label
        {
            Location = new Point(190, 420),
            AutoSize = false,
            Size = new Size(320, 20),
            ForeColor = Color.FromArgb(120, 130, 145),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        _okButton = new Button { Text = "Add", Location = new Point(536, 412), Size = new Size(80, 32), DialogResult = DialogResult.OK, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Enabled = false };
        _cancelButton = new Button { Text = "Cancel", Location = new Point(624, 412), Size = new Size(80, 32), DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        AcceptButton = _okButton;
        CancelButton = _cancelButton;

        _okButton.Click += (_, _) => AcceptSelection();

        Controls.Add(searchLabel);
        Controls.Add(_search);
        Controls.Add(_list);
        Controls.Add(_browseButton);
        Controls.Add(_statusLabel);
        Controls.Add(_okButton);
        Controls.Add(_cancelButton);

        Load += (_, _) => Populate();
    }

    private void AcceptSelection()
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is AppEntry e)
        {
            SelectedPath = e.Path;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private void Populate()
    {
        _statusLabel.Text = "Loading installed programs...";
        Application.DoEvents();
        try
        {
            var entries = new List<AppEntry>();
            entries.AddRange(EnumerateStartMenu());
            entries.AddRange(EnumerateInstalledApps());
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(e.Path)) continue;
                if (!File.Exists(e.Path)) continue;
                if (!seen.Add(e.Path)) continue;
                _all.Add(e);
            }
            _statusLabel.Text = $"{_all.Count} program(s) found";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AppPicker enumeration failed");
            _statusLabel.Text = "Error: " + ex.Message;
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = _search.Text?.Trim() ?? "";
        _visible = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(e => Matches(e, q)).ToList();
        _list.BeginUpdate();
        _list.Items.Clear();
        var idx = 0;
        foreach (var e in _visible)
        {
            var imageKey = e.Path;
            if (!_list.SmallImageList!.Images.ContainsKey(imageKey))
            {
                Icon? icon = null;
                try { icon = Icon.ExtractAssociatedIcon(e.Path); } catch { }
                if (icon is not null)
                {
                    _list.SmallImageList.Images.Add(imageKey, icon);
                    icon.Dispose();
                }
                else
                {
                    imageKey = "_blank";
                }
            }
            var item = new ListViewItem(new[] { e.Name, e.Publisher, e.Path }, imageKey);
            item.Tag = e;
            _list.Items.Add(item);
            idx++;
        }
        _list.EndUpdate();
        _statusLabel.Text = $"{_visible.Count} of {_all.Count} program(s)";
    }

    private static bool Matches(AppEntry e, string q)
    {
        return (e.Name?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            || (e.Publisher?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            || (e.Path?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Pick a program to auto-launch",
            Filter = "Executables (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            SelectedPath = dlg.FileName;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private static IEnumerable<AppEntry> EnumerateStartMenu()
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
        if (shellType is null) yield break;
        object? shell;
        try { shell = Activator.CreateInstance(shellType); } catch { yield break; }
        if (shell is null) yield break;
        try
        {
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            }.Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }
                foreach (var f in files)
                {
                    AppEntry? entry = null;
                    try
                    {
                        var shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { f });
                        if (shortcut is null) continue;
                        try
                        {
                            var targetPath = (string?)shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
                            if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath)) continue;
                            var name = Path.GetFileNameWithoutExtension(f);
                            entry = new AppEntry { Name = name, Publisher = "Start Menu", Path = targetPath };
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(shortcut);
                        }
                    }
                    catch
                    {
                    }
                    if (entry is not null) yield return entry;
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    private static IEnumerable<AppEntry> EnumerateInstalledApps()
    {
        var collected = new List<AppEntry>();
        try { CollectInstalledAppsInto(collected); } catch (Exception ex) { Log.Warning(ex, "Installed apps scan failed"); }
        return collected;
    }

    private static void CollectInstalledAppsInto(List<AppEntry> sink)
    {
        var hives = new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser };
        var paths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };
        foreach (var hive in hives)
        {
            foreach (var path in paths)
            {
                Microsoft.Win32.RegistryKey? key = null;
                try
                {
                    key = hive.OpenSubKey(path);
                    if (key is null) continue;
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        Microsoft.Win32.RegistryKey? appKey = null;
                        try
                        {
                            appKey = key.OpenSubKey(sub);
                            if (appKey is null) continue;
                            var name = appKey.GetValue("DisplayName") as string;
                            if (string.IsNullOrEmpty(name)) continue;
                            var displayIcon = appKey.GetValue("DisplayIcon") as string;
                            var installLocation = appKey.GetValue("InstallLocation") as string;
                            var publisher = appKey.GetValue("Publisher") as string ?? "";
                            var exe = ResolveExe(displayIcon) ?? ResolveExe(installLocation);
                            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) continue;
                            sink.Add(new AppEntry
                            {
                                Name = name!,
                                Publisher = publisher,
                                Path = exe
                            });
                        }
                        finally
                        {
                            appKey?.Dispose();
                        }
                    }
                }
                finally
                {
                    key?.Dispose();
                }
            }
        }
    }

    private static string? ResolveExe(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var s = raw.Trim('"');
        var comma = s.IndexOf(',');
        if (comma > 0) s = s.Substring(0, comma);
        if (File.Exists(s) && s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return s;
        if (Directory.Exists(s))
        {
            try
            {
                var guess = Directory.EnumerateFiles(s, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (guess is not null) return guess;
            }
            catch
            {
            }
        }
        return null;
    }

    private sealed class AppEntry
    {
        public string Name { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string Path { get; set; } = "";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icons?.Dispose();
        }
        base.Dispose(disposing);
    }
}
