using Nefarius.ViGEm.Client;
using Serilog;
using UniPad.Core.Input;
using UniPad.Core.Mapping;

namespace UniPad.Core.Output;

/// <summary>
/// Owns the ViGEm client and the set of virtual pads, and drives one mapping evaluation per player
/// on every poll cycle.
/// </summary>
public sealed class OutputManager : IDisposable
{
    /// <summary>Maximum number of player slots supported.</summary>
    public const int MaxPlayers = 8;

    /// <summary>Number of XInput slots Windows itself provides. A hard OS limit.</summary>
    public const int XInputSlotCount = 4;

    /// <summary>
    /// Delay between plugging in successive pads. Windows generally assigns XInput slots in
    /// connection order, so spacing the connects makes player numbering predictable.
    /// </summary>
    private const int ConnectStaggerMs = 300;

    private readonly SdlInputBackend _input;
    private readonly FeedbackRouter _feedback;
    private readonly IVirtualPad?[] _pads = new IVirtualPad?[MaxPlayers];
    private readonly PlayerMapping?[] _mappings = new PlayerMapping?[MaxPlayers];
    private readonly MappingEngine[] _engines = new MappingEngine[MaxPlayers];
    private readonly PadState[] _states = new PadState[MaxPlayers];
    private readonly object _lock = new();

    private ViGEmClient? _client;
    private bool _driverAvailable;
    private volatile bool _enabled = true;

    /// <summary>Creates a manager bound to an input backend.</summary>
    public OutputManager(SdlInputBackend input)
    {
        _input = input;
        _feedback = new FeedbackRouter(input);

        for (var i = 0; i < MaxPlayers; i++)
        {
            _engines[i] = new MappingEngine(_input.FindDevice);
        }
    }

    /// <summary>Rumble router, exposed so the UI can trigger identification buzzes.</summary>
    public FeedbackRouter Feedback => _feedback;

    /// <summary>True when ViGEmBus was reachable and the client initialised.</summary>
    public bool IsDriverAvailable => _driverAvailable;

    /// <summary>Master enable switch; when false all pads stay silent but remain connected.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                SubmitNeutralToAll();
            }

            Log.Information("Output {State}", value ? "enabled" : "disabled");
        }
    }

    /// <summary>Last error encountered while initialising the driver, for display in the UI.</summary>
    public string? DriverError { get; private set; }

    /// <summary>
    /// Attempts to create the ViGEm client. Returns false when the driver is missing, which the UI
    /// turns into an install prompt rather than a crash.
    /// </summary>
    public bool TryInitialiseDriver()
    {
        if (_driverAvailable)
        {
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            DriverError = "Virtual controllers are only available on Windows.";
            Log.Warning("ViGEm unavailable: not running on Windows");
            return false;
        }

        try
        {
            // PLATFORM: ViGEmClient talks to the ViGEmBus kernel driver over a Windows device handle.
            _client = new ViGEmClient();
            _driverAvailable = true;
            DriverError = null;
            Log.Information("ViGEm client initialised");
            return true;
        }
        catch (Exception ex)
        {
            _driverAvailable = false;
            DriverError = ex.Message;
            Log.Warning(ex, "ViGEm client initialisation failed");
            return false;
        }
    }

    /// <summary>
    /// Applies a full set of player mappings: pads are created, removed or re-typed to match, and
    /// connected in slot order with a stagger so XInput indices line up with player numbers.
    /// <remarks>
    /// Blocks for up to <see cref="ConnectStaggerMs"/> milliseconds per pad. Must not be called
    /// from a UI thread.
    /// </remarks>
    /// </summary>
    public void ApplyMappings(IReadOnlyList<PlayerMapping> mappings)
    {
        lock (_lock)
        {
            // First pass: tear down anything that no longer matches.
            for (var i = 0; i < MaxPlayers; i++)
            {
                var wanted = mappings.FirstOrDefault(m => m.Index == i);
                _mappings[i] = wanted;

                var shouldExist = wanted is { Enabled: true } && wanted.Device is not null;
                var existing = _pads[i];

                if (existing is not null && (!shouldExist || existing.Type != wanted!.OutputType))
                {
                    DisposePad(i);
                }

                _engines[i].ResetToggles();
            }

            if (!_driverAvailable)
            {
                return;
            }

            // Second pass: create and connect missing pads in ascending slot order. The slots are
            // collected up front so the stagger can be skipped after the last one - there is no
            // ordering left to protect once every pad is plugged in.
            Span<int> pending = stackalloc int[MaxPlayers];
            var pendingCount = 0;

            for (var i = 0; i < MaxPlayers; i++)
            {
                var mapping = _mappings[i];
                if (mapping is not { Enabled: true } || mapping.Device is null || _pads[i] is not null)
                {
                    continue;
                }

                pending[pendingCount++] = i;
            }

            for (var p = 0; p < pendingCount; p++)
            {
                var slot = pending[p];

                if (!TryCreatePad(slot, _mappings[slot]!))
                {
                    // Nothing was plugged in, so there is nothing to space out either.
                    continue;
                }

                if (p < pendingCount - 1)
                {
                    // Stagger so Windows assigns XInput indices in the expected order.
                    Thread.Sleep(ConnectStaggerMs);
                }
            }
        }

        RefreshUserIndices();
    }

    private bool TryCreatePad(int slot, PlayerMapping mapping)
    {
        if (_client is null)
        {
            return false;
        }

        try
        {
            IVirtualPad pad = mapping.OutputType == VirtualPadType.DualShock4
                ? new ViGEmDs4Pad(_client, slot)
                : new ViGEmX360Pad(_client, slot);

            pad.RumbleReceived += args => OnRumble(slot, args);

            // Suppress our own pad from the input side while it is being plugged in, otherwise SDL
            // sees it as a new controller and the output loops straight back into the input.
            using (_input.ExpectVirtualDevice())
            {
                pad.Connect();
            }

            _pads[slot] = pad;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create virtual pad for player {Slot}", slot + 1);
            DriverError = ex.Message;
            return false;
        }
    }

    private void OnRumble(int slot, RumbleEventArgs args)
    {
        var mapping = _mappings[slot];
        if (mapping?.Device is null || mapping.Vibration is { Enabled: false })
        {
            return;
        }

        _feedback.Route(mapping.Device, args.LargeMotor, args.SmallMotor, mapping.Vibration.Strength);
    }

    /// <summary>
    /// Evaluates every enabled player's mapping and pushes the resulting reports. Invoked once per
    /// poll cycle from the input thread.
    /// </summary>
    public void ProcessCycle()
    {
        if (!_enabled)
        {
            return;
        }

        for (var i = 0; i < MaxPlayers; i++)
        {
            var mapping = _mappings[i];
            var pad = _pads[i];

            if (mapping is not { Enabled: true } || pad is not { IsConnected: true })
            {
                continue;
            }

            _engines[i].Evaluate(mapping, ref _states[i]);
            pad.Submit(in _states[i]);
        }

        _feedback.FlushPending(GetStrengthForDeviceKey);
    }

    private int GetStrengthForDeviceKey(string deviceKey)
    {
        foreach (var mapping in _mappings)
        {
            if (mapping?.Device?.ToString() == deviceKey)
            {
                return mapping.Vibration.Strength;
            }
        }

        return 100;
    }

    /// <summary>
    /// Reads the current emulated state of a player. Used by the UI live preview so it renders
    /// exactly what the game receives.
    /// </summary>
    public PadState GetState(int slot) =>
        (uint)slot < MaxPlayers ? _states[slot] : default;

    /// <summary>Re-queries XInput slot numbers from every X360 pad.</summary>
    public void RefreshUserIndices()
    {
        foreach (var pad in _pads)
        {
            if (pad is ViGEmX360Pad x360)
            {
                x360.TryResolveUserIndex();
            }
        }
    }

    /// <summary>Reports the XInput slot assigned to a player, when known.</summary>
    public int? GetUserIndex(int slot) =>
        (uint)slot < MaxPlayers ? _pads[slot]?.UserIndex : null;

    /// <summary>True when the player's pad is currently plugged into the virtual bus.</summary>
    public bool IsPadConnected(int slot) =>
        (uint)slot < MaxPlayers && _pads[slot] is { IsConnected: true };

    /// <summary>Buzzes the physical device assigned to a player so the user can identify it.</summary>
    public void IdentifyPlayer(int slot)
    {
        var mapping = _mappings.ElementAtOrDefault(slot);
        if (mapping?.Device is not null)
        {
            _feedback.Identify(mapping.Device);
        }
    }

    private void SubmitNeutralToAll()
    {
        var neutral = default(PadState);
        foreach (var pad in _pads)
        {
            if (pad is { IsConnected: true })
            {
                pad.Submit(in neutral);
            }
        }
    }

    private void DisposePad(int slot)
    {
        var pad = _pads[slot];
        if (pad is null)
        {
            return;
        }

        try
        {
            var neutral = default(PadState);
            pad.Submit(in neutral);
            pad.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing pad for player {Slot}", slot + 1);
        }

        _pads[slot] = null;
    }

    /// <summary>Disconnects and disposes every pad, leaving the virtual bus clean.</summary>
    public void DisconnectAll()
    {
        lock (_lock)
        {
            for (var i = 0; i < MaxPlayers; i++)
            {
                DisposePad(i);
            }
        }

        _feedback.StopAll();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisconnectAll();

        try
        {
            _client?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing ViGEm client");
        }

        _client = null;
        _driverAvailable = false;
    }
}
