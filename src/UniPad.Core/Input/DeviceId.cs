namespace UniPad.Core.Input;

/// <summary>
/// Stable identifier for a physical input device.
/// <para>
/// SDL instance ids change on every reconnect, so they cannot be persisted. Instead a device is
/// keyed by its SDL GUID (which encodes bus/vendor/product/version/crc) plus a port ordinal that
/// disambiguates several identical controllers being plugged in at once.
/// </para>
/// <example>
/// <c>030000005e0400008e02000010010000:0</c>
/// </example>
/// </summary>
/// <param name="Guid">SDL joystick GUID string, lower-case hex, 32 characters.</param>
/// <param name="Port">Zero-based ordinal among devices sharing the same GUID.</param>
public sealed record DeviceId(string Guid, int Port)
{
    /// <summary>Sentinel used by bindings that reference the keyboard rather than a joystick.</summary>
    public static readonly DeviceId Keyboard = new("keyboard", 0);

    /// <summary>Sentinel used by bindings that reference the mouse.</summary>
    public static readonly DeviceId Mouse = new("mouse", 0);

    /// <summary>True when this id refers to a synthetic (non joystick) source.</summary>
    public bool IsSynthetic => Guid is "keyboard" or "mouse";

    /// <inheritdoc />
    public override string ToString() => $"{Guid}:{Port}";

    /// <summary>
    /// Parses the canonical <c>guid:port</c> form. Returns <c>null</c> when the text is malformed
    /// so that a corrupt profile degrades to "unbound" instead of throwing.
    /// </summary>
    public static DeviceId? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var separator = text.LastIndexOf(':');
        if (separator <= 0 || separator == text.Length - 1)
        {
            // Tolerate a bare GUID with no port suffix.
            return new DeviceId(text.Trim(), 0);
        }

        var guid = text[..separator].Trim();
        var portText = text[(separator + 1)..].Trim();

        if (guid.Length == 0 || !int.TryParse(portText, out var port) || port < 0)
        {
            return null;
        }

        return new DeviceId(guid, port);
    }
}
