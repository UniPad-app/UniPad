using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using UniPad.Core.Input;
using UniPad.Core.Mapping;
using UniPad.Core.Network;
using UniPad.Core.Output;

// No external test/benchmark framework; runs against either exact upstream or patched Core.
// Hardware-free microbenchmarks are NOT whole-application Windows CPU measurements.
internal static class Program
{
    private static int _checks;
    private static long _sink;
    private static readonly List<object> Measurements = [];
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private static async Task Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Contains("--wait") || args.Contains("--legacy-wait"))
        {
            RunWaitBenchmarks(args.Contains("--legacy-wait"));
            return;
        }
        _checks += SdlIntegration.Run();
        CheckIdentityAndLookup();
        CheckMappingFeatures();
        var checksum = ReplayMappings();
        await CheckPhones();
        RunBenchmarks();
        var report = new
        {
            label = args.FirstOrDefault() ?? "unlabelled",
            platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            runtime = Environment.Version.ToString(),
            logicalProcessors = Environment.ProcessorCount,
            checks = _checks,
            replayFrames = 100_000,
            replayChecksum = checksum.ToString("X16"),
            measurements = Measurements,
            note = "Synthetic Core-only microbenchmarks; no SDL hardware IO, ViGEm, Avalonia, or DS4Windows included."
        };
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        _checks++;
    }

    private static InputDevice MakeDevice(DeviceId id) => new()
    {
        Id = id, Name = "Test device", IsConnected = true,
        AxisCount = 6, ButtonCount = 32, HatCount = 1,
        Snapshot = new InputSnapshot(6, 32, 1)
    };

    private static void Register(SdlInputBackend backend, InputDevice device)
    {
        // Test fixture injection only: never opens a native SDL handle.
        var dictionary = (IDictionary)typeof(SdlInputBackend).GetField("_byDeviceId", Private)!.GetValue(backend)!;
        var keyType = dictionary.GetType().GetGenericArguments()[0];
        dictionary[keyType == typeof(string) ? device.Id.ToString() : device.Id] = device;
    }

    private static void CheckIdentityAndLookup()
    {
        using var input = new SdlInputBackend();
        var id = new DeviceId("030000005e0400008e02000010010000", 0);
        var first = MakeDevice(id);
        var second = MakeDevice(id with { Port = 1 });
        Register(input, first);
        Register(input, second);
        Check(ReferenceEquals(input.FindDevice(new DeviceId(id.Guid, 0)), first), "Equal IDs resolve");
        Check(ReferenceEquals(input.FindDevice(id with { Port = 1 }), second), "Twin ports remain distinct");
        Check(input.FindDevice(id with { Port = 2 }) is null, "Missing device");
        Check(input.FindDevice(null) is null, "Null device");
        first.IsConnected = false;
        Check(ReferenceEquals(input.FindDevice(id), first), "Disconnected record retained");
        first.IsConnected = true;
        Check(ReferenceEquals(input.FindDevice(id), first), "Reconnection reuses record");
        var synthetic = MakeDevice(DeviceId.Keyboard);
        input.SyntheticResolver = d => d.IsSynthetic ? synthetic : null;
        Check(ReferenceEquals(input.FindDevice(DeviceId.Keyboard), synthetic), "Keyboard fallback");
        Check(ReferenceEquals(input.FindDevice(DeviceId.Mouse), synthetic), "Mouse fallback");
        Check(ReferenceEquals(input.FindDevice(new DeviceId("phone-12345678", 0)), synthetic), "Phone fallback");
        Check(DeviceId.TryParse(id.ToString()) == id, "Profile device string unchanged");
        input.PollRateHz = 20;
        Check(input.PollRateHz == 60, "60 Hz minimum retained");
        input.PollRateHz = 5000;
        Check(input.PollRateHz == 2000, "2000 Hz maximum retained");
    }

    private static void CheckMappingFeatures()
    {
        var device = MakeDevice(DeviceId.Keyboard);
        var engine = new MappingEngine(_ => device);
        var mapping = new PlayerMapping { Device = device.Id, EmulateStickWithDpad = false };
        var state = default(PadState);
        mapping.SetBinding(PadTarget.A, InputBinding.ForButton(device.Id, 0));
        device.Snapshot.Buttons[0] = true;
        engine.Evaluate(mapping, ref state);
        Check(state.A, "Button to button");
        mapping.Bindings[PadTarget.A].Toggle = true;
        engine.ResetToggles();
        engine.Evaluate(mapping, ref state);
        Check(state.A, "Toggle rising edge");
        device.Snapshot.Buttons[0] = false;
        engine.Evaluate(mapping, ref state);
        Check(state.A, "Toggle retains latch on release");
        device.Snapshot.Buttons[0] = true;
        engine.Evaluate(mapping, ref state);
        Check(!state.A, "Toggle second rising edge");
        mapping.SetBinding(PadTarget.LStickRight, InputBinding.ForButton(device.Id, 1));
        mapping.LeftStick.Deadzone = 0;
        mapping.LeftStick.Range = 1;
        device.Snapshot.Buttons[1] = true;
        engine.Evaluate(mapping, ref state);
        Check(state.LeftThumbX == 32767, "Button to axis full deflection");
        mapping.SetBinding(PadTarget.LStickModifier, InputBinding.ForButton(device.Id, 2));
        device.Snapshot.Buttons[2] = true;
        engine.Evaluate(mapping, ref state);
        Check(state.LeftThumbX == 16383, "Stick modifier scaling");
        mapping.ClearBindings();
        mapping.SetBinding(PadTarget.B, InputBinding.ForAxis(device.Id, 0, AxisDirection.Positive));
        device.Snapshot.Axes[0] = 20000;
        engine.Evaluate(mapping, ref state);
        Check(state.B, "Axis to button threshold");
        mapping.Bindings[PadTarget.B].Invert = true;
        engine.Evaluate(mapping, ref state);
        Check(!state.B, "Axis inversion");
        mapping.SetBinding(PadTarget.RStickRight, InputBinding.ForAxis(device.Id, 0, AxisDirection.Positive));
        mapping.RightStick.Deadzone = 0;
        mapping.RightStick.Range = 1;
        engine.Evaluate(mapping, ref state);
        Check(state.RightThumbX == 20000, "Axis to axis magnitude");
        mapping.RightStick.Deadzone = .8f;
        engine.Evaluate(mapping, ref state);
        Check(state.RightThumbX == 0, "Radial dead zone");
        mapping.SetBinding(PadTarget.LeftTrigger, InputBinding.ForAxis(device.Id, 1, AxisDirection.Positive));
        device.Snapshot.Axes[1] = 32767;
        engine.Evaluate(mapping, ref state);
        Check(state.LeftTrigger == 255, "Analogue trigger full scale");
        device.Snapshot.HasRestingValues = true;
        device.Snapshot.AxisRestingValues[1] = -32768;
        device.Snapshot.Axes[1] = -32768;
        engine.Evaluate(mapping, ref state);
        Check(state.LeftTrigger == 0, "Bipolar trigger at rest");
        mapping.SetBinding(PadTarget.DPadUp, InputBinding.ForHat(device.Id, 0, 1));
        mapping.SetBinding(PadTarget.DPadRight, InputBinding.ForHat(device.Id, 0, 2));
        device.Snapshot.Hats[0] = 3;
        mapping.EmulateStickWithDpad = true;
        engine.Evaluate(mapping, ref state);
        Check(state.DPadUp && state.DPadRight && state.LeftThumbX > 0 && state.LeftThumbY > 0,
            "Hat and D-pad stick emulation");
        Check(Math.Abs(state.LeftThumbX - state.LeftThumbY) <= 1, "Normalised diagonal");
        device.IsConnected = false;
        engine.Evaluate(mapping, ref state);
        Check(state.IsNeutral, "Disconnected input neutralises output");
        device.IsConnected = true;
        mapping.ClearBindings();
        engine.ResetToggles();
        engine.Evaluate(mapping, ref state);
        Check(state.IsNeutral, "Cleared mapping neutralises output");
    }

    private static ulong ReplayMappings()
    {
        // Deterministic, covers all 27 targets and all binding source enums. Compare checksum
        // between separate baseline/patched runs (not GetHashCode, which is randomised).
        var random = new Random(47821);
        var device = MakeDevice(DeviceId.Keyboard);
        var mapping = new PlayerMapping { Device = device.Id };
        var engine = new MappingEngine(_ => device);
        var state = default(PadState);
        ulong hash = 14695981039346656037;
        var fields = typeof(PadState).GetFields(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
        for (var frame = 0; frame < 100_000; frame++)
        {
            if (frame % 100 == 0)
            {
                mapping.ClearBindings();
                engine.ResetToggles();
                foreach (var target in PadTargetInfo.All)
                    mapping.Bindings[target] = new InputBinding
                    {
                        Device = device.Id, Type = (BindingSourceType)random.Next(0, 6),
                        Index = random.Next(0, 6), HatMask = (byte)(1 << random.Next(0, 4)),
                        Direction = (AxisDirection)random.Next(0, 3), Invert = random.Next(2) == 0,
                        Toggle = random.Next(2) == 0, Threshold = (float)random.NextDouble()
                    };
                mapping.LeftStick.Deadzone = (float)random.NextDouble() * .9f;
                mapping.LeftStick.Range = (float)random.NextDouble() * 1.5f;
                mapping.RightStick.Deadzone = (float)random.NextDouble() * .9f;
                mapping.RightStick.Range = (float)random.NextDouble() * 1.5f;
                mapping.EmulateStickWithDpad = random.Next(2) == 0;
            }
            for (var i = 0; i < 6; i++) device.Snapshot.Axes[i] = (short)random.Next(-32767, 32768);
            for (var i = 0; i < 32; i++) device.Snapshot.Buttons[i] = random.Next(2) == 0;
            device.Snapshot.Hats[0] = (byte)random.Next(16);
            device.IsConnected = frame % 41 != 0;
            engine.Evaluate(mapping, ref state);
            foreach (var field in fields)
            {
                var value = Convert.ToInt64(field.GetValue(state));
                hash = unchecked((hash ^ (ulong)value) * 1099511628211);
            }
        }
        Check(hash != 0, "Replay completed");
        return hash;
    }

    private static async Task CheckPhones()
    {
        using var server = new PhoneServer(31740);
        server.Start();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), CancellationToken.None);
        async Task Send(string text) => await socket.SendAsync(Encoding.UTF8.GetBytes(text),
            WebSocketMessageType.Text, true, CancellationToken.None);
        await Send($"{{\"t\":\"hello\",\"k\":\"{server.Token}\",\"id\":\"12345678abcd\",\"name\":\"test\"}}");
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(5000);
        await socket.ReceiveAsync(buffer, timeout.Token);
        var phone = server.Clients.Single();
        Check(phone.Device.IsConnected, "Phone admission");
        await Send("{\"t\":\"s\",\"b\":1,\"lx\":0.5}");
        var limit = Stopwatch.StartNew();
        while (limit.ElapsedMilliseconds < 1000)
        {
            server.Poll();
            if (phone.Device.Snapshot.Buttons[0]) break;
            await Task.Delay(1);
        }
        Check(phone.Device.Snapshot.Buttons[0], "Phone state reaches snapshot");
        Check(phone.Device.Snapshot.Axes[0] == 16383, "Phone analogue scale");
        await Send("{\"t\":\"s\",\"b\":0}");
        await Task.Delay(5);
        server.Poll();
        Check(phone.Device.Snapshot.Buttons[0], "Phone quick-tap latch");
        await Task.Delay(60);
        server.Poll();
        Check(!phone.Device.Snapshot.Buttons[0], "Latch expiry");
        await Send("{\"t\":\"s\",\"b\":1,\"lx\":1}");
        await Task.Delay(30);
        server.Poll();
        Check(phone.Device.Snapshot.Buttons[0], "Second phone press");
        await Task.Delay(550);
        server.Poll();
        Check(!phone.Device.Snapshot.Buttons[0] && phone.Device.Snapshot.Axes[0] == 0,
            "Stale phone neutralisation");
        using var replacement = new ClientWebSocket();
        await replacement.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), CancellationToken.None);
        await replacement.SendAsync(Encoding.UTF8.GetBytes(
            $"{{\"t\":\"hello\",\"k\":\"{server.Token}\",\"id\":\"12345678abcd\",\"name\":\"ignored\"}}"),
            WebSocketMessageType.Text, true, CancellationToken.None);
        await replacement.ReceiveAsync(buffer, timeout.Token);
        Check(ReferenceEquals(server.Clients.Single(), phone), "Reconnect reuses cached phone record");
        await replacement.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"s\",\"b\":2,\"ly\":-0.5}"),
            WebSocketMessageType.Text, true, CancellationToken.None);
        limit.Restart();
        while (limit.ElapsedMilliseconds < 1000)
        {
            server.Poll();
            if (phone.Device.Snapshot.Buttons[1]) break;
            await Task.Delay(1);
        }
        Check(phone.Device.Snapshot.Buttons[1], "Reconnect still reaches poll cache");
        await server.RenameAsync(phone.Key, "renamed");
        Check(phone.Device.Name == "Phone: renamed", "Rename preserves cached record");
        await server.KickAsync(phone.Key);
        server.Poll();
        Check(server.Clients.Count == 0 && !phone.Device.IsConnected, "Kick removes cached topology");
        socket.Abort();
        replacement.Abort();
    }

    private static void Measure(string name, int iterations, Action work)
    {
        for (var i = 0; i < 40_000; i++) work();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var watch = new Stopwatch();
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        watch.Start();
        for (var i = 0; i < iterations; i++) work();
        watch.Stop();
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Measurements.Add(new { name, iterations, nsPerOperation = watch.Elapsed.TotalNanoseconds / iterations,
            bytesPerOperation = bytes / (double)iterations });
    }

    private static void RunBenchmarks()
    {
        using var input = new SdlInputBackend();
        var device = MakeDevice(new DeviceId("030000005e0400008e02000010010000", 0));
        Register(input, device);
        var key = new DeviceId(device.Id.Guid, 0);
        Measure("FindDevice_physical", 1_000_000, () => { if (input.FindDevice(key) is not null) _sink++; });
        var mapping = new PlayerMapping { Device = device.Id };
        foreach (var target in PadTargetInfo.All) mapping.SetBinding(target, InputBinding.ForButton(key, 0));
        var state = default(PadState);
        var engine = new MappingEngine(input.FindDevice);
        Measure("Mapping_27_bound_targets", 250_000, () => engine.Evaluate(mapping, ref state));
        // No driver is initialised and no pads are created. Do not invoke Dispose here:
        // ViGEm's third-party module initializer is Windows-only, even with a null client.
        var output = new OutputManager(input);
        Measure("OutputCycle_no_connected_pads", 250_000, output.ProcessCycle);
        var mappings = (PlayerMapping?[])typeof(OutputManager).GetField("_mappings", Private)!.GetValue(output)!;
        var pads = (IVirtualPad?[])typeof(OutputManager).GetField("_pads", Private)!.GetValue(output)!;
        mapping.Enabled = true;
        for (var i = 0; i < 8; i++)
        {
            mappings[i] = mapping;
            pads[i] = new FakePad(i);
        }
        Measure("OutputCycle_eight_fake_pads", 100_000, output.ProcessCycle);
        using var phones = new PhoneServer();
        Measure("PhonePoll_zero_clients", 250_000, phones.Poll);
        var clients = (IDictionary)typeof(PhoneServer).GetField("_clients", Private)!.GetValue(phones)!;
        var list = new List<PhoneClient>();
        for (var i = 0; i < 8; i++)
        {
            var phone = new PhoneClient { Key = $"phone-{i}", Device = MakeDevice(new DeviceId($"phone-{i}", 0)) };
            // Correct phone topology, not MakeDevice's generic 32-button test layout.
            phone.Device.Snapshot = new InputSnapshot(PhoneLayout.AxisCount, PhoneLayout.ButtonCount, 0);
            clients.Add(phone.Key, phone);
            list.Add(phone);
        }
        typeof(PhoneServer).GetField("_pollClients", Private)?.SetValue(phones, list.ToArray());
        Measure("PhonePoll_eight_clients", 250_000, phones.Poll);
    }

    private sealed class FakePad(int slot) : IVirtualPad
    {
        public int Slot => slot;
        public VirtualPadType Type => VirtualPadType.Xbox360;
        public bool IsConnected => true;
        public int? UserIndex => null;
        public event Action<RumbleEventArgs>? RumbleReceived { add { } remove { } }
        public void Connect() { }
        public void Disconnect() { }
        public void Dispose() { }
        public void Submit(in PadState state) { if (state.A) _sink++; }
    }

    private static void RunWaitBenchmarks(bool legacyOnly)
    {
        if (!OperatingSystem.IsWindows() && !legacyOnly)
        {
            Console.WriteLine("SKIPPED: native high-resolution timer benchmark requires Windows 10 1803+.");
            return;
        }
        var type = typeof(SdlInputBackend).Assembly.GetType("UniPad.Core.Input.PollDeadlineWaiter");
        using var instance = legacyOnly ? null : (IDisposable)Activator.CreateInstance(type
            ?? throw new InvalidOperationException("Run --wait against the patched Core project."))!;
        Action<Stopwatch, long, long> wait = LegacyWait;
        if (!legacyOnly)
        {
            var native = (bool)type!.GetProperty("UsesHighResolutionTimer", Private)!.GetValue(instance)!;
            Check(native, "Native high-resolution timer must be available; refusing misleading fallback results");
            wait = (Action<Stopwatch, long, long>)type.GetMethod("WaitUntil")!
                .CreateDelegate(typeof(Action<Stopwatch, long, long>), instance);
        }
        foreach (var hz in legacyOnly ? new[] { 1000, 2000 } : new[] { 60, 125, 250, 500, 1000, 2000 })
        foreach (var mode in legacyOnly ? new[] { "legacy" } : new[] { "legacy", "native" })
        {
            var action = mode == "legacy" ? LegacyWait : wait;
            // Warm up for 0.25 s, then run a separate measured 3 s sample.
            for (var pass = 0; pass < 2; pass++)
            {
                using var process = Process.GetCurrentProcess();
                var watch = Stopwatch.StartNew();
                var cpuStart = process.TotalProcessorTime;
                var intervals = new List<double>();
                var last = watch.ElapsedTicks;
                var nextDeadline = last;
                var period = Stopwatch.Frequency / hz;
                while (watch.Elapsed.TotalSeconds < (pass == 0 ? .25 : 3))
                {
                    var start = watch.ElapsedTicks;
                    if (mode == "legacy" || start - nextDeadline >= period) nextDeadline = start;
                    nextDeadline += period;
                    action(watch, nextDeadline, Stopwatch.Frequency);
                    var now = watch.ElapsedTicks;
                    intervals.Add((now - last) * 1_000_000.0 / Stopwatch.Frequency);
                    last = now;
                }
                var cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
                var seconds = watch.Elapsed.TotalSeconds;
                if (pass == 0) continue;
                intervals.Sort();
                double P(double p) => intervals[(int)((intervals.Count - 1) * p)];
                Console.WriteLine(JsonSerializer.Serialize(new { mode, hz, actualHz = intervals.Count / seconds,
                    cpuCorePercent = cpuMs / (seconds * 10), intervalP50Us = P(.5), intervalP95Us = P(.95),
                    intervalP99Us = P(.99), maximumUs = intervals[^1] }));
            }
        }
    }

    private static void LegacyWait(Stopwatch watch, long target, long frequency)
    {
        while (true)
        {
            var remaining = target - watch.ElapsedTicks;
            if (remaining <= 0) return;
            if (remaining > (frequency / 1000) * 2) Thread.Sleep(1);
            else Thread.SpinWait(40);
        }
    }
}
