using System.Globalization;
using System.Runtime.InteropServices;
using Serilog;
using UniPad.Core.Input;

namespace UniPad.Core.SystemServices;

/// <summary>
/// Maps an SDL device onto its Windows device instance path, which is the identifier HidHide needs.
/// <para>
/// SDL does not expose instance paths, so the HID device tree is enumerated through SetupAPI and
/// matched on VID/PID (plus serial when available). Implemented with direct P/Invoke to avoid
/// pulling in a heavier dependency.
/// </para>
/// </summary>
public static partial class DeviceInstanceResolver
{
    // ---- SetupAPI constants ----
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint SpdrpHardwareId = 0x00000001;

    private static readonly Guid HidClassGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    /// <summary>
    /// Resolves the device instance path for <paramref name="device"/>, or null when it cannot be
    /// determined (in which case the caller should skip cloaking rather than guess).
    /// </summary>
    public static string? Resolve(InputDevice device)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        // SDL's path is already the HID interface path on Windows for HIDAPI-backed devices; it can
        // be converted into the instance-id form HidHide expects.
        if (!string.IsNullOrWhiteSpace(device.Path) && device.Path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            var converted = ConvertInterfacePathToInstanceId(device.Path);
            if (converted is not null)
            {
                return converted;
            }
        }

        try
        {
            return FindByHardwareId(device.VendorId, device.ProductId);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SetupAPI enumeration failed for {Name}", device.Name);
            return null;
        }
    }

    /// <summary>
    /// Rewrites <c>\\?\HID#VID_045E&amp;PID_028E#7&amp;1234abcd&amp;0&amp;0000#{guid}</c> into
    /// <c>HID\VID_045E&amp;PID_028E\7&amp;1234abcd&amp;0&amp;0000</c>.
    /// </summary>
    private static string? ConvertInterfacePathToInstanceId(string interfacePath)
    {
        var trimmed = interfacePath[4..];

        var guidStart = trimmed.IndexOf('{');
        if (guidStart > 0)
        {
            trimmed = trimmed[..guidStart].TrimEnd('#');
        }

        var parts = trimmed.Split('#', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : string.Join('\\', parts);
    }

    /// <summary>Enumerates HID devices and returns the first instance id matching the VID/PID pair.</summary>
    private static string? FindByHardwareId(ushort vendorId, ushort productId)
    {
        if (vendorId == 0 && productId == 0)
        {
            return null;
        }

        var vidPid = string.Format(
            CultureInfo.InvariantCulture,
            "VID_{0:X4}&PID_{1:X4}",
            vendorId,
            productId);

        var classGuid = HidClassGuid;

        // PLATFORM: SetupAPI is a Windows-only device management API.
        var deviceInfoSet = SetupDiGetClassDevs(ref classGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var data = new SpDevinfoData
            {
                CbSize = (uint)Marshal.SizeOf<SpDevinfoData>(),
            };

            for (uint index = 0; SetupDiEnumDeviceInfo(deviceInfoSet, index, ref data); index++)
            {
                var hardwareId = GetStringProperty(deviceInfoSet, ref data, SpdrpHardwareId);
                if (hardwareId is null || !hardwareId.Contains(vidPid, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var instanceId = GetInstanceId(deviceInfoSet, ref data);
                if (instanceId is not null)
                {
                    return instanceId;
                }
            }

            return null;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string? GetStringProperty(IntPtr deviceInfoSet, ref SpDevinfoData data, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(
                deviceInfoSet, ref data, property, out _, buffer, (uint)buffer.Length, out var required))
        {
            return null;
        }

        var length = (int)Math.Min(required, (uint)buffer.Length);
        // REG_MULTI_SZ - take the first entry only.
        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, length);
        var terminator = text.IndexOf('\0');
        return terminator >= 0 ? text[..terminator] : text;
    }

    private static string? GetInstanceId(IntPtr deviceInfoSet, ref SpDevinfoData data)
    {
        // The buffer is sized in UTF-16 characters but passed as bytes, because the LibraryImport
        // source generator cannot marshal char[] without disabling runtime marshalling globally.
        const int CharCapacity = 512;
        var buffer = new byte[CharCapacity * 2];

        if (!SetupDiGetDeviceInstanceId(deviceInfoSet, ref data, buffer, CharCapacity, out var requiredChars))
        {
            return null;
        }

        var charCount = Math.Clamp((int)requiredChars - 1, 0, CharCapacity - 1);
        return System.Text.Encoding.Unicode.GetString(buffer, 0, charCount * 2);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        [Out] byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        [Out] byte[] deviceInstanceId,
        int deviceInstanceIdSize,
        out uint requiredSize);

    [LibraryImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
