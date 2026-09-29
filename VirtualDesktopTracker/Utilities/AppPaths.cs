using System;
using System.IO;

namespace VirtualDesktopTracker.Utilities;

public static class AppPaths
{
    public const string AppFolderName = "VirtualDesktopTracker";
    public const string DbFileName = "VirtualDesktopTracker.db";
    public const string LogsFolderName = "logs";

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    public static string DatabasePath => Path.Combine(DataDirectory, DbFileName);

    public static string LogsDirectory => Path.Combine(DataDirectory, LogsFolderName);

    public static string BackupPath(int slot) => Path.Combine(DataDirectory, $"{DbFileName}.bak-{slot}");
}
