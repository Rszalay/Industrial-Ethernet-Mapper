using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Industrial_Ethernet_Mapper.Services;

/// <summary>
/// Identifies device type and product name by probing EtherNet/IP (CIP) on port 44818,
/// then falling back to OUI-based vendor/type lookup.
/// </summary>
internal static class DeviceIdentifier
{
    // CIP List Identity request — 24-byte encapsulation header, no data
    private static readonly byte[] ListIdentityRequest =
    [
        0x63, 0x00,                                     // Command: List Identity
        0x00, 0x00,                                     // Length: 0
        0x00, 0x00, 0x00, 0x00,                         // Session handle
        0x00, 0x00, 0x00, 0x00,                         // Status
        0x4D, 0x61, 0x70, 0x70, 0x65, 0x72, 0x00, 0x00, // Sender context ("Mapper\0\0")
        0x00, 0x00, 0x00, 0x00                          // Options
    ];

    public static async Task<(string DeviceType, string ProductName, string Vendor)> IdentifyAsync(
        string? ipAddress, string? macAddress, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(ipAddress))
        {
            var eip = await TryEtherNetIpAsync(ipAddress, cancellationToken).ConfigureAwait(false);
            if (eip.HasValue)
                return eip.Value;
        }

        string oui = NormalizeOui(macAddress);
        if (OuiTable.TryGetValue(oui, out var entry))
            return (entry.DeviceType, string.Empty, entry.Vendor);

        return ("Unknown", string.Empty, "Unknown");
    }

    // ── EtherNet/IP (CIP) probe ───────────────────────────────────────────────

    private static async Task<(string DeviceType, string ProductName, string Vendor)?> TryEtherNetIpAsync(
        string ipAddress, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            using var udp = new UdpClient();
            var endpoint = new IPEndPoint(IPAddress.Parse(ipAddress), 44818);
            await udp.SendAsync(ListIdentityRequest, endpoint, cts.Token).ConfigureAwait(false);

            UdpReceiveResult reply = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return ParseIdentityResponse(reply.Buffer);
        }
        catch
        {
            return null;
        }
    }

    private static (string DeviceType, string ProductName, string Vendor)? ParseIdentityResponse(byte[] data)
    {
        // Encapsulation header = 24 bytes, then item count (2 bytes)
        if (data.Length < 26) return null;

        // Command echo must be 0x0063
        if (data[0] != 0x63 || data[1] != 0x00) return null;
        // Status must be success (0)
        if (BitConverter.ToUInt32(data, 8) != 0) return null;

        int pos = 24;
        int itemCount = BitConverter.ToUInt16(data, pos); pos += 2;
        if (itemCount == 0 || pos + 4 > data.Length) return null;

        // Item type (2) + item length (2)
        pos += 4;

        // Encapsulation version (2)
        if (pos + 2 > data.Length) return null;
        pos += 2;

        // Socket address (16 bytes: family, port, addr, 8 zero bytes)
        if (pos + 16 > data.Length) return null;
        pos += 16;

        if (pos + 10 > data.Length) return null;
        ushort vendorId    = BitConverter.ToUInt16(data, pos); pos += 2;
        ushort deviceType  = BitConverter.ToUInt16(data, pos); pos += 2;
        pos += 2; // product code
        pos += 2; // revision (major + minor)
        pos += 2; // status word
        pos += 4; // serial number

        // Product name: 1-byte length followed by ASCII chars
        if (pos + 1 > data.Length) return null;
        int nameLen = data[pos++];
        string productName = (pos + nameLen <= data.Length)
            ? Encoding.ASCII.GetString(data, pos, nameLen).Trim()
            : string.Empty;

        return (DeviceTypeFromCode(deviceType), productName, VendorFromId(vendorId));
    }

    // ── CIP device type codes (ODVA spec table) ───────────────────────────────

    private static string DeviceTypeFromCode(ushort code) => code switch
    {
        0x00 => "Generic Device",
        0x02 => "VFD (AC Drive)",
        0x03 => "Motor Overload",
        0x04 => "Limit Switch",
        0x05 => "Proximity Sensor",
        0x06 => "Photoelectric Sensor",
        0x07 => "Discrete I/O",
        0x08 => "Analog I/O",
        0x09 => "Resolver",
        0x0C => "Communications Adapter",
        0x0E => "PLC",
        0x10 => "Position Controller",
        0x13 => "VFD (DC Drive)",
        0x15 => "Contactor",
        0x16 => "Motor Starter",
        0x17 => "Soft Starter",
        0x18 => "HMI",
        0x1A => "Mass Flow Controller",
        0x1B => "Pneumatic Valve",
        0x1E => "Process Control Valve",
        0x23 => "Encoder",
        0x24 => "Safety I/O",
        0x26 => "CIP Motion Drive",
        0x2B => "Safety Analog I/O",
        0x2D => "Managed Switch",
        0x2F => "Safety Drive",
        0x30 => "Safety Drive",
        0x31 => "CIP Motion Encoder",
        0x32 => "CIP Motion Converter",
        0x33 => "CIP Motion I/O Actuator",
        0x34 => "CIP Motion I/O Sensor",
        _ => $"Industrial Device (CIP 0x{code:X2})"
    };

    // ── CIP vendor IDs (ODVA vendor list, subset) ─────────────────────────────

    private static string VendorFromId(ushort id) => id switch
    {
        1   => "Rockwell Automation",
        3   => "Honeywell",
        7   => "Hitachi",
        8   => "ABB",
        10  => "Omron",
        24  => "Molex",
        26  => "Turck",
        29  => "ABB",
        36  => "Numatics",
        41  => "Danfoss",
        52  => "Bosch Rexroth",
        71  => "Banner Engineering",
        78  => "Siemens",
        90  => "Eaton",
        96  => "IDEC",
        105 => "SEW-Eurodrive",
        115 => "Baldor Electric",
        144 => "Keyence",
        179 => "SICK AG",
        193 => "Schneider Electric",
        206 => "Phoenix Contact",
        234 => "Yaskawa",
        258 => "Beckhoff",
        267 => "Belden",
        274 => "Cognex",
        326 => "Pilz",
        333 => "Lenze",
        _   => $"Vendor {id}"
    };

    // ── OUI fallback table ────────────────────────────────────────────────────

    private static string NormalizeOui(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return string.Empty;
        var chars = mac.Where(char.IsLetterOrDigit).ToArray();
        return chars.Length >= 6 ? new string(chars, 0, 6).ToUpperInvariant() : string.Empty;
    }

    private record OuiEntry(string Vendor, string DeviceType);

    private static readonly Dictionary<string, OuiEntry> OuiTable =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // Rockwell Automation / Allen-Bradley
        ["000A00"] = new("Rockwell Automation", "Industrial Device"),
        ["001C73"] = new("Rockwell Automation", "Industrial Device"),
        ["001F6C"] = new("Rockwell Automation", "Industrial Device"),
        ["000E26"] = new("Rockwell Automation", "Industrial Device"),
        ["00809F"] = new("Rockwell Automation", "Industrial Device"),
        ["00E0B1"] = new("Rockwell Automation", "Industrial Device"),
        ["1831BF"] = new("Rockwell Automation", "Industrial Device"),
        // Cisco / Stratix
        ["000A95"] = new("Cisco", "Network Switch"),
        ["001A2F"] = new("Cisco", "Network Switch"),
        ["001B8F"] = new("Cisco", "Network Switch"),
        ["001C57"] = new("Cisco", "Network Switch"),
        ["001D45"] = new("Cisco", "Network Switch"),
        ["0022BD"] = new("Cisco", "Network Switch"),
        ["002564"] = new("Cisco", "Network Switch"),
        ["001120"] = new("Cisco", "Network Device"),
        ["001DA1"] = new("Cisco", "Network Device"),
        // Siemens
        ["001992"] = new("Siemens", "Industrial Device"),
        ["0008E7"] = new("Siemens", "Industrial Device"),
        ["00197C"] = new("Siemens", "Industrial Device"),
        ["002606"] = new("Siemens", "Industrial Device"),
        ["001CD5"] = new("Siemens", "Industrial Device"),
        ["000E8C"] = new("Siemens", "Industrial Device"),
        // Schneider Electric / Modicon
        ["00800F"] = new("Schneider Electric", "PLC"),
        ["0080F4"] = new("Schneider Electric", "Industrial Device"),
        ["00A0F4"] = new("Schneider Electric", "Industrial Device"),
        ["00E0DB"] = new("Schneider Electric", "Industrial Device"),
        // ABB
        ["001A98"] = new("ABB", "Industrial Device"),
        ["080060"] = new("ABB", "Industrial Device"),
        ["00E0E3"] = new("ABB", "VFD"),
        // Omron
        ["000050"] = new("Omron", "PLC"),
        ["00C086"] = new("Omron", "Industrial Device"),
        // Phoenix Contact
        ["00A047"] = new("Phoenix Contact", "Industrial Device"),
        ["A44C11"] = new("Phoenix Contact", "Industrial Device"),
        // Beckhoff
        ["00018E"] = new("Beckhoff", "Industrial Device"),
        // Moxa (industrial networking)
        ["00907A"] = new("Moxa", "Industrial Network Device"),
        ["0090E8"] = new("Moxa", "Industrial Network Device"),
        // WAGO
        ["00306E"] = new("WAGO", "Industrial Device"),
        // Turck
        ["0030DE"] = new("Turck", "Industrial Device"),
        // B&R Automation
        ["00C025"] = new("B&R Automation", "Industrial Device"),
        // SICK (sensors)
        ["0030F1"] = new("SICK AG", "Sensor"),
        ["00124B"] = new("SICK AG", "Sensor"),
        // Keyence
        ["001B5E"] = new("Keyence", "Sensor"),
        // Advantech (HMI/IPC)
        ["00D0C9"] = new("Advantech", "HMI/IPC"),
        ["0050C2"] = new("Advantech", "HMI/IPC"),
        // HP / Aruba
        ["001B63"] = new("HP", "PC/Server"),
        ["001CC4"] = new("HP", "PC/Server"),
        ["3CD92B"] = new("HP", "PC/Server"),
        // Dell
        ["000874"] = new("Dell", "PC/Server"),
        ["001E4F"] = new("Dell", "PC/Server"),
        ["5CF9DD"] = new("Dell", "PC/Server"),
        // Intel (PC NICs)
        ["001B21"] = new("Intel", "PC/Server"),
        ["7845C4"] = new("Intel", "PC/Server"),
        ["A4C361"] = new("Intel", "PC/Server"),
        ["001320"] = new("Intel", "PC/Server"),
        ["E4B97A"] = new("Intel", "PC/Server"),
        // Apple
        ["000A27"] = new("Apple", "PC/Mobile"),
        ["001124"] = new("Apple", "PC/Mobile"),
        ["8C8590"] = new("Apple", "PC/Mobile"),
        // Microsoft / Surface
        ["000D3A"] = new("Microsoft", "PC/Server"),
        ["7C1E52"] = new("Microsoft", "PC/Server"),
        // VMware
        ["000C29"] = new("VMware", "Virtual Machine"),
        ["005056"] = new("VMware", "Virtual Machine"),
    };
}
