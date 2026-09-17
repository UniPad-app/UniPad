using System.Threading;
using Serilog;

namespace UniPad.App.Services;

/// <summary>
/// Keeps a single UniPad process per user session and hands a later launch over to the one already
/// running.
/// <para>
/// Two instances cannot coexist usefully: they write the same profile file, so whichever exits
/// last silently discards the other's settings, and each one connects its own set of virtual pads,
/// so a game suddenly sees four controllers where the user configured two. Launching again is
/// therefore treated as "show me the window", which is almost always what was meant - the window
/// was hidden in the notification area and the executable was the obvious way back to it.
/// </para>
/// <para>
/// Both handles are named in the <c>Local\</c> namespace, which scopes them to the logon session.
/// Two people signed in to the same machine each get their own UniPad, which is correct: their
/// controllers, profiles and virtual pads are separate.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // The mutex is never waited on or owned - only its existence matters, and the out parameter of
    // the constructor reports that. Ownership would tie the handle to the thread that took it and
    // make releasing it from anywhere else throw, or leave it abandoned if the process crashed.
    private const string MutexName = @"Local\UniPad.SingleInstance";
    private const string ActivationEventName = @"Local\UniPad.Activate";

    private readonly EventWaitHandle _activation;
    private readonly CancellationTokenSource _stop = new();
    private Mutex? _mutex;

    /// <summary>The guard held by this process, once it is known to be the first instance.</summary>
    public static SingleInstance? Current { get; private set; }

    /// <summary>Raised on a background thread when another launch asks for the window.</summary>
    public event Action? ActivationRequested;

    /// <summary>Claims the slot for this process, or discovers that another one already holds it.</summary>
    public SingleInstance()
    {
        _mutex = new Mutex(false, MutexName, out var createdNew);
        _activation = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName, out _);

        IsFirstInstance = createdNew;

        if (createdNew)
        {
            Current = this;
        }
    }

    /// <summary>False when UniPad is already running in this session.</summary>
    public bool IsFirstInstance { get; }

    /// <summary>Asks the running instance to show itself. Called by the process that is about to exit.</summary>
    public void SignalExistingInstance()
    {
        try
        {
            _activation.Set();
            Log.Information("UniPad is already running; asked the existing instance to show its window");
        }
        catch (Exception ex)
        {
            // The other process may be shutting down as we signal it. Nothing can be done, and
            // failing loudly here would be worse than doing nothing.
            Log.Debug(ex, "Could not signal the existing instance");
        }
    }

    /// <summary>Starts listening for later launches. Called once the window exists to be shown.</summary>
    public void StartListening()
    {
        var thread = new Thread(Listen)
        {
            Name = "UniPad activation listener",
            IsBackground = true,
        };

        thread.Start();
    }

    /// <summary>
    /// Gives up the slot without tearing the process down, for the one case where a second
    /// instance is intended: an update has been staged and the new build is about to be launched
    /// while this process is still alive. Without this the incoming build would see the slot taken
    /// and hand itself back to a process that is on its way out, leaving nothing running at all.
    /// </summary>
    public void ReleaseForRestart()
    {
        var mutex = _mutex;
        _mutex = null;
        mutex?.Dispose();

        Current = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();

        ReleaseForRestart();

        _activation.Dispose();
        _stop.Dispose();
    }

    private void Listen()
    {
        var handles = new WaitHandle[] { _activation, _stop.Token.WaitHandle };

        while (!_stop.IsCancellationRequested)
        {
            // Index 1 is the shutdown signal, so the thread never outlives the process it serves.
            if (WaitHandle.WaitAny(handles) != 0)
            {
                return;
            }

            ActivationRequested?.Invoke();
        }
    }
}
