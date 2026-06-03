using System;
using System.Runtime.InteropServices;

namespace VirtualDesktopTracker.Components.Interop;

[ComImport]
[Guid("a5cd92ff-29be-454c-8d04-d82879dd3f28")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IVirtualDesktopManager
{
    [PreserveSig]
    int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, [Out] out bool onCurrentDesktop);

    [PreserveSig]
    int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);

    [PreserveSig]
    int GetDesktopIdByName([MarshalAs(UnmanagedType.LPWStr)] string name, out Guid desktopId);

    [PreserveSig]
    int RegisterCallbacks([In] IntPtr callbacks, [Out] out IntPtr cookie);

    [PreserveSig]
    int UnregisterCallbacks([In] IntPtr cookie);

    [PreserveSig]
    int SetDesktopName([In] ref Guid desktopId, [MarshalAs(UnmanagedType.LPWStr)] string name);

    [PreserveSig]
    int GetDesktopName([In] ref Guid desktopId, [MarshalAs(UnmanagedType.LPWStr)] out string name);

    [PreserveSig]
    int GetCurrentDesktopId(out Guid desktopId);

    [PreserveSig]
    int GetAllDesktops([Out] out IntPtr desktops);

    [PreserveSig]
    int GetDesktopsByPoint(IntPtr point, [Out] out IntPtr desktops);

    [PreserveSig]
    int MoveWindowToDesktop(IntPtr hwnd, [In] ref Guid desktopId);
}

public static class VirtualDesktopManager
{
    public static readonly Guid ClsId = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");

    public static IVirtualDesktopManager? TryCreate()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(ClsId, throwOnError: false);
            if (type is null) return null;
            return (IVirtualDesktopManager?)Activator.CreateInstance(type);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Minimal IVirtualDesktopManager interface matching the 24H2 COM vtable.
/// Uses direct return types (bool, Guid, void) instead of PreserveSig int + out params.
/// The full interface (above) has additional methods that don't exist in the 24H2 coclass.
/// </summary>
[ComImport]
[Guid("a5cd92ff-29be-454c-8d04-d82879dd3f28")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager24H2
{
    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);

    Guid GetWindowDesktopId(IntPtr topLevelWindow);

    void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

internal static class VirtualDesktopManager24H2
{
    private static readonly Guid ClsId = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");

    public static IVirtualDesktopManager24H2? TryCreate()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(ClsId, throwOnError: false);
            if (type is null) return null;
            return (IVirtualDesktopManager24H2?)Activator.CreateInstance(type);
        }
        catch
        {
            return null;
        }
    }
}
