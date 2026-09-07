using System.Diagnostics;
using UniPad.Core.Input;

namespace UniPad.Core.Output;

/// <summary>
/// Forwards rumble commands from the game back to the physical controller.
/// <para>
/// Throttling is mandatory: some games push feedback packets at hundreds of hertz, and several
/// Bluetooth pads disconnect when flooded.
/// </para>
/// </summary>
public sealed class FeedbackRouter
{
    /// <summary>Minimum spacing between forwarded rumble commands per device.</summary>
    private const int MinimumIntervalMs = 10;

    /// <summary>Duration requested for each forwarded pulse; slightly longer than the interval so
    /// continuous rumble does not stutter between commands.</summary>
    private const uint PulseDurationMs = 60;

    private readonly SdlInputBackend _input;
    private readonly Dictionary<string, RumbleChannel> _channels = new();
    private readonly object _lock = new();

    /// <summary>Creates a router that emits rumble through the given input backend.</summary>
    public FeedbackRouter(SdlInputBackend input)
    {
        _input = input;
    }

    /// <summary>
    /// Sends a rumble command to <paramref name="device"/>, scaled by <paramref name="strengthPercent"/>
    /// and throttled per device.
    /// </summary>
    public void Route(DeviceId device, byte largeMotor, byte smallMotor, int strengthPercent)
    {
        var key = device.ToString();
        var now = Stopwatch.GetTimestamp();

        lock (_lock)
        {
            if (!_channels.TryGetValue(key, out var channel))
            {
                channel = new RumbleChannel();
                _channels[key] = channel;
            }

            var elapsedMs = (now - channel.LastSentTicks) * 1000.0 / Stopwatch.Frequency;
            var isStopCommand = largeMotor == 0 && smallMotor == 0;

            // A stop command always goes through immediately, otherwise a controller could be left
            // buzzing after the game releases it.
            if (!isStopCommand && elapsedMs < MinimumIntervalMs)
            {
                channel.PendingLarge = largeMotor;
                channel.PendingSmall = smallMotor;
                channel.HasPending = true;
                return;
            }

            channel.LastSentTicks = now;
            channel.HasPending = false;
        }

        var scale = Math.Clamp(strengthPercent, 0, 100) / 100.0;
        var low = (ushort)Math.Clamp(largeMotor * 257 * scale, 0, ushort.MaxValue);
        var high = (ushort)Math.Clamp(smallMotor * 257 * scale, 0, ushort.MaxValue);

        _input.Rumble(device, low, high, low == 0 && high == 0 ? 0 : PulseDurationMs);
    }

    /// <summary>
    /// Flushes coalesced rumble commands. Called once per poll cycle so a throttled update is not
    /// dropped entirely.
    /// </summary>
    public void FlushPending(Func<string, int> strengthLookup)
    {
        List<(string Key, byte Large, byte Small)>? due = null;

        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var (key, channel) in _channels)
            {
                if (!channel.HasPending)
                {
                    continue;
                }

                var elapsedMs = (now - channel.LastSentTicks) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs < MinimumIntervalMs)
                {
                    continue;
                }

                due ??= [];
                due.Add((key, channel.PendingLarge, channel.PendingSmall));
                channel.HasPending = false;
                channel.LastSentTicks = now;
            }
        }

        if (due is null)
        {
            return;
        }

        foreach (var (key, large, small) in due)
        {
            var deviceId = DeviceId.TryParse(key);
            if (deviceId is null)
            {
                continue;
            }

            var scale = Math.Clamp(strengthLookup(key), 0, 100) / 100.0;
            var low = (ushort)Math.Clamp(large * 257 * scale, 0, ushort.MaxValue);
            var high = (ushort)Math.Clamp(small * 257 * scale, 0, ushort.MaxValue);
            _input.Rumble(deviceId, low, high, PulseDurationMs);
        }
    }

    /// <summary>Immediately stops rumble on every known device.</summary>
    public void StopAll()
    {
        List<string> keys;
        lock (_lock)
        {
            keys = [.. _channels.Keys];
            _channels.Clear();
        }

        foreach (var key in keys)
        {
            var deviceId = DeviceId.TryParse(key);
            if (deviceId is not null)
            {
                _input.Rumble(deviceId, 0, 0, 0);
            }
        }
    }

    /// <summary>Fires a short identification buzz on a device.</summary>
    public void Identify(DeviceId device)
    {
        _input.Rumble(device, 0xC000, 0xC000, 600);
    }

    private sealed class RumbleChannel
    {
        public long LastSentTicks;
        public byte PendingLarge;
        public byte PendingSmall;
        public bool HasPending;
    }
}
