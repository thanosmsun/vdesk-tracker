using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace VirtualDesktopTracker.Utilities;

public static class Logger
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        Directory.CreateDirectory(AppPaths.LogsDirectory);

        // RollingInterval.Day appends the date itself, producing app-YYYYMMDD.log
        var logFile = Path.Combine(AppPaths.LogsDirectory, "app-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 5_000_000,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    public static void Shutdown() => Log.CloseAndFlush();
}
