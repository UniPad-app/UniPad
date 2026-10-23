using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using UniPad.App.Localization;
using UniPad.App.Services;
using UniPad.Core.Network;
using Avalonia.Media;

namespace UniPad.App.ViewModels;

/// <summary>One row in the connected phones list.</summary>
public sealed partial class PhoneClientRow : ViewModelBase
{
    private readonly PhonesViewModel _owner;

    [ObservableProperty]
    private string _playerText = "-";

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffline))]
    private bool _isOnline;

    [ObservableProperty]
    private string _latencyText = "-";

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isConfirmingKick;

    /// <summary>Colour of the player this phone drives, or null while it waits for one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayer))]
    private IBrush? _playerBrush;

    /// <summary>True while the phone is assigned to a player.</summary>
    public bool HasPlayer => PlayerBrush is not null;

    internal PhoneClientRow(PhonesViewModel owner, string key)
    {
        _owner = owner;
        Key = key;
    }

    /// <summary>Server-side key of the phone, "phone-xxxxxxxx".</summary>
    public string Key { get; }

    /// <summary>True while the phone's socket is closed but its slot is still held for a reconnect.</summary>
    public bool IsOffline => !IsOnline;

    /// <summary>Copies the latest server state into the row. The generated setters only notify on change.</summary>
    internal void Update(PhoneClient client)
    {
        PlayerText = client.Player >= 0
            ? (client.Player + 1).ToString(CultureInfo.InvariantCulture)
            : "-";

        PlayerBrush = client.Player >= 0 ? PlayerColors.Get(client.Player) : null;

        Name = client.Name;
        IsOnline = client.Device.IsConnected;

        LatencyText = IsOnline
            ? Strings.Isolate($"{client.RttMs.ToString(CultureInfo.InvariantCulture)} {Strings.Get("unit.ms")}")
            : "-";
    }

    [RelayCommand]
    private void BeginRename()
    {
        EditName = Name;
        IsConfirmingKick = false;
        IsEditing = true;
    }

    [RelayCommand]
    private async Task SaveNameAsync()
    {
        var name = EditName.Trim();
        IsEditing = false;

        if (name.Length == 0 || string.Equals(name, Name, StringComparison.Ordinal))
        {
            return;
        }

        await _owner.RenameAsync(this, name);
    }

    [RelayCommand]
    private void CancelRename() => IsEditing = false;

    /// <summary>
    /// Two-step kick: the first click arms the button, the second one disconnects. Replaces the
    /// browser confirm() of the web admin page without opening a dialog.
    /// </summary>
    [RelayCommand]
    private async Task KickAsync()
    {
        if (!IsConfirmingKick)
        {
            IsConfirmingKick = true;
            return;
        }

        IsConfirmingKick = false;
        await _owner.KickAsync(this);
    }

    [RelayCommand]
    private void CancelKick() => IsConfirmingKick = false;
}

/// <summary>
/// View model for the Phones page: the join QR code for the local Wi-Fi phone controller, the
/// server state and the list of connected phones with rename and kick.
/// <para>
/// This is the in-app replacement for the /admin web page. Everything that page could do is here,
/// and the page itself still works for anyone who prefers a browser.
/// </para>
/// </summary>
public sealed partial class PhonesViewModel : ViewModelBase
{
    /// <summary>The UI timer calls <see cref="Tick"/> at roughly 6 Hz; latency is re-read every ~1.5 s.</summary>
    private const int TicksPerRefresh = 9;

    private readonly AppState _state;
    private string? _qrUrl;
    private int _tickCounter;

    [ObservableProperty]
    private Bitmap? _qrImage;

    [ObservableProperty]
    private string _joinUrl = string.Empty;

    [ObservableProperty]
    private bool _isServerRunning;

    [ObservableProperty]
    private string _serverStatus = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoClients))]
    private bool _hasClients;

    [ObservableProperty]
    private bool _isConfirmingKickAll;

    /// <summary>Creates the Phones page view model.</summary>
    public PhonesViewModel(AppState state)
    {
        _state = state;

        // Status, summary and latency captions are composed in code, so they follow the language by hand.
        Strings.Instance.LanguageChanged += Refresh;

        Refresh();
    }

    /// <summary>Every phone the server knows about, ordered by player slot.</summary>
    public ObservableCollection<PhoneClientRow> Clients { get; } = [];

    /// <summary>True when the empty-state hint should be shown.</summary>
    public bool HasNoClients => !HasClients;

    /// <summary>Address of the browser admin page. It only answers requests from this PC.</summary>
    public string AdminUrl =>
        $"http://localhost:{_state.Phones.Port.ToString(CultureInfo.InvariantCulture)}/admin";

    /// <summary>Called from the UI timer while the Phones page is visible.</summary>
    public void Tick()
    {
        if (++_tickCounter < TicksPerRefresh)
        {
            return;
        }

        _tickCounter = 0;
        Refresh();
    }

    /// <summary>
    /// Re-reads the server: running state, join link (the PC's Wi-Fi address can change), QR code
    /// and the phone list. Rows are updated in place so a row being renamed keeps its text box.
    /// </summary>
    public void Refresh()
    {
        var server = _state.Phones;

        IsServerRunning = server.IsRunning;
        ServerStatus = IsServerRunning
            ? $"{Strings.Get("phones.serverRunning")} {Strings.Isolate(server.Port.ToString(CultureInfo.InvariantCulture))}"
            : Strings.Get("phones.serverStopped");

        if (IsServerRunning)
        {
            var url = server.JoinUrl;
            JoinUrl = url;

            if (!string.Equals(url, _qrUrl, StringComparison.Ordinal))
            {
                _qrUrl = url;
                RenderQr(server);
            }
        }
        else
        {
            JoinUrl = string.Empty;
            _qrUrl = null;
            ReplaceQr(null);
        }

        SyncRows(server.Clients);
    }

    private void SyncRows(IReadOnlyList<PhoneClient> clients)
    {
        for (var i = Clients.Count - 1; i >= 0; i--)
        {
            var key = Clients[i].Key;
            if (!clients.Any(c => c.Key == key))
            {
                Clients.RemoveAt(i);
            }
        }

        for (var i = 0; i < clients.Count; i++)
        {
            var client = clients[i];
            var index = IndexOf(client.Key);
            PhoneClientRow row;

            if (index < 0)
            {
                row = new PhoneClientRow(this, client.Key);
                Clients.Insert(i, row);
            }
            else
            {
                row = Clients[index];
                if (index != i)
                {
                    Clients.Move(index, i);
                }
            }

            row.Update(client);
        }

        var online = clients.Count(c => c.Device.IsConnected);
        HasClients = clients.Count > 0;

        if (!HasClients)
        {
            IsConfirmingKickAll = false;
        }

        Summary =
            $"{Strings.Get("phones.online")}: " +
            Strings.Isolate(online.ToString(CultureInfo.InvariantCulture)) +
            " | " +
            $"{Strings.Get("phones.total")}: " +
            Strings.Isolate($"{clients.Count.ToString(CultureInfo.InvariantCulture)}/{PhoneServer.MaxClients.ToString(CultureInfo.InvariantCulture)}");
    }

    private int IndexOf(string key)
    {
        for (var i = 0; i < Clients.Count; i++)
        {
            if (Clients[i].Key == key)
            {
                return i;
            }
        }

        return -1;
    }

    private void RenderQr(PhoneServer server)
    {
        try
        {
            using var stream = new MemoryStream(server.CreateQrPng());
            ReplaceQr(new Bitmap(stream));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not render the phone QR code");
            ReplaceQr(null);
        }
    }

    private void ReplaceQr(Bitmap? bitmap)
    {
        var old = QrImage;
        QrImage = bitmap;

        if (!ReferenceEquals(old, bitmap))
        {
            old?.Dispose();
        }
    }

    /// <summary>Invalidates the current QR code. Phones already connected stay connected.</summary>
    [RelayCommand]
    private void NewQr()
    {
        _state.Phones.RegenerateToken();
        _qrUrl = null;
        Refresh();

        _state.ReportStatus(
            "phones.newQrDone", null, "A new QR code was created; the previous one no longer works.");
    }

    /// <summary>Opens the browser version of this page.</summary>
    [RelayCommand]
    private void OpenAdmin()
    {
        var url = AdminUrl;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Url}", url);
            _state.ReportStatus("msg.openPathFailed", url, $"Could not open: {url}");
        }
    }

    /// <summary>Two-step "disconnect every phone".</summary>
    [RelayCommand]
    private async Task KickAllAsync()
    {
        if (!IsConfirmingKickAll)
        {
            IsConfirmingKickAll = true;
            return;
        }

        IsConfirmingKickAll = false;

        foreach (var client in _state.Phones.Clients)
        {
            await _state.Phones.KickAsync(client.Key);
        }

        Refresh();
        _state.ReportStatus("phones.allKicked", null, "Every phone was disconnected.");
    }

    [RelayCommand]
    private void CancelKickAll() => IsConfirmingKickAll = false;

    /// <summary>Called by the view after the join link was put on the clipboard.</summary>
    public void ReportCopied() =>
        _state.ReportStatus("phones.copied", null, "Link copied to the clipboard.");

    internal async Task KickAsync(PhoneClientRow row)
    {
        var name = row.Name;
        await _state.Phones.KickAsync(row.Key);
        Refresh();
        _state.ReportStatus("phones.kicked", name, $"Phone disconnected: {name}");
    }

    internal async Task RenameAsync(PhoneClientRow row, string name)
    {
        await _state.Phones.RenameAsync(row.Key, name);
        Refresh();
        _state.ReportStatus("phones.renamed", row.Name, $"Phone renamed: {row.Name}");
    }
}
