using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Serilog;
using WindowsDesktop;
using VirtualDesktopTracker.Components.Interop;
using VirtualDesktopTracker.Utilities;

namespace VirtualDesktopTracker.Components;

public sealed record TaskViewInfo(Guid DesktopId, string DisplayName, bool IsEnabled, IReadOnlyList<string> AutoLaunch);

public sealed class TaskViewManager : IDisposable
{
    private readonly DataStore _store;
    private readonly Func<int> _nextAutoNumber;
    private Func<IntPtr, Guid?>? _windowDesktopResolver;
    private long _lastAutoLaunchEndTick;
    private int _autoLaunchInProgress;
    private readonly object _autoLaunchLock = new();
    private readonly Dictionary<Guid, HashSet<string>> _launchedPrograms = new();
    private bool _disposed;

    public event Action? DesktopsChanged;

    public TaskViewManager(DataStore store, Func<int> nextAutoNumber)
    {
        _store = store;
        _nextAutoNumber = nextAutoNumber;
        try
        {
            VirtualDesktop.Created += OnCreated;
            VirtualDesktop.Destroyed += OnDestroyed;
            VirtualDesktop.CurrentChanged += OnCurrentChanged;
            VirtualDesktop.Renamed += OnRenamed;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to subscribe to VirtualDesktop events");
        }
    }

    public void SetWindowDesktopResolver(Func<IntPtr, Guid?> resolver)
    {
        _windowDesktopResolver = resolver;
    }

    public IReadOnlyList<TaskViewInfo> Enumerate()
    {
        if (!VirtualDesktop.IsSupported)
        {
            Log.Warning("VirtualDesktop API not supported on this Windows build");
            return Array.Empty<TaskViewInfo>();
        }
        var configs = new Dictionary<Guid, TaskViewConfigRow>();
        foreach (var row in _store.GetAllTaskViewConfigs())
        {
            configs[row.DesktopId] = row;
        }
        var list = new List<TaskViewInfo>();
        try
        {
            foreach (var d in VirtualDesktop.GetDesktops())
            {
                if (configs.TryGetValue(d.Id, out var row))
                {
                    list.Add(new TaskViewInfo(d.Id, row.DisplayName, row.IsEnabled, ParseAutoLaunch(row.AutoLaunch)));
                }
                else
                {
                    var name = string.IsNullOrEmpty(d.Name) ? DefaultName() : d.Name;
                    list.Add(new TaskViewInfo(d.Id, name, true, Array.Empty<string>()));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enumerate virtual desktops");
        }
        return list;
    }

    public TaskViewInfo Create(string? displayName)
    {
        if (!VirtualDesktop.IsSupported)
            throw new InvalidOperationException("VirtualDesktop API not supported on this Windows build");
        var newDesktop = VirtualDesktop.Create();
        var name = string.IsNullOrWhiteSpace(displayName) ? DefaultName() : displayName.Trim();
        TrySetWindowsName(newDesktop, name);
        _store.EnsureTaskViewConfigRow(newDesktop.Id, name);
        DesktopsChanged?.Invoke();
        return new TaskViewInfo(newDesktop.Id, name, true, Array.Empty<string>());
    }

    public bool RenameWindowsDesktop(Guid desktopId, string newName)
    {
        if (!VirtualDesktop.IsSupported) return false;
        var d = TryFindDesktop(desktopId);
        if (d is null) return false;
        return TrySetWindowsName(d, newName);
    }

    private static bool TrySetWindowsName(VirtualDesktop desktop, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        try
        {
            var t = desktop.GetType();
            var sourceField = t.GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic);
            if (sourceField is null) return false;
            var source = sourceField.GetValue(desktop);
            if (source is null) return false;
            if (VirtualDesktopRenamer.TrySetName(source, newName))
            {
                Log.Information("Renamed Windows desktop to {Name}", newName);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set Windows desktop name to {Name}", newName);
            return false;
        }
    }

    public bool Remove(Guid desktopId, bool closeWindows = true)
    {
        if (!VirtualDesktop.IsSupported) return false;
        var d = TryFindDesktop(desktopId);
        if (d is null) return false;
        try
        {
            if (closeWindows)
            {
                var closed = CloseWindowsOnDesktop(desktopId);
                if (closed > 0)
                {
                    Log.Information("CloseWindowsOnDesktop: closed {Count} window(s) on desktop {DesktopId}", closed, desktopId);
                    Thread.Sleep(500);
                }
            }
            d.Remove();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to remove desktop {DesktopId}", desktopId);
            return false;
        }
    }

    public int CloseWindowsOnDesktop(Guid desktopId)
    {
        if (_windowDesktopResolver is null)
        {
            Log.Warning("CloseWindowsOnDesktop: no window->desktop resolver available; nothing closed");
            return 0;
        }
        var candidates = new List<(IntPtr hWnd, string title)>();
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd == IntPtr.Zero) return true;
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            if (NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;
            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == (uint)Environment.ProcessId) return true;
            try
            {
                var wid = _windowDesktopResolver(hWnd);
                if (wid == desktopId)
                {
                    var title = NativeMethods.GetWindowText(hWnd);
                    candidates.Add((hWnd, title));
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Desktop id resolution failed for hwnd {Hwnd}", hWnd);
            }
            return true;
        }, IntPtr.Zero);

        var closed = 0;
        foreach (var (hWnd, title) in candidates)
        {
            try
            {
                if (NativeMethods.PostMessageW(hWnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                {
                    closed++;
                    Log.Information("Sent WM_CLOSE to window {Hwnd} ('{Title}') on desktop {DesktopId}", hWnd, title, desktopId);
                }
                else
                {
                    Log.Warning("PostMessageW(WM_CLOSE) failed for hwnd {Hwnd}", hWnd);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WM_CLOSE threw for hwnd {Hwnd}", hWnd);
            }
        }
        return closed;
    }

    public void SwitchTo(Guid desktopId)
    {
        if (!VirtualDesktop.IsSupported) return;
        var d = TryFindDesktop(desktopId);
        if (d is null)
        {
            Log.Warning("SwitchTo: desktop {DesktopId} not found", desktopId);
            return;
        }
        try
        {
            d.Switch();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to switch to desktop {DesktopId}", desktopId);
        }
    }

    public void ApplyConfig(Guid desktopId, string displayName, bool isEnabled, IReadOnlyList<string> autoLaunch)
    {
        var list = autoLaunch ?? Array.Empty<string>();
        var json = JsonSerializer.Serialize(list);
        _store.UpsertTaskViewConfig(desktopId, displayName ?? "", isEnabled, json);
        var existing = TryFindDesktop(desktopId);
        if (existing is not null)
        {
            var currentWin = existing.Name;
            var desired = displayName ?? "";
            if (!string.IsNullOrWhiteSpace(desired) && !string.Equals(currentWin, desired, StringComparison.Ordinal))
            {
                TrySetWindowsName(existing, desired);
            }
        }
        DesktopsChanged?.Invoke();
        _launchedPrograms.Remove(desktopId);
    }

    public void SetEnabled(Guid desktopId, bool isEnabled)
    {
        var row = _store.GetTaskViewConfig(desktopId);
        var name = row?.DisplayName ?? "";
        var al = row?.AutoLaunch ?? "[]";
        _store.UpsertTaskViewConfig(desktopId, name, isEnabled, al);
        DesktopsChanged?.Invoke();
        _launchedPrograms.Remove(desktopId);
    }

    public void EnumerateAndSeed()
    {
        if (!VirtualDesktop.IsSupported) return;
        foreach (var d in VirtualDesktop.GetDesktops())
        {
            var name = string.IsNullOrEmpty(d.Name) ? DefaultName() : d.Name;
            _store.EnsureTaskViewConfigRow(d.Id, name);
        }
    }

    public void RunStartupAutoLaunch()
    {
        if (!VirtualDesktop.IsSupported) return;
        foreach (var d in VirtualDesktop.GetDesktops())
        {
            var row = _store.GetTaskViewConfig(d.Id);
            if (row is null || !row.IsEnabled) continue;
            var paths = ParseAutoLaunch(row.AutoLaunch);
            if (paths.Count == 0) continue;
            foreach (var p in paths)
            {
                LaunchIfNotRunning(p, d.Name);
            }
        }
    }

    public void LaunchOrMoveForDesktop(Guid targetDesktopId, IReadOnlyList<string> paths)
    {
        if (!VirtualDesktop.IsSupported || paths is null || paths.Count == 0) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<string>();
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (seen.Add(p)) unique.Add(p);
        }
        if (unique.Count == 0) return;
        var target = TryFindDesktop(targetDesktopId);
        if (target is null)
        {
            Log.Warning("LaunchOrMoveForDesktop: desktop {DesktopId} not found", targetDesktopId);
            return;
        }
        var targetName = target.Name;
        foreach (var p in unique)
        {
            LaunchOrMoveOne(p, target, targetName);
        }
    }

    private void LaunchOrMoveOne(string path, VirtualDesktop target, string targetName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var resolved = Environment.ExpandEnvironmentVariables(path.Trim());
            if (string.IsNullOrEmpty(resolved)) return;
            if (!File.Exists(resolved) && !Uri.TryCreate(resolved, UriKind.Absolute, out _))
            {
                Log.Warning("Auto-launch skipped for '{Desktop}': file not found: {Path}", targetName, resolved);
                return;
            }
            var isUrl = Uri.TryCreate(resolved, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            var imageName = isUrl ? "" : Path.GetFileNameWithoutExtension(resolved);
            if (string.IsNullOrEmpty(imageName) && !isUrl) imageName = resolved;

            if (!isUrl && !string.IsNullOrEmpty(imageName) && ShouldSkipLaunch(target.Id, imageName))
            {
                return;
            }

            if (!isUrl)
            {
                bool launched = false;
                Process? started = null;
                try
                {
                    started = Process.Start(new ProcessStartInfo
                    {
                        FileName = resolved,
                        UseShellExecute = true
                    });
                    launched = true;
                }
                catch (Exception ex)
                {
                    Log.Information(
                        "Auto-launch: Process.Start threw for {Path}: {Msg} — will try move-existing",
                        resolved, ex.Message);
                }

                if (launched && started is not null)
                {
                    // Wait briefly — single-instance apps (Teams, VDeskTracker, etc.)
                    // will exit within this window due to their mutex guard.
                    Thread.Sleep(350);
                    try
                    {
                        if (!started.HasExited)
                        {
                            // Multi-instance: new process is alive — we're done.
                            Log.Information(
                                "Auto-launched {Path} (new instance) for desktop {Desktop}",
                                resolved, targetName);
                            return;
                        }
                    }
                    catch
                    {
                        // Process may have exited and been disposed between Sleep and HasExited.
                    }
                    try { started.Dispose(); } catch { }
                }
                else if (launched)
                {
                    // Process.Start returned null (defensive — very rare with ShellExecute).
                    Thread.Sleep(350);
                }

                // Single-instance fallback: new process was rejected by the app's mutex.
                // Move the already-running window to the target desktop instead.
                Log.Information(
                    "Auto-launch: new instance of {Name} rejected (single-instance mutex) — moving existing",
                    imageName);
                TryMoveExistingToDesktop(imageName, target, targetName);
            }
            else
            {
                // URLs have no concept of "single-instance" — just launch.
                Process.Start(new ProcessStartInfo
                {
                    FileName = resolved,
                    UseShellExecute = true
                });
                Log.Information("Auto-launched URL {Path} for desktop {Desktop}", resolved, targetName);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-launch/move failed for {Path} (desktop {Desktop})", path, targetName);
        }
    }

    private void TryMoveExistingToDesktop(string imageName, VirtualDesktop target, string targetName)
    {
        if (string.IsNullOrEmpty(imageName)) return;
        Process[] procs;
        try { procs = Process.GetProcessesByName(imageName); }
        catch (Exception ex)
        {
            Log.Warning(ex, "GetProcessesByName failed for {Name}", imageName);
            return;
        }
        if (procs.Length == 0)
        {
            Log.Information("Auto-switch: no {Name} process found to move", imageName);
            return;
        }
        int totalWindows = 0;
        int moved = 0;
        int failed = 0;
        foreach (var p in procs)
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    totalWindows++;
                    try
                    {
                        if (MoveWindowToDesktop(p.MainWindowHandle, target))
                        {
                            moved++;
                            Log.Information(
                                "Auto-switch: moved {Name} (hwnd {Hwnd}) to '{Desktop}'",
                                imageName, p.MainWindowHandle, targetName);
                        }
                        else
                        {
                            failed++;
                            Log.Warning(
                                "Auto-switch: MoveViewToDesktop returned false for {Name} (hwnd {Hwnd}) — " +
                                "possible Slions version mismatch or desktop state issue",
                                imageName, p.MainWindowHandle);
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Log.Warning(ex,
                            "Auto-switch: MoveViewToDesktop threw for {Name} (hwnd {Hwnd}) — " +
                            "reflection path may be broken",
                            imageName, p.MainWindowHandle);
                    }
                }
            }
            catch
            {
                // Process has exited or is in an invalid state — skip silently.
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
        }
        if (moved > 0)
        {
            Log.Information(
                "Auto-switch: moved {Moved}/{Total} {Name} window(s) to '{Desktop}' (single-instance fallback)",
                moved, totalWindows, imageName, targetName);
        }
        else if (totalWindows > 0 && failed > 0)
        {
            Log.Warning(
                "Auto-switch: {Name} has {Total} window(s) but MoveViewToDesktop failed for all {Failed}. " +
                "The reflection path to 'VirtualDesktopManagerInternal.MoveViewToDesktop' may be broken — " +
                "check Slions VirtualDesktop package version compatibility.",
                imageName, totalWindows, failed);
        }
        else if (totalWindows > 0)
        {
            Log.Information(
                "Auto-switch: {Name} has {Total} window(s) but none were moved " +
                "(possibly already on target desktop, or MoveViewToDesktop returned false silently)",
                imageName, totalWindows);
        }
        else
        {
            Log.Information(
                "Auto-switch: {Name} process(es) found but no visible main window handle — " +
                "the app may be minimized to the tray, a background process, or not yet fully started",
                imageName);
        }
    }

    private bool ShouldSkipLaunch(Guid desktopId, string imageName)
    {
        if (!_launchedPrograms.TryGetValue(desktopId, out var programs))
        {
            _launchedPrograms[desktopId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _launchedPrograms[desktopId].Add(imageName);
            return false;
        }

        if (!programs.Contains(imageName))
        {
            programs.Add(imageName);
            return false;
        }

        if (HasVisibleWindow(imageName))
        {
            Log.Information(
                "Auto-launch: {Name} already launched on this desktop and still running — skipping",
                imageName);
            return true;
        }

        programs.Remove(imageName);
        return false;
    }

    private static bool HasVisibleWindow(string imageName)
    {
        try
        {
            var procs = Process.GetProcessesByName(imageName);
            foreach (var p in procs)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                        return true;
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
        return false;
    }

    private static bool MoveWindowToDesktop(IntPtr hwnd, VirtualDesktop target)
    {
        try
        {
            var targetType = target.GetType();
            var sourceField = targetType.GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic);
            if (sourceField is null) return false;
            var targetSource = sourceField.GetValue(target);
            if (targetSource is null) return false;

            var vdType = typeof(VirtualDesktop);
            var providerField = vdType.GetField("_provider", BindingFlags.Static | BindingFlags.NonPublic);
            if (providerField is null) return false;
            var provider = providerField.GetValue(null);
            if (provider is null) return false;
            var managerProp = provider.GetType().GetProperty(
                "VirtualDesktopManagerInternal",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (managerProp is null) return false;
            var manager = managerProp.GetValue(provider);
            if (manager is null) return false;
            var moveMethod = manager.GetType().GetMethod(
                "MoveViewToDesktop",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (moveMethod is null) return false;

            moveMethod.Invoke(manager, new object[] { hwnd, targetSource });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "MoveViewToDesktop reflection failed for hwnd {Hwnd}", hwnd);
            return false;
        }
    }

    private static void LaunchIfNotRunning(string path, string desktopLabel)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var resolved = Environment.ExpandEnvironmentVariables(path.Trim());
            if (!File.Exists(resolved) && !Uri.TryCreate(resolved, UriKind.Absolute, out _))
            {
                Log.Warning("Auto-launch skipped for '{Desktop}': file not found: {Path}", desktopLabel, resolved);
                return;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = resolved,
                UseShellExecute = true
            });
            Log.Information("Auto-launched {Path} for desktop {Desktop}", resolved, desktopLabel);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-launch failed for {Path} (desktop {Desktop})", path, desktopLabel);
        }
    }

    private string DefaultName() => $"Desktop {_nextAutoNumber()}";

    private VirtualDesktop? TryFindDesktop(Guid id)
    {
        try
        {
            return VirtualDesktop.FromId(id);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<string> ParseAutoLaunch(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void OnCreated(object? sender, VirtualDesktop d)
    {
        var name = string.IsNullOrEmpty(d.Name) ? DefaultName() : d.Name;
        _store.EnsureTaskViewConfigRow(d.Id, name);
        DesktopsChanged?.Invoke();
    }

    private void OnDestroyed(object? sender, VirtualDesktopDestroyEventArgs e)
    {
        DesktopsChanged?.Invoke();
    }

    private void OnCurrentChanged(object? sender, VirtualDesktopChangedEventArgs e)
    {
        Log.Information("OnCurrentChanged fired: newId={NewId} oldId={OldId} newName={NewName}", e.NewDesktop?.Id, e.OldDesktop?.Id, e.NewDesktop?.Name);
        if (e.NewDesktop is not null)
        {
            var desktopId = e.NewDesktop.Id;
            var nowTick = Environment.TickCount64;
            const long debounceMs = 2000;
            bool acquired = false;
            try
            {
                Monitor.Enter(_autoLaunchLock, ref acquired);
                if (_autoLaunchInProgress != 0)
                {
                    Log.Information("OnCurrentChanged: auto-launch already in progress; skipping for {DesktopId}", desktopId);
                    return;
                }
                if ((nowTick - _lastAutoLaunchEndTick) < debounceMs)
                {
                    Log.Information("OnCurrentChanged: auto-launch debounced for {DesktopId} (last ended {Delta}ms ago)", desktopId, nowTick - _lastAutoLaunchEndTick);
                    return;
                }
                _autoLaunchInProgress = 1;
            }
            finally
            {
                if (acquired) Monitor.Exit(_autoLaunchLock);
            }
            try
            {
                var row = _store.GetTaskViewConfig(desktopId);
                if (row is not null && row.IsEnabled)
                {
                    var paths = ParseAutoLaunch(row.AutoLaunch);
                    if (paths.Count > 0)
                    {
                        Log.Information("OnCurrentChanged: launching/moving {Count} program(s) for '{Desktop}'", paths.Count, row.DisplayName);
                        LaunchOrMoveForDesktop(desktopId, paths);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "OnCurrentChanged auto-launch failed");
            }
            finally
            {
                lock (_autoLaunchLock)
                {
                    _autoLaunchInProgress = 0;
                    _lastAutoLaunchEndTick = Environment.TickCount64;
                }
            }
        }
        DesktopsChanged?.Invoke();
    }

    private void OnRenamed(object? sender, VirtualDesktopRenamedEventArgs e)
    {
        if (e.Desktop is null) return;
        var row = _store.GetTaskViewConfig(e.Desktop.Id);
        if (row is null)
        {
            _store.EnsureTaskViewConfigRow(e.Desktop.Id, e.Name ?? "");
        }
        else if (!string.IsNullOrEmpty(e.Name) && !string.Equals(row.DisplayName, e.Name, StringComparison.Ordinal))
        {
            var al = row.AutoLaunch;
            _store.UpsertTaskViewConfig(e.Desktop.Id, e.Name, row.IsEnabled, al);
        }
        DesktopsChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            VirtualDesktop.Created -= OnCreated;
            VirtualDesktop.Destroyed -= OnDestroyed;
            VirtualDesktop.CurrentChanged -= OnCurrentChanged;
            VirtualDesktop.Renamed -= OnRenamed;
        }
        catch
        {
        }
    }
}
