using System;
using System.Threading;
using System.Windows.Forms;
using Serilog;
using VirtualDesktopTracker.Components.Interop;

namespace VirtualDesktopTracker.Components;

public sealed class PowerHandler : NativeWindow
{
    private readonly Actor _actor;
    private readonly Thread _thread;
    private IntPtr _suspendResumeHandle = IntPtr.Zero;
    private bool _disposed;

    public PowerHandler(Actor actor)
    {
        _actor = actor;
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "PowerHandler"
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public void Start()
    {
        _thread.Start();
    }

    public void Stop()
    {
        try
        {
            if (Handle != IntPtr.Zero)
            {
                NativeMethods.PostMessageW(Handle, WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch
        {
        }
    }

    private const int WM_APP_QUIT = 0x8001;

    private void MessageLoop()
    {
        var cp = new CreateParams
        {
            Caption = "VDeskTrackerPowerWindow",
            X = 0,
            Y = 0,
            Width = 0,
            Height = 0,
            Parent = NativeMethods.HWND_MESSAGE
        };
        try
        {
            CreateHandle(cp);

            try
            {
                _suspendResumeHandle = NativeMethods.RegisterSuspendResumeNotification(Handle, Win32.DEVICE_NOTIFY_WINDOW_HANDLE);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "RegisterSuspendResumeNotification failed");
            }
            try
            {
                var away = Win32.GUID_SYSTEM_AWAYMODE;
                if (!NativeMethods.RegisterPowerSettingNotification(Handle, ref away, Win32.NOTIFY_FOR_THIS_SESSION))
                {
                    Log.Warning("RegisterPowerSettingNotification (awaymode) returned false");
                }
                var mon = Win32.GUID_MONITORPOWER_ON;
                if (!NativeMethods.RegisterPowerSettingNotification(Handle, ref mon, Win32.NOTIFY_FOR_THIS_SESSION))
                {
                    Log.Warning("RegisterPowerSettingNotification (monitor) returned false");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "RegisterPowerSettingNotification failed");
            }
            try
            {
                if (!NativeMethods.WTSRegisterSessionNotification(Handle, Win32.NOTIFY_FOR_THIS_SESSION))
                {
                    Log.Warning("WTSRegisterSessionNotification returned false");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WTSRegisterSessionNotification failed");
            }

            while (true)
            {
                if (!NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
                {
                    break;
                }
                if (msg.message == WM_APP_QUIT)
                {
                    break;
                }
                NativeMethods.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PowerHandler thread crashed");
        }
        finally
        {
            try { NativeMethods.WTSUnRegisterSessionNotification(Handle); } catch { }
            try
            {
                if (_suspendResumeHandle != IntPtr.Zero)
                {
                    NativeMethods.UnregisterSuspendResumeNotification(_suspendResumeHandle);
                }
            }
            catch { }
        }
    }

    protected override void WndProc(ref Message m)
    {
        try
        {
            switch (m.Msg)
            {
                case Win32.WM_POWERBROADCAST:
                    HandlePowerBroadcast(m.WParam);
                    break;
                case Win32.WM_WTSSESSION_CHANGE:
                    HandleWts(m.WParam);
                    break;
                case Win32.WM_QUERYENDSESSION:
                    Log.Information("WM_QUERYENDSESSION received");
                    _actor.EnqueueEndSessionRequest();
                    m.Result = new IntPtr(1);
                    return;
                case Win32.WM_ENDSESSION:
                    if (m.WParam != IntPtr.Zero)
                    {
                        _actor.EnqueueEndSessionRequest();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PowerHandler WndProc error");
        }
        base.WndProc(ref m);
    }

    private void HandlePowerBroadcast(IntPtr wParam)
    {
        var code = wParam.ToInt32();
        switch (code)
        {
            case Win32.PBT_APMSUSPEND:
                Log.Information("PBT_APMSUSPEND");
                _actor.EnqueueSuspend();
                break;
            case Win32.PBT_APMRESUMEAUTOMATIC:
            case Win32.PBT_APMRESUMESUSPEND:
            case Win32.PBT_APMRESUMECRITICAL:
                Log.Information("PBT_APMRESUMECRITICAL/SUSPEND/AUTOMATIC");
                _actor.EnqueueResume();
                break;
        }
    }

    private void HandleWts(IntPtr wParam)
    {
        var code = wParam.ToInt32();
        switch (code)
        {
            case Win32.WTS_SESSION_LOCK:
                Log.Information("WTS_SESSION_LOCK");
                _actor.EnqueueSessionLock();
                break;
            case Win32.WTS_SESSION_UNLOCK:
                Log.Information("WTS_SESSION_UNLOCK");
                _actor.EnqueueSessionUnlock();
                break;
        }
    }

    public void Shutdown()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try
        {
            if (Handle != IntPtr.Zero)
            {
                ReleaseHandle();
            }
        }
        catch { }
    }
}
