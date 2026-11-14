using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SDL;
using UniPad.Core.Input;

/// <summary>Real SDL native library, synthetic joystick hardware; no ViGEm or physical pad.</summary>
internal static unsafe class SdlIntegration
{
    public static int Run()
    {
        using var input = new SdlInputBackend();
        input.PollRateHz = 1000;
        var gate = typeof(SdlInputBackend).GetField("_sdlLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(input)!;
        var name = Marshal.StringToCoTaskMemUTF8("UniPad native integration fixture");
        SDL_JoystickID firstId = default, secondId = default;
        var checks = 0;
        void AssertEventually(Func<bool> predicate, string description)
        {
            var watch = Stopwatch.StartNew();
            while (!predicate() && watch.ElapsedMilliseconds < 3000) Thread.Sleep(1);
            if (!predicate()) throw new InvalidOperationException("SDL: " + description);
            checks++;
        }
        try
        {
            input.Start();
            var desc = new SDL_VirtualJoystickDesc
            {
                version = (uint)sizeof(SDL_VirtualJoystickDesc),
                vendor_id = 0x1209, product_id = 0x55AA,
                naxes = 6, nbuttons = 16, nhats = 1,
                name = (byte*)name
            };
            lock (gate)
            {
                firstId = SDL3.SDL_AttachVirtualJoystick(&desc);
                secondId = SDL3.SDL_AttachVirtualJoystick(&desc);
            }
            AssertEventually(() => input.Devices.Count(d => d.VendorId == 0x1209 && d.ProductId == 0x55AA) == 2,
                "Twin native devices discovered");
            var first = input.Devices.Single(d => d.InstanceId == (uint)firstId);
            var second = input.Devices.Single(d => d.InstanceId == (uint)secondId);
            if (first.Id == second.Id) throw new InvalidOperationException("SDL twin port collision");
            checks++;
            lock (gate)
            {
                SDL3.SDL_SetJoystickVirtualButton((SDL_Joystick*)first.Handle, 0, true);
                SDL3.SDL_SetJoystickVirtualAxis((SDL_Joystick*)first.Handle, 0, 23456);
                SDL3.SDL_SetJoystickVirtualHat((SDL_Joystick*)first.Handle, 0, 3);
            }
            AssertEventually(() => first.Snapshot.Buttons[0] && first.Snapshot.Axes[0] == 23456
                && first.Snapshot.Hats[0] == 3, "Native button, axis and hat refresh");
            lock (gate) SDL3.SDL_DetachVirtualJoystick(firstId);
            firstId = default;
            AssertEventually(() => !first.IsConnected, "Native removal");
            if (!ReferenceEquals(input.FindDevice(first.Id), first))
                throw new InvalidOperationException("SDL disconnected record lost");
            checks++;
            lock (gate) firstId = SDL3.SDL_AttachVirtualJoystick(&desc);
            AssertEventually(() => first.IsConnected, "Native reconnect resurrects record");
            if (!ReferenceEquals(input.FindDevice(first.Id), first))
                throw new InvalidOperationException("SDL reconnected record replaced");
            checks++;
            lock (gate) SDL3.SDL_SetJoystickVirtualButton((SDL_Joystick*)second.Handle, 1, true);
            AssertEventually(() => second.Snapshot.Buttons[1], "Twin remains live across reconnect");
            foreach (var hz in new[] { 60, 125, 250, 500, 1000, 2000 })
            {
                input.PollRateHz = hz;
                var start = input.PollCount;
                AssertEventually(() => input.PollCount > start + 2, "Runtime rate switch " + hz);
            }
            return checks;
        }
        finally
        {
            lock (gate)
            {
                if ((uint)firstId != 0) SDL3.SDL_DetachVirtualJoystick(firstId);
                if ((uint)secondId != 0) SDL3.SDL_DetachVirtualJoystick(secondId);
            }
            // Dispose the polling thread and SDL before releasing the native name buffer.
            input.Dispose();
            Marshal.FreeCoTaskMem(name);
        }
    }
}
