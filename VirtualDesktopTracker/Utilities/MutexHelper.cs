using System;
using System.Threading;

namespace VirtualDesktopTracker.Utilities;

public sealed class MutexHelper : IDisposable
{
    private readonly Mutex _mutex;
    private bool _acquired;
    private bool _disposed;

    public bool Acquired => _acquired;

    private MutexHelper(Mutex mutex, bool acquired)
    {
        _mutex = mutex;
        _acquired = acquired;
    }

    public static MutexHelper TryAcquire()
    {
        var name = $@"Local\VDeskTracker-{Environment.UserName}";
        bool createdNew;
        var mutex = new Mutex(initiallyOwned: true, name, out createdNew);
        if (createdNew)
        {
            return new MutexHelper(mutex, true);
        }

        try
        {
            mutex.Dispose();
        }
        catch
        {
        }
        return new MutexHelper(new Mutex(false, name), false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_acquired)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
        }
        try
        {
            _mutex.Dispose();
        }
        catch
        {
        }
    }
}
