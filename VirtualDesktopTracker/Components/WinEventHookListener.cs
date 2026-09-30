using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using Serilog;
using VirtualDesktopTracker.Components.Interop;
using VirtualDesktopTracker.Events;

namespace VirtualDesktopTracker.Components;

public sealed class WinEventHookListener : IDisposable
{
    private readonly Channel<TrackingEvent> _channel;
    private readonly object _coalesceLock = new();
    private readonly ManualResetEventSlim _callbackDone = new(true);

    private IntPtr _hook;
    private WinEventProc? _proc;
    private IVirtualDesktopManager? _vdm;
    private DesktopResolver? _desktopResolver;
    private volatile bool _shuttingDown;
    private bool _disposed;

    private (Guid DesktopId, string AppPath) _lastPostedEventKey;
    private bool _hasLast;

    public WinEventHookListener(Channel<TrackingEvent> channel)
    {
        _channel = channel;
    }

    public Func<IntPtr, Guid?> DesktopIdResolver
    {
        get
        {
            var resolver = _desktopResolver;
            var vdm = _vdm;

            return hwnd =>
            {
                if (resolver is not null)
                {
                    try
                    {
                        var id = resolver.GetDesktopIdForHwnd(hwnd);
                        if (id != Guid.Empty) return id;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Slions DesktopIdResolver failed for hwnd {Hwnd}, trying VDM fallback", hwnd);
                    }
                }

                if (vdm is not null)
                {
                    try
                    {
                        int hr = vdm.GetWindowDesktopId(hwnd, out Guid id);
                        if (hr == 0 && id != Guid.Empty) return id;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "VDM GetWindowDesktopId failed for hwnd {Hwnd}", hwnd);
                    }
                }

                try
                {
                    var current = WindowsDesktop.VirtualDesktop.Current;
                    if (current is not null) return current.Id;
                }
                catch
                {
                }

                return null;
            };
        }
    }

    public bool Start()
    {
        if (_hook != IntPtr.Zero) return true;
        try
        {
            _vdm = VirtualDesktopManager.TryCreate();
            _desktopResolver = DesktopResolver.TryCreate();
            if (_desktopResolver is null)
            {
                Log.Warning("Slions DesktopResolver unavailable; HWND->desktop resolution will use VDM only");
            }
            _proc = OnWinEvent;
            _hook = NativeMethods.SetWinEventHook(
                Win32.EVENT_SYSTEM_FOREGROUND,
                Win32.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _proc,
                0,
                0,
                Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
            if (_hook == IntPtr.Zero)
            {
                Log.Warning("SetWinEventHook returned NULL");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start WinEventHook");
            return false;
        }
    }

    public void ResetCoalescing()
    {
        lock (_coalesceLock)
        {
            _hasLast = false;
        }
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        _shuttingDown = true;
        try
        {
            _callbackDone.Wait(500);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Wait for hook callback timed out");
        }
        try
        {
            if (!NativeMethods.UnhookWinEvent(_hook))
            {
                Log.Warning("UnhookWinEvent failed");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UnhookWinEvent threw");
        }
        _hook = IntPtr.Zero;
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_shuttingDown) return;
        if (hwnd == IntPtr.Zero) return;
        _callbackDone.Reset();
        try
        {
            if (idObject != 0) return;
            if (_shuttingDown) return;
            var root = NativeMethods.GetAncestor(hwnd, Win32.GA_ROOTOWNER);
            if (root == IntPtr.Zero) root = hwnd;
            var exStyle = NativeMethods.GetWindowLongPtr(root, Win32.GWL_EXSTYLE).ToInt64();
            if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0) return;

            Guid desktopId = ResolveDesktopId(root);

            string appPath = "";
            string appName = "";
            try
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != 0)
                {
                    var hProc = NativeMethods.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (hProc != IntPtr.Zero)
                    {
                        try
                        {
                            var sb = new StringBuilder(1024);
                            uint size = (uint)sb.Capacity;
                            if (NativeMethods.QueryFullProcessImageNameW(hProc, 0, sb, ref size))
                            {
                                appPath = sb.ToString();
                                appName = ExtractAppName(appPath);
                            }
                        }
                        finally
                        {
                            NativeMethods.CloseHandle(hProc);
                        }
                    }
                }
            }
            catch
            {
            }

            if (string.IsNullOrEmpty(appName)) appName = "Unknown";

            lock (_coalesceLock)
            {
                if (_hasLast && _lastPostedEventKey.DesktopId == desktopId && _lastPostedEventKey.AppPath == appPath)
                {
                    return;
                }
                _lastPostedEventKey = (desktopId, appPath);
                _hasLast = true;
            }

            var evt = new ForegroundChangedEvent(desktopId, "", appPath, appName);
            if (!_channel.Writer.TryWrite(evt))
            {
                Log.Warning("Channel write failed (full or closed)");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "WinEvent callback error");
        }
        finally
        {
            _callbackDone.Set();
        }
    }

    private Guid ResolveDesktopId(IntPtr hwnd)
    {
        if (_desktopResolver is not null)
        {
            try
            {
                var id = _desktopResolver.GetDesktopIdForHwnd(hwnd);
                if (id != Guid.Empty) return id;
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x8002802B))
            {
                // TYPE_E_ELEMENTNOTFOUND: expected for transient/closing windows;
                // fall through to the fallback resolvers without spamming the log.
                Log.Debug(ex, "Slions desktop resolver: view not found for hwnd {Hwnd}", hwnd);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Slions desktop resolver failed");
            }
        }
        if (_vdm is not null)
        {
            try
            {
                int hr = _vdm.GetWindowDesktopId(hwnd, out Guid id);
                if (hr == 0 && id != Guid.Empty) return id;
            }
            catch
            {
            }
        }
        try
        {
            var current = WindowsDesktop.VirtualDesktop.Current;
            if (current is not null) return current.Id;
        }
        catch
        {
        }
        return Guid.Empty;
    }

    private static string ExtractAppName(string appPath)
    {
        if (string.IsNullOrEmpty(appPath)) return "";
        var idx = appPath.LastIndexOfAny(new[] { '\\', '/' });
        var name = idx >= 0 ? appPath.Substring(idx + 1) : appPath;
        return name;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try
        {
            if (_vdm is not null) Marshal.ReleaseComObject(_vdm);
        }
        catch
        {
        }
        _vdm = null;
        _desktopResolver?.Dispose();
        _desktopResolver = null;
        _callbackDone.Dispose();
    }
}

internal sealed class DesktopResolver : IDisposable
{
    private readonly object _appViewCollection;
    private readonly MethodInfo _getViewForHwnd;
    private readonly MethodInfo _getVirtualDesktopId;
    private bool _disposed;

    private DesktopResolver(object appViewCollection, MethodInfo getView, MethodInfo getDesktopId)
    {
        _appViewCollection = appViewCollection;
        _getViewForHwnd = getView;
        _getVirtualDesktopId = getDesktopId;
    }

    public static DesktopResolver? TryCreate()
    {
        try
        {
            var vdType = typeof(WindowsDesktop.VirtualDesktop);
            var providerField = vdType.GetField("_provider", BindingFlags.Static | BindingFlags.NonPublic);
            if (providerField is null) return null;
            var provider = providerField.GetValue(null);
            if (provider is null) return null;
            var avProp = provider.GetType().GetProperty(
                "ApplicationViewCollection",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (avProp is null) return null;

            object? av = null;
            try { av = avProp.GetValue(provider); }
            catch (Exception ex) { Log.Information("ApplicationViewCollection first access threw: {Msg} (initialization is lazy; will retry)", ex.Message); }
            if (av is null)
            {
                try { av = avProp.GetValue(provider); }
                catch (Exception ex) { Log.Warning(ex, "ApplicationViewCollection second access still failing"); }
            }
            if (av is null) return null;

            var getView = av.GetType().GetMethod(
                "GetViewForHwnd",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (getView is null) return null;

            var ifaceType = Type.GetType("WindowsDesktop.Interop.Proxy.IApplicationView, VirtualDesktop");
            if (ifaceType is null) return null;
            var getDesktopId = ifaceType.GetMethod(
                "GetVirtualDesktopId",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (getDesktopId is null) return null;

            return new DesktopResolver(av, getView, getDesktopId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create Slions DesktopResolver");
            return null;
        }
    }

    public Guid GetDesktopIdForHwnd(IntPtr hwnd)
    {
        if (_disposed) return Guid.Empty;
        var view = _getViewForHwnd.Invoke(_appViewCollection, new object?[] { hwnd });
        if (view is null) return Guid.Empty;
        try
        {
            var ret = _getVirtualDesktopId.Invoke(view, null);
            if (ret is Guid g) return g;
        }
        finally
        {
            if (Marshal.IsComObject(view)) Marshal.ReleaseComObject(view);
        }
        return Guid.Empty;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Marshal.IsComObject(_appViewCollection))
        {
            try { Marshal.ReleaseComObject(_appViewCollection); }
            catch (Exception ex) { Log.Warning(ex, "Failed to release ApplicationViewCollection RCW"); }
        }
    }
}
