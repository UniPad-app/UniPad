using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using QRCoder;
using Serilog;
using UniPad.Core.Input;

namespace UniPad.Core.Network;

/// <summary>
/// One connected (or temporarily disconnected) phone.
/// </summary>
public sealed class PhoneClient
{
    public required string Key { get; init; } // "phone-xxxxxxxx"
    public required InputDevice Device { get; init; }
    public string Name { get; set; } = "Phone";
    public int Player { get; set; } = -1; // 0-based slot
    public int RttMs { get; set; }
    internal WebSocket? Socket;
    internal readonly SemaphoreSlim SendLock = new(1, 1);

    // Written by the network thread, read by the poll thread under Gate.
    internal readonly object Gate = new();
    internal int Buttons;
    internal readonly float[] Axes = new float[PhoneLayout.AxisCount];
    internal long LastPacketTicks;

    // A short tap can arrive as press+release in one network burst. The latch keeps the press
    // visible for at least MinPressMs so neither the poll loop nor bind capture misses it.
    internal int Latched;
    internal readonly long[] LatchUntil = new long[PhoneLayout.ButtonCount];
}

/// <summary>
/// Local web server: serves the phone page and turns each phone into a synthetic device.
/// </summary>
public sealed class PhoneServer : IDisposable
{
    /// <summary>
    /// If a phone goes silent this long, its buttons are released (no stuck inputs).
    /// </summary>
    private const int StaleMs = 500;
    /// <summary>Most phones the server accepts at once.</summary>
    public const int MaxClients = 16;

    /// <summary>Minimum time a tapped button stays pressed, so quick taps are never lost.</summary>
    private const int MinPressMs = 40;

    private readonly ConcurrentDictionary<string, PhoneClient> _clients = new();
    private readonly Dictionary<string, string> _pages = new();
    private WebApplication? _app;

    public PhoneServer(int port = 3000) => Port = port;

    public int Port { get; }
    public string Token { get; private set; } = NewToken();
    public string JoinUrl => $"http://{LocalIp()}:{Port}/?k={Token}";

    /// <summary>
    /// Set by AppState: returns the 0-based player that currently uses this phone, or -1 when no
    /// player has it. Phones are never given a player automatically; the user picks one.
    /// </summary>
    public Func<DeviceId, int>? PlayerOf { get; set; }

    /// <summary>Set by AppState: called after a phone is kicked so its slot is freed.</summary>
    public Action<PhoneClient>? Released { get; set; }

    public event Action? ClientsChanged;

    public InputDevice? Find(DeviceId id) =>
        _clients.TryGetValue(id.Guid, out var c) ? c.Device : null;

    /// <summary>Phones that are currently online.</summary>
    public IEnumerable<InputDevice> Devices =>
    _clients.Values.Where(c => c.Device.IsConnected).Select(c => c.Device);

    /// <summary>True once the web server is listening.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Snapshot of every known phone (online or waiting for a reconnect), ordered by player slot.</summary>
    public IReadOnlyList<PhoneClient> Clients =>
        _clients.Values.OrderBy(c => c.Player < 0 ? int.MaxValue : c.Player).ThenBy(c => c.Name).ToArray();

    private volatile string _language = "en";

    /// <summary>Language of the phone page ("en" or "fa"); follows the app language.</summary>
    public string Language => _language;

    /// <summary>Sets the phone page language and pushes it to every connected phone.</summary>
    public void SetLanguage(string language)
    {
        var normalised = language == "fa" ? "fa" : "en";
        if (normalised == _language) return;
        _language = normalised;

        var msg = JsonSerializer.Serialize(new { t = "lang", lang = normalised });
        foreach (var c in _clients.Values) _ = SendAsync(c, msg);
    }

    /// <summary>
    /// Invalidates the current join link and QR code. Phones already connected stay connected,
    /// but a phone that drops has to scan the new code.
    /// </summary>
    public void RegenerateToken() => Token = NewToken();

    /// <summary>PNG image of the join QR code.</summary>
    public byte[] CreateQrPng(int pixelsPerModule = 8) =>
        PngByteQRCodeHelper.GetQRCode(JoinUrl, QRCodeGenerator.ECCLevel.Q, pixelsPerModule);

    public void Start()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(Port));
        _app = builder.Build();

        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5) });

        _app.MapGet("/", () => Results.Content(
            Page("controller.html").Replace("__UNIPAD_LANG__", _language), "text/html; charset=utf-8"));

        _app.MapGet("/admin", (HttpContext c) => Allowed(c, false)
            ? Results.Content(Page("admin.html"), "text/html; charset=utf-8")
            : Results.StatusCode(403));

        _app.MapGet("/admin/state", (HttpContext c) =>
        {
            if (!Allowed(c, false)) return Results.StatusCode(403);
            var png = PngByteQRCodeHelper.GetQRCode(JoinUrl, QRCodeGenerator.ECCLevel.Q, 8);
            return Results.Json(new
            {
                url = JoinUrl,
                qr = "data:image/png;base64," + Convert.ToBase64String(png),
                clients = _clients.Values.OrderBy(x => x.Player).Select(x => new
                {
                    id = x.Key,
                    name = x.Name,
                    player = x.Player,
                    online = x.Device.IsConnected,
                    rtt = x.RttMs,
                }),
            });
        });

        _app.MapPost("/admin/kick/{id}", async (HttpContext c, string id) =>
        {
            if (!Allowed(c, true)) return Results.StatusCode(403);
            await KickAsync(id);
            return Results.Ok();
        });

        _app.MapPost("/admin/rename/{id}", async (HttpContext c, string id, string name) =>
        {
            if (!Allowed(c, true)) return Results.StatusCode(403);
            await RenameAsync(id, name);
            return Results.Ok();
        });

        _app.MapPost("/admin/newtoken", (HttpContext c) =>
        {
            if (!Allowed(c, true)) return Results.StatusCode(403);
            RegenerateToken();
            return Results.Ok();
        });

        _app.Map("/ws", HandleSocketAsync);

        _app.StartAsync().GetAwaiter().GetResult();
        IsRunning = true;
        Log.Information("Phone server listening, join URL {Url}", JoinUrl);
    }

    /// <summary>Copies every phone's latest state into its snapshot. Runs on the 1000 Hz poll thread.</summary>
    public void Poll()
    {
        var now = Stopwatch.GetTimestamp();
        var staleTicks = Stopwatch.Frequency * StaleMs / 1000;

        foreach (var client in _clients.Values)
        {
            var snapshot = client.Device.Snapshot;
            lock (client.Gate)
            {
                var fresh = client.Device.IsConnected && now - client.LastPacketTicks < staleTicks;                
                var buttons = client.Buttons;

                if (client.Latched != 0)
                {
                    for (var i = 0; i < PhoneLayout.ButtonCount; i++)
                    {
                        var bit = 1 << i;
                        if ((client.Latched & bit) == 0) continue;
                        if (now < client.LatchUntil[i]) buttons |= bit;
                        else client.Latched &= ~bit;
                    }
                }
                
                for (var i = 0; i < PhoneLayout.ButtonCount; i++)
                {
                    snapshot.Buttons[i] = fresh && ((buttons >> i) & 1) != 0;
                }

                for (var i = 0; i < PhoneLayout.AxisCount; i++)
                {
                    snapshot.Axes[i] = fresh ? (short)(client.Axes[i] * 32767f) : (short)0;
                }
            }

            snapshot.Revision++;
            client.Device.LastPollTicks = now;
        }
    }

    private async Task HandleSocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[1024];
        PhoneClient? client = null;

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (!result.EndOfMessage || result.Count == 0) continue; // oversized: ignore

                using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                var m = doc.RootElement;
                var type = m.TryGetProperty("t", out var t) ? t.GetString() : null;

                if (client is null)
                {
                    if (type != "hello") continue;
                    client = await HelloAsync(ws, m);
                    if (client is null) return;
                    continue;
                }

                switch (type)
                {
                    case "s":
                        ApplyState(client, m);
                        break;
                    case "ping":
                        var ts = m.GetProperty("ts").GetDouble().ToString("R", CultureInfo.InvariantCulture);
                        await SendAsync(client, $"{{\"t\":\"pong\",\"ts\":{ts}}}");
                        break;
                    case "rtt":
                        client.RttMs = m.GetProperty("ms").GetInt32();
                        break;
                    case "name":
                        var newName = m.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(newName))
                            await RenameAsync(client.Key, newName); // also echoes "name" back and raises ClientsChanged
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Phone socket closed");
        }
        finally
        {
            if (client is not null && ReferenceEquals(client.Socket, ws))
            {
                client.Socket = null;
                client.Device.IsConnected = false; // slot is kept for reconnect
                ClientsChanged?.Invoke();
            }
        }
    }

    private async Task<PhoneClient?> HelloAsync(WebSocket ws, JsonElement m)
    {
        var token = m.TryGetProperty("k", out var k) ? k.GetString() ?? "" : "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Token)))
        {
            await RawSendAsync(ws, "{\"t\":\"badtoken\"}");
            return null;
        }

        // Only hex, max 16 chars (a 32-char id would be mangled by DeviceId.NormaliseGuid).
        var rawId = m.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var cid = new string(rawId.Where(Uri.IsHexDigit).Take(16).ToArray()).ToLowerInvariant();
        if (cid.Length < 8) return null;

        var key = DeviceId.PhonePrefix + cid;
        if (!_clients.ContainsKey(key) && _clients.Count >= MaxClients)
        {
            await RawSendAsync(ws, "{\"t\":\"full\"}");
            return null;
        }

        var isNew = false;
        var client = _clients.GetOrAdd(key, _ =>
        {
            isNew = true;
            return new PhoneClient
            {
                Key = key,
                Device = new InputDevice
                {
                    Id = new DeviceId(key, 0),
                    Name = "Phone",
                    ReadMode = DeviceReadMode.RawJoystick,
                    AxisCount = PhoneLayout.AxisCount,
                    ButtonCount = PhoneLayout.ButtonCount,
                    Snapshot = new InputSnapshot(PhoneLayout.AxisCount, PhoneLayout.ButtonCount, 0),
                },
            };
        });

        if (isNew)
        {
            var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            client.Name = Clean(name);
        }

        client.Device.Name = "Phone: " + client.Name;

        // The same phone reconnecting: drop the old socket.
        var old = client.Socket;
        client.Socket = ws;
        try { old?.Abort(); } catch { /* ignore */ }

        lock (client.Gate)
        {
            client.Buttons = 0;
            client.Latched = 0;
            Array.Clear(client.Axes);
            client.LastPacketTicks = Stopwatch.GetTimestamp();
        }

        // No automatic slot: a reconnecting phone finds the player it already had, a new one has none.
        client.Player = PlayerOf?.Invoke(client.Device.Id) ?? -1;

        client.Device.IsConnected = true;
        await SendAsync(client, JsonSerializer.Serialize(new { t = "welcome", player = client.Player, name = client.Name, lang = _language }));
        ClientsChanged?.Invoke();
        return client;
    }

     private static void ApplyState(PhoneClient client, JsonElement m)
    {
        static float Read(JsonElement e, string name, float min) =>
            e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? Math.Clamp((float)d, min, 1f) : 0f;

        var now = Stopwatch.GetTimestamp();
        var bits = m.TryGetProperty("b", out var b) && b.TryGetInt32(out var value) ? value : 0;

        lock (client.Gate)
        {
            // Newly pressed buttons are latched so a press+release in the same burst still counts.
            var rising = bits & ~client.Buttons;
            if (rising != 0)
            {
                var until = now + Stopwatch.Frequency * MinPressMs / 1000;
                for (var i = 0; i < PhoneLayout.ButtonCount; i++)
                {
                    if ((rising & (1 << i)) != 0) client.LatchUntil[i] = until;
                }
                client.Latched |= rising;
            }

            client.Buttons = bits;
            client.Axes[PhoneLayout.LX] = Read(m, "lx", -1f);
            client.Axes[PhoneLayout.LY] = Read(m, "ly", -1f);
            client.Axes[PhoneLayout.RX] = Read(m, "rx", -1f);
            client.Axes[PhoneLayout.RY] = Read(m, "ry", -1f);
            client.Axes[PhoneLayout.LT] = Read(m, "lt", 0f);
            client.Axes[PhoneLayout.RT] = Read(m, "rt", 0f);
            client.LastPacketTicks = now;
        }
    }

    public async Task KickAsync(string key)
    {
        if (!_clients.TryRemove(key, out var client)) return;

        client.Device.IsConnected = false;
        await SendAsync(client, "{\"t\":\"kicked\"}");
        try { client.Socket?.Abort(); } catch { /* ignore */ }
        client.Socket = null;

        Released?.Invoke(client);
        ClientsChanged?.Invoke();
    }

    public async Task RenameAsync(string key, string name)
    {
        if (!_clients.TryGetValue(key, out var client)) return;

        client.Name = Clean(name);
        client.Device.Name = "Phone: " + client.Name;
        await SendAsync(client, JsonSerializer.Serialize(new { t = "name", name = client.Name }));
        ClientsChanged?.Invoke();
    }

    /// <summary>
    /// Re-reads which player each phone belongs to and tells the phones whose player changed.
    /// Called by AppState whenever the output is applied.
    /// </summary>
    public void RefreshPlayers()
    {
        var changed = false;
        foreach (var client in _clients.Values)
        {
            var player = PlayerOf?.Invoke(client.Device.Id) ?? -1;
            if (player == client.Player) continue;

            client.Player = player;
            changed = true;
            _ = SendAsync(client, JsonSerializer.Serialize(new { t = "player", player }));
        }

        if (changed) ClientsChanged?.Invoke();
    }

    private static async Task SendAsync(PhoneClient client, string text)
    {
        var ws = client.Socket;
        if (ws is not { State: WebSocketState.Open }) return;

        await client.SendLock.WaitAsync();
        try { await RawSendAsync(ws, text); }
        catch { /* socket died; the receive loop cleans up */ }
        finally { client.SendLock.Release(); }
    }

    private static Task RawSendAsync(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Admin pages only from this PC; write actions also need a custom header (blocks CSRF).</summary>
    private static bool Allowed(HttpContext c, bool write)
    {
        var ip = c.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip) && (!write || c.Request.Headers.ContainsKey("X-UniPad"));
    }

    private string Page(string name)
    {
        lock (_pages)
        {
            if (_pages.TryGetValue(name, out var cached)) return cached;
            using var stream = typeof(PhoneServer).Assembly.GetManifestResourceStream("UniPad.Web." + name)
                               ?? throw new FileNotFoundException(name);
            using var reader = new StreamReader(stream);
            return _pages[name] = reader.ReadToEnd();
        }
    }

    private static string Clean(string name)
    {
        var trimmed = new string(name.Where(ch => !char.IsControl(ch)).Take(20).ToArray()).Trim();
        return trimmed.Length == 0 ? "Player" : trimmed;
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    private static string LocalIp()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var props = nic.GetIPProperties();
            if (props.GatewayAddresses.Count == 0) continue; // skips virtual adapters

            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    return addr.Address.ToString();
            }
        }

        return "127.0.0.1";
    }

    public void Dispose()
    {
        foreach (var c in _clients.Values)
        {
            try { c.Socket?.Abort(); } catch { /* ignore */ }
        }

        try { _app?.StopAsync().Wait(2000); } catch { /* ignore */ }
    }
}