using System;
using System.Reflection;
using Serilog;
using WindowsDesktop;

namespace VirtualDesktopTracker.Components;

public static class VirtualDesktopRenamer
{
    private static readonly object _initLock = new();
    private static MethodInfo? _setDesktopName;
    private static object? _managerInstance;
    private static bool _initFailed;

    public static bool TrySetName(object desktopComObject, string newName)
    {
        if (desktopComObject is null) return false;
        if (string.IsNullOrWhiteSpace(newName)) return false;
        try
        {
            if (!EnsureManager()) return false;
            _setDesktopName!.Invoke(_managerInstance, new object?[] { desktopComObject, newName });
            return true;
        }
        catch (TargetInvocationException tie)
        {
            Log.Warning(tie.InnerException ?? tie, "SetDesktopName failed for name '{Name}'", newName);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SetDesktopName failed for name '{Name}'", newName);
            return false;
        }
    }

    private static bool EnsureManager()
    {
        if (_setDesktopName is not null) return true;
        if (_initFailed) return false;
        lock (_initLock)
        {
            if (_setDesktopName is not null) return true;
            if (_initFailed) return false;
            try
            {
                var vdType = typeof(VirtualDesktop);
                var providerField = vdType.GetField("_provider", BindingFlags.Static | BindingFlags.NonPublic);
                if (providerField is null) { _initFailed = true; return false; }
                var provider = providerField.GetValue(null);
                if (provider is null) { _initFailed = true; return false; }

                var mgrProp = provider.GetType().GetProperty(
                    "VirtualDesktopManagerInternal",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (mgrProp is null) { _initFailed = true; return false; }
                var manager = mgrProp.GetValue(provider);
                if (manager is null) { _initFailed = true; return false; }

                var setName = manager.GetType().GetMethod(
                    "SetDesktopName",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (setName is null) { _initFailed = true; return false; }

                _managerInstance = manager;
                _setDesktopName = setName;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to bind Slions VirtualDesktopManagerInternal.SetDesktopName");
                _initFailed = true;
                return false;
            }
        }
    }
}
