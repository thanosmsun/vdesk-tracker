namespace VirtualDesktopTracker.Components.Interop;

public static class Win32
{
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;

    public const uint GA_ROOTOWNER = 2;

    public const int WM_QUERYENDSESSION = 0x0011;
    public const int WM_ENDSESSION = 0x0016;
    public const int WM_POWERBROADCAST = 0x0218;
    public const int WM_WTSSESSION_CHANGE = 0x02B1;

    public const int PBT_APMSUSPEND = 0x0004;
    public const int PBT_APMRESUMESUSPEND = 0x0007;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const int PBT_APMRESUMECRITICAL = 0x0006;

    public const int WTS_SESSION_LOCK = 0x7;
    public const int WTS_SESSION_UNLOCK = 0x8;

    public const uint NOTIFY_FOR_THIS_SESSION = 0x00000001;
    public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    public const int STATUS_SUCCESS = 0;

    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    public static readonly Guid GUID_MONITORPOWER_ON = new(0x02731015, 0x4511, 0x4457, 0x9B, 0xA6, 0xC7, 0xE5, 0xC2, 0x3A, 0x48, 0x3C);
    public static readonly Guid GUID_SYSTEM_AWAYMODE = new("98A7F580-01F7-48AA-9C0F-44352C29E5C0");

    public const int CSIDL_LOCAL_APPDATA = 0x001C;
}
