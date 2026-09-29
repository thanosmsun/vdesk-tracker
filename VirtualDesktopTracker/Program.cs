using System;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Channels;
using System.Windows.Forms;
using Microsoft.Win32;
using Serilog;
using VirtualDesktopTracker.Components;
using VirtualDesktopTracker.Components.Interop;
using VirtualDesktopTracker.Events;
using VirtualDesktopTracker.Utilities;
using WindowsDesktop;

namespace VirtualDesktopTracker;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                Log.Error((Exception)e.ExceptionObject, "Unhandled exception in AppDomain");
            }
            catch
            {
            }
        };

        Logger.Initialize();
        Log.Information("VirtualDesktopTracker starting");
        if (IsElevated())
        {
            Log.Warning("Running as administrator - WinEvent hooks will still work but this is not recommended");
        }

        using var mutex = MutexHelper.TryAcquire();
        if (!mutex.Acquired)
        {
            Log.Information("Another instance is already running, exiting");
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.ThreadException += (_, e) => Log.Error(e.Exception, "UI thread exception");

        DataStore? dataStore = null;
        MainWindow? mainWindow = null;
        Actor? actor = null;
        WinEventHookListener? listener = null;
        PowerHandler? power = null;
        ReportWindow? reportWindow = null;
        TaskViewManager? taskViews = null;
        TaskViewWindow? taskViewWindow = null;
        try
        {
            dataStore = new DataStore(AppPaths.DatabasePath);
            dataStore.Initialize();

            var recovery = new CrashRecovery(dataStore);
            var recoveryResult = recovery.Run();

            var channel = Channel.CreateBounded<TrackingEvent>(new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

            listener = new WinEventHookListener(channel);
            taskViews = new TaskViewManager(dataStore, dataStore.NextAutoDesktopNumber);
            taskViews.EnumerateAndSeed();
            taskViews.SetWindowDesktopResolver(hwnd =>
            {
                try
                {
                    var id = listener.DesktopIdResolver(hwnd);
                    return id;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "DesktopIdResolver threw for hwnd {Hwnd}", hwnd);
                    return null;
                }
            });
            var session = new SessionManager(
                dataStore,
                () => { },
                dataStore.GetDesktopName,
                id => dataStore.IsDesktopEnabled(id));
            actor = new Actor(channel, dataStore, session, listener);
            reportWindow = new ReportWindow(dataStore, (id, name) =>
            {
                dataStore.UpdateTaskViewDisplayName(id, name);
                dataStore.RenameDesktopLabel(id, name);
                taskViews?.RenameWindowsDesktop(id, name);
                session.OnRenameDesktop(id, name);
            });

            taskViewWindow = new TaskViewWindow(taskViews, _ => { });

            mainWindow = new MainWindow(
                onStart: () => actor.TryPost(new StartTrackingEvent()),
                onStop: () => actor.TryPost(new StopTrackingEvent()),
                onShowReport: () => ShowReport(reportWindow!),
                onRename: () => RenameCurrentTaskView(taskViews!, mainWindow!),
                onShowTaskViews: () => ShowTaskViews(taskViewWindow!),
                onExit: () => Application.Exit(),
                isTracking: () => session.IsTracking,
                isCurrentDesktopEnabled: () => dataStore.IsDesktopEnabled(session.CurrentDesktopId),
                snapshot: () => actor.SnapshotTooltip(),
                isLaunchAtStartup: StartupHelper.IsEnabled,
                onStartupToggle: () =>
                {
                    StartupHelper.Toggle();
                    mainWindow!.RefreshUi();
                });

            actor.Start();
            listener.Start();
            power = new PowerHandler(actor);
            power.Start();

            if (recoveryResult is not null && recoveryResult.GapMs > 0)
            {
                var seconds = (int)(recoveryResult.GapMs / 1000);
                Log.Information("Crash recovery: gapMs={GapMs}, seconds={Seconds}", recoveryResult.GapMs, seconds);
            }
            else
            {
                Log.Information("No crash recovery needed");
            }

            Application.ApplicationExit += (_, _) =>
            {
                Log.Information("Application exit");
                Shutdown(actor, listener, power, mainWindow, reportWindow, taskViewWindow, taskViews, dataStore);
            };

            Application.Run(mainWindow);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Fatal startup error");
            try
            {
                MessageBox.Show("VDesk Tracker failed to start:\n" + ex.Message, "VDesk Tracker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
            }
            Shutdown(actor, listener, power, mainWindow, reportWindow, taskViewWindow, taskViews, dataStore);
            return 1;
        }
    }

    private static void Shutdown(Actor? actor, WinEventHookListener? listener, PowerHandler? power, MainWindow? mainWindow, ReportWindow? reportWindow, TaskViewWindow? taskViewWindow, TaskViewManager? taskViews, DataStore? dataStore)
    {
        try { actor?.TryPost(new StopTrackingEvent()); } catch { }
        if (actor is not null)
        {
            try { Thread.Sleep(150); } catch { }
            try { actor.Stop(); } catch { }
        }
        try { listener?.Dispose(); } catch { }
        try { power?.Shutdown(); } catch { }
        try { mainWindow?.Dispose(); } catch { }
        try { dataStore?.Dispose(); } catch { }
        try { reportWindow?.Dispose(); } catch { }
        try { taskViewWindow?.Dispose(); } catch { }
        try { taskViews?.Dispose(); } catch { }
        try { Logger.Shutdown(); } catch { }
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void ShowReport(ReportWindow window)
    {
        if (window.IsDisposed) return;
        if (window.Visible)
        {
            window.BringToFront();
        }
        else
        {
            window.Show();
        }
    }

    private static void ShowTaskViews(TaskViewWindow window)
    {
        if (window.IsDisposed)
        {
            MessageBox.Show("Task View window is no longer available. Please restart the app.", "Task Views", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (window.Visible)
        {
            window.BringToFront();
        }
        else
        {
            window.Show();
        }
    }

    private static void RenameCurrentTaskView(TaskViewManager taskViews, MainWindow owner)
    {
        try
        {
            var current = WindowsDesktop.VirtualDesktop.Current;
            if (current is null)
            {
                MessageBox.Show(owner, "Could not determine the current desktop. Try focusing a window and clicking again.",
                    "Rename Task View", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var info = taskViews.Enumerate().FirstOrDefault(t => t.DesktopId == current.Id);
            if (info is null)
            {
                info = new TaskViewInfo(current.Id, current.Name, true, Array.Empty<string>());
            }
            using var dlg = new TaskViewEditDialog(info);
            if (dlg.ShowDialog(owner) != DialogResult.OK) return;
            taskViews.ApplyConfig(info.DesktopId, dlg.DisplayName, dlg.IsEnabled, dlg.AutoLaunch);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Rename Task View failed");
            MessageBox.Show(owner, ex.Message, "Rename Task View", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal static class StartupHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VDeskTracker";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key is null) return false;
            return key.GetValue(ValueName) is string s && !string.IsNullOrEmpty(s);
        }
        catch
        {
            return false;
        }
    }

    public static void Toggle()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (IsEnabled())
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    key.SetValue(ValueName, "\"" + exe + "\"");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to toggle Launch at Startup");
        }
    }

    public static string? GetStartMenuShortcutPath()
    {
        try
        {
            var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            return Path.Combine(startMenu, "Programs", "VirtualDesktopTracker.lnk");
        }
        catch
        {
            return null;
        }
    }

    public static bool IsStartMenuShortcutPresent()
    {
        var p = GetStartMenuShortcutPath();
        return p is not null && File.Exists(p);
    }

    public static bool CreateStartMenuShortcut()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
            var lnkPath = GetStartMenuShortcutPath();
            if (lnkPath is null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            object? shell = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                if (shell is null) return false;
                var shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                if (shortcut is null) return false;
                try
                {
                    var st = shortcut.GetType();
                    st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exe });
                    st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exe)! });
                    st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "Track time spent per Windows virtual desktop and manage task views" });
                    st.InvokeMember("WindowStyle", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { 1 });
                    st.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exe + ",0" });
                    st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
                    Log.Information("Start Menu shortcut created: {Path}", lnkPath);
                    return true;
                }
                finally
                {
                    if (System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
                }
            }
            finally
            {
                if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create Start Menu shortcut");
            return false;
        }
    }

    public static bool RemoveStartMenuShortcut()
    {
        try
        {
            var lnkPath = GetStartMenuShortcutPath();
            if (lnkPath is null || !File.Exists(lnkPath)) return false;
            File.Delete(lnkPath);
            Log.Information("Start Menu shortcut removed");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to remove Start Menu shortcut");
            return false;
        }
    }
}
