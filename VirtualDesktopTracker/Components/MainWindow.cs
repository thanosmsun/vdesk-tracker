using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Serilog;

namespace VirtualDesktopTracker.Components;

public sealed class MainWindow : Form
{
    private readonly Action _onStart;
    private readonly Action _onStop;
    private readonly Action _onShowReport;
    private readonly Action _onRename;
    private readonly Action _onShowTaskViews;
    private readonly Func<bool> _isTracking;
    private readonly Func<bool> _isCurrentDesktopEnabled;
    private readonly Func<TooltipSnapshot?> _snapshot;
    private readonly Func<bool> _isLaunchAtStartup;
    private readonly Action _onStartupToggle;
    private readonly Action _onExit;

    private readonly Panel _statusStrip;
    private readonly Label _statusDot;
    private readonly Label _statusText;
    private readonly Label _desktopLabel;
    private readonly Label _appLabel;
    private readonly Label _elapsedLabel;
    private readonly Button _startStopButton;
    private readonly Button _reportButton;
    private readonly Button _renameButton;
    private readonly Button _taskViewsButton;
    private readonly CheckBox _startupCheck;
    private readonly Button _exitButton;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private bool _exiting;

    public MainWindow(
        Action onStart,
        Action onStop,
        Action onShowReport,
        Action onRename,
        Action onShowTaskViews,
        Action onExit,
        Func<bool> isTracking,
        Func<bool> isCurrentDesktopEnabled,
        Func<TooltipSnapshot?> snapshot,
        Func<bool> isLaunchAtStartup,
        Action onStartupToggle)
    {
        _onStart = onStart;
        _onStop = onStop;
        _onShowReport = onShowReport;
        _onRename = onRename;
        _onShowTaskViews = onShowTaskViews;
        _onExit = onExit;
        _isTracking = isTracking;
        _isCurrentDesktopEnabled = isCurrentDesktopEnabled;
        _snapshot = snapshot;
        _isLaunchAtStartup = isLaunchAtStartup;
        _onStartupToggle = onStartupToggle;

        Text = "VDesk Tracker";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(540, 360);
        MinimumSize = new Size(420, 320);
        Font = new Font("Segoe UI", 9F);
        Icon = LoadAppIcon() ?? BuildAppIcon();

        _statusStrip = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Color.FromArgb(245, 247, 250),
            Padding = new Padding(12, 0, 12, 0)
        };
        _statusDot = new Label
        {
            AutoSize = false,
            Width = 14,
            Height = 14,
            Location = new Point(12, 15),
            BackColor = Color.Gray
        };
        MakeCircular(_statusDot);
        _statusText = new Label
        {
            AutoSize = true,
            Location = new Point(36, 13),
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Text = "Stopped"
        };
        _statusStrip.Controls.Add(_statusDot);
        _statusStrip.Controls.Add(_statusText);

        var body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 16, 16, 16)
        };

        var currentHeader = new Label
        {
            Text = "Currently active",
            AutoSize = true,
            Location = new Point(0, 0),
            ForeColor = Color.FromArgb(120, 130, 145),
            Font = new Font("Segoe UI", 8.5F)
        };
        _desktopLabel = new Label
        {
            Text = "-",
            AutoSize = true,
            Location = new Point(0, 20),
            Font = new Font("Segoe UI", 14F, FontStyle.Bold)
        };
        _appLabel = new Label
        {
            Text = "",
            AutoSize = true,
            Location = new Point(0, 50),
            Font = new Font("Segoe UI", 10F)
        };
        _elapsedLabel = new Label
        {
            Text = "",
            AutoSize = true,
            Location = new Point(0, 72),
            ForeColor = Color.FromArgb(80, 100, 120),
            Font = new Font("Segoe UI", 9F)
        };

        _startStopButton = new Button
        {
            Text = "Start Tracking",
            Location = new Point(0, 110),
            Size = new Size(160, 36),
            BackColor = Color.FromArgb(40, 170, 70),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _startStopButton.FlatAppearance.BorderSize = 0;
        _startStopButton.Click += (_, _) => OnStartStopClicked();

        _reportButton = new Button
        {
            Text = "Show Report...",
            Location = new Point(170, 110),
            Size = new Size(140, 36)
        };
        _reportButton.Click += (_, _) => _onShowReport();

        _renameButton = new Button
        {
            Text = "Rename Task View...",
            Location = new Point(320, 110),
            Size = new Size(170, 36)
        };
        _renameButton.Click += (_, _) => _onRename();

        _taskViewsButton = new Button
        {
            Text = "Task Views...",
            Location = new Point(0, 152),
            Size = new Size(160, 32)
        };
        _taskViewsButton.Click += (_, _) => _onShowTaskViews();

        _startupCheck = new CheckBox
        {
            Text = "Launch at Startup",
            AutoSize = true,
            Location = new Point(180, 158),
            Checked = _isLaunchAtStartup()
        };
        _startupCheck.CheckedChanged += (_, _) => _onStartupToggle();

        var note = new Label
        {
            Text = "Closing this window stops tracking and exits the app. Pin this window to the taskbar for quick access.",
            Location = new Point(0, 195),
            Size = new Size(490, 60),
            ForeColor = Color.FromArgb(120, 130, 145),
            Font = new Font("Segoe UI", 8.5F)
        };

        _exitButton = new Button
        {
            Text = "Exit",
            Location = new Point(410, 270),
            Size = new Size(80, 32),
            BackColor = Color.FromArgb(220, 80, 80),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _exitButton.FlatAppearance.BorderSize = 0;
        _exitButton.Click += (_, _) => DoExit();

        body.Controls.Add(currentHeader);
        body.Controls.Add(_desktopLabel);
        body.Controls.Add(_appLabel);
        body.Controls.Add(_elapsedLabel);
        body.Controls.Add(_startStopButton);
        body.Controls.Add(_reportButton);
        body.Controls.Add(_renameButton);
        body.Controls.Add(_taskViewsButton);
        body.Controls.Add(_startupCheck);
        body.Controls.Add(note);
        body.Controls.Add(_exitButton);

        Controls.Add(body);
        Controls.Add(_statusStrip);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _refreshTimer.Tick += (_, _) => RefreshUi();
        _refreshTimer.Start();

        FormClosing += OnFormClosing;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting) return;
        e.Cancel = true;
        DoExit();
    }

    private void DoExit()
    {
        if (_exiting) return;
        _exiting = true;
        try { _onExit(); } catch (Exception ex) { Log.Warning(ex, "OnExit threw"); }
    }

    private void OnStartStopClicked()
    {
        if (_isTracking())
        {
            _onStop();
        }
        else
        {
            _onStart();
        }
        RefreshUi();
    }

    public void RefreshUi()
    {
        try
        {
            var tracking = _isTracking();
            _startStopButton.Text = tracking ? "Stop Tracking" : "Start Tracking";
            _startStopButton.BackColor = tracking
                ? Color.FromArgb(220, 80, 80)
                : Color.FromArgb(40, 170, 70);
            _statusDot.BackColor = tracking
                ? Color.FromArgb(40, 170, 70)
                : Color.Gray;
            _statusText.Text = tracking ? "Tracking" : "Stopped";

            var snap = _snapshot();
            var desktopEnabled = _isCurrentDesktopEnabled();
            if (snap is null)
            {
                _desktopLabel.Text = tracking ? "(no foreground yet)" : "-";
                _appLabel.Text = "";
                _elapsedLabel.Text = "";
            }
            else
            {
                _desktopLabel.Text = desktopEnabled ? snap.DesktopName : snap.DesktopName + " (paused)";
                _desktopLabel.ForeColor = desktopEnabled ? Color.Black : Color.FromArgb(180, 80, 80);
                _appLabel.Text = snap.AppName;
                _elapsedLabel.Text = desktopEnabled ? "Elapsed: " + snap.Elapsed : "Tracking paused for this desktop";
            }
            Text = tracking ? $"VDesk Tracker - Tracking ({snap?.DesktopName ?? "-"})" : "VDesk Tracker";
        }
        catch
        {
        }
    }

    private static void MakeCircular(Label label)
    {
        label.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(label.BackColor);
            e.Graphics.FillEllipse(b, 0, 0, label.Width - 1, label.Height - 1);
        };
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            var asm = typeof(MainWindow).Assembly;
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".app.ico", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null) return null;
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            return new Icon(stream);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load embedded app icon");
            return null;
        }
    }

    private static Icon BuildAppIcon()
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(60, 110, 200));
            g.FillEllipse(bg, 2, 2, 28, 28);
            using var pen = new Pen(Color.White, 2);
            g.DrawEllipse(pen, 9, 9, 14, 14);
        }
        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        Interop.NativeMethods.DestroyIcon(hIcon);
        bmp.Dispose();
        return icon;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _refreshTimer?.Stop(); _refreshTimer?.Dispose(); } catch { }
            try { Icon?.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
