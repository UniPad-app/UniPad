using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace UniPad.Core.Input;

/// <summary>
/// A poll-thread-owned, reusable high-resolution Windows timer. Unlike a busy wait, it
/// releases the CPU between reports. No global timer-resolution or power-plan changes.
/// </summary>
internal sealed class PollDeadlineWaiter : IDisposable
{
    private SafeWaitHandle? _timer;

    public PollDeadlineWaiter()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Windows 10 1803+: auto-reset, unnamed, high-resolution timer. Do not silently
        // substitute a low-resolution timer: that can turn a 1000 Hz request into ~64 Hz.
        var timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x00100002);
        if (timer.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            timer.Dispose();
            Log.Warning("High-resolution poll timer unavailable ({Error}); using legacy precise wait", error);
            return;
        }

        _timer = timer;
    }

    internal bool UsesHighResolutionTimer => _timer is not null;

    public void WaitUntil(Stopwatch stopwatch, long targetTicks, long frequency)
    {
        while (true)
        {
            var remaining = targetTicks - stopwatch.ElapsedTicks;
            if (remaining <= 0) return;

            if (_timer is not null)
            {
                // Negative means relative time, in 100 ns units. Round up so the timer
                // cannot intentionally expire before the requested deadline.
                var due = -Math.Max(1L, (long)Math.Ceiling(remaining * (10_000_000.0 / frequency)));
                if (SetWaitableTimerEx(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0)
                    && WaitForSingleObject(_timer, 100) == 0)
                {
                    // Recheck the monotonic clock if the OS woke us early.
                    continue;
                }

                var error = Marshal.GetLastWin32Error();
                _timer.Dispose();
                _timer = null;
                // Log once, not at polling frequency. Preserve the original timing policy
                // on old/unsupported systems or unexpected native timer failures.
                Log.Warning("High-resolution poll timer failed ({Error}); using legacy precise wait", error);
                continue;
            }

            // Compatibility path, deliberately identical to the original waiter. This is
            // also used on non-Windows hosts, where the application has no ViGEm output.
            if (remaining > (frequency / 1000) * 2) Thread.Sleep(1);
            else Thread.SpinWait(40);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(
        IntPtr attributes, string? name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long dueTime,
        int period, IntPtr completionRoutine, IntPtr argument, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
