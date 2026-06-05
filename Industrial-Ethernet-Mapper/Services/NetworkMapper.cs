using Industrial_Ethernet_Mapper.Models;
using StratixSsh;
using System.Text.RegularExpressions;

namespace Industrial_Ethernet_Mapper.Services;

public sealed class NetworkMapper
{
    private readonly NetworkGraph _graph = new();

    public async Task<NetworkGraph> ScanNetworkAsync(string rootIp, string defaultUsername, string defaultPassword, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootIp))
            throw new ArgumentException("Root IP address is required.", nameof(rootIp));

        var root = _graph.GetOrCreateNode(rootIp, rootIp, NodeType.ManagedSwitch, "RootSwitch");

        if (IsSkipValue(defaultUsername))
        {
            root.Type = NodeType.Stub;
            root.Notes.Add("Root node skipped by user.");
            return _graph;
        }

        await VisitNodeAsync(root, defaultUsername.Trim(), defaultPassword, cancellationToken).ConfigureAwait(false);
        return _graph;
    }

    private async Task VisitNodeAsync(Node node, string defaultUsername, string defaultPassword, CancellationToken cancellationToken)
    {
        if (node.Visited || node.IsStub || string.IsNullOrWhiteSpace(node.ManagementIp))
            return;

        Console.WriteLine($"\n[{node.Name}] Connecting to {node.ManagementIp}...");

        StratixSshSession? session = null;
        try
        {
            session = await TryLoginAsync(node.ManagementIp, defaultUsername, defaultPassword, cancellationToken).ConfigureAwait(false);

            if (session == null)
            {
                Console.WriteLine($"[{node.Name}] Default credentials failed.");
                session = await PromptForCredentialsAsync(node, cancellationToken).ConfigureAwait(false);
                if (session == null)
                {
                    node.Type = NodeType.Stub;
                    node.Notes.Add("Login skipped or failed. Node recorded as stub.");
                    Console.WriteLine($"[{node.Name}] Marked as stub.");
                    return;
                }
            }

            Console.WriteLine($"[{node.Name}] Connected.");
            await ScanNodeAsync(session, node, defaultUsername, defaultPassword, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (session != null)
                await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<StratixSshSession?> TryLoginAsync(string ipAddress, string username, string password, CancellationToken cancellationToken)
    {
        if (IsSkipValue(username))
            return null;

        try
        {
            var session = new StratixSshSession(ipAddress, username, password);
            await session.ConnectAndLoginAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            return null;
        }
    }

    private async Task<StratixSshSession?> PromptForCredentialsAsync(Node node, CancellationToken cancellationToken)
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine($"Unable to connect to {node.ManagementIp} as the default account.");
            Console.Write("Enter fallback username or Skip to create a stub: ");
            string? username = Console.ReadLine()?.Trim();
            if (IsSkipValue(username))
                return null;

            Console.Write($"Password for {username}: ");
            string password = ReadPassword();
            Console.WriteLine();

            var session = await TryLoginAsync(node.ManagementIp, username ?? string.Empty, password, cancellationToken).ConfigureAwait(false);
            if (session != null)
                return session;

            Console.WriteLine("Login failed. Enter another credential set or type Skip.");
        }
    }

    private static bool IsSkipValue(string? text)
        => !string.IsNullOrWhiteSpace(text) && string.Equals(text.Trim(), "Skip", StringComparison.OrdinalIgnoreCase);

    private static string ReadPassword()
    {
        var buffer = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace && buffer.Count > 0)
            {
                buffer.RemoveAt(buffer.Count - 1);
                Console.Write("\b \b");
                continue;
            }

            buffer.Add(key.KeyChar);
            Console.Write('*');
        }

        return new string(buffer.ToArray());
    }

    private async Task ScanNodeAsync(StratixSshSession session, Node node, string defaultUsername, string defaultPassword, CancellationToken cancellationToken)
    {
        node.Visited = true;

        Console.WriteLine($"[{node.Name}] Querying version/identity...");
        string versionOutput = await SendSafelyAsync(session, "show version", cancellationToken).ConfigureAwait(false);
        string hostnameOutput = await SendSafelyAsync(session, "show running-config | include ^hostname", cancellationToken).ConfigureAwait(false);
        string inventoryOutput = await SendSafelyAsync(session, "show inventory", cancellationToken).ConfigureAwait(false);

        ParseShowVersion(node, versionOutput);
        ParseHostname(node, hostnameOutput);
        ParseInventory(node, inventoryOutput);
        Console.WriteLine($"[{node.Name}] Model: {node.Model}  Hostname: {node.Hostname}  S/N: {node.SerialNumber}");

        Console.WriteLine($"[{node.Name}] Reading ARP table...");
        string arpOutput = await SendSafelyAsync(session, "show ip arp", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(arpOutput) || !arpOutput.Contains("Internet", StringComparison.OrdinalIgnoreCase))
            arpOutput = await SendSafelyAsync(session, "show arp", cancellationToken).ConfigureAwait(false);
        ParseArpTable(node, arpOutput);
        Console.WriteLine($"[{node.Name}] ARP entries: {node.ArpTable.Count}");

        Console.WriteLine($"[{node.Name}] Reading MAC address table...");
        string macOutput = await SendSafelyAsync(session, "show mac address-table", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(macOutput) || !macOutput.Contains("Mac Address", StringComparison.OrdinalIgnoreCase))
            macOutput = await SendSafelyAsync(session, "show mac address-table dynamic", cancellationToken).ConfigureAwait(false);
        ParseMacAddressTable(node, macOutput);
        Console.WriteLine($"[{node.Name}] MAC entries: {node.MacTable.Count}");

        Console.WriteLine($"[{node.Name}] Reading VLAN table...");
        string vlanOutput = await SendSafelyAsync(session, "show vlan brief", cancellationToken).ConfigureAwait(false);
        ParseVlanBrief(node, vlanOutput);
        Console.WriteLine($"[{node.Name}] VLANs: {node.Vlans.Count}");

        BuildDeviceList(node);
        Console.WriteLine($"[{node.Name}] Devices on ports: {node.Devices.Count}  — identifying...");
        await IdentifyDevicesAsync(node, cancellationToken).ConfigureAwait(false);
        CommitDeviceNodes(node);
        foreach (var dev in node.Devices)
        {
            string ip = string.IsNullOrEmpty(dev.IpAddress) ? "(no IP)" : dev.IpAddress;
            string type = string.IsNullOrEmpty(dev.DeviceType) ? "?" : dev.DeviceType;
            string product = string.IsNullOrEmpty(dev.ProductName) ? "" : $"  \"{dev.ProductName}\"";
            Console.WriteLine($"[{node.Name}]   Port {dev.Port,-12} {ip,-16}  {dev.Vendor,-26} {type}{product}");
        }

        Console.WriteLine($"[{node.Name}] Running LLDP neighbor discovery...");
        string lldpOutput = await SendSafelyAsync(session, "show lldp neighbors detail", cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[{node.Name}] Running CDP neighbor discovery...");
        string cdpOutput = await SendSafelyAsync(session, "show cdp neighbors detail", cancellationToken).ConfigureAwait(false);

        var neighbors = ParseNeighbors(lldpOutput, "LLDP").Concat(ParseNeighbors(cdpOutput, "CDP")).ToList();
        if (neighbors.Count == 0)
        {
            Console.WriteLine($"[{node.Name}] No LLDP/CDP neighbors detected.");
            if (string.IsNullOrWhiteSpace(lldpOutput))
                Console.WriteLine($"[{node.Name}]   LLDP: no response (protocol may be disabled)");
            if (string.IsNullOrWhiteSpace(cdpOutput))
                Console.WriteLine($"[{node.Name}]   CDP: no response (protocol may be disabled)");
        }
        else
        {
            Console.WriteLine($"[{node.Name}] Neighbors found: {neighbors.Count}");
        }

        // Resolve all neighbor nodes and create links before recursing, so the
        // full graph topology is committed even if a later login fails.
        var neighborNodes = new List<(NeighborInfo Info, Node NeighborNode)>(neighbors.Count);
        foreach (var neighbor in neighbors)
        {
            var candidateType = DetermineNodeType(neighbor);
            var neighborNode = _graph.GetOrCreateNode(neighbor.ManagementAddress, neighbor.DeviceId, candidateType, neighbor.SystemName ?? neighbor.DeviceId ?? neighbor.ManagementAddress);
            neighborNode.Notes.Add($"Discovered by {neighbor.Protocol} from {node.Name}.");
            if (!string.IsNullOrWhiteSpace(neighbor.SystemName))
                neighborNode.Hostname = neighbor.SystemName;
            if (!string.IsNullOrWhiteSpace(neighbor.Manufacturer))
                neighborNode.Manufacturer = neighbor.Manufacturer;
            if (!string.IsNullOrWhiteSpace(neighbor.Platform))
                neighborNode.Model = neighbor.Platform;
            if (!string.IsNullOrWhiteSpace(neighbor.SoftwareVersion))
                neighborNode.SoftwareVersion = neighbor.SoftwareVersion;
            if (!string.IsNullOrWhiteSpace(neighbor.SerialNumber))
                neighborNode.SerialNumber = neighbor.SerialNumber;

            Console.WriteLine($"[{node.Name}]   {neighbor.Protocol} neighbor: {neighborNode.Name} ({neighborNode.ManagementIp})  port {neighbor.LocalPort} -> {neighbor.RemotePort}  type={neighborNode.Type}");
            _graph.AddLink(node, neighbor.LocalPort, neighborNode, neighbor.RemotePort, neighbor.Protocol);
            neighborNodes.Add((neighbor, neighborNode));
        }

        foreach (var (_, neighborNode) in neighborNodes)
        {
            if (!neighborNode.Visited && !neighborNode.IsStub && !string.IsNullOrWhiteSpace(neighborNode.ManagementIp))
                await VisitNodeAsync(neighborNode, defaultUsername, defaultPassword, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> SendSafelyAsync(StratixSshSession session, string command, CancellationToken cancellationToken)
    {
        try
        {
            return await session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ParseShowVersion(Node node, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;

        node.SoftwareVersion = MatchFirstGroup(output, @"Version\s+(?<value>[\S]+)", node.SoftwareVersion);
        node.SerialNumber = MatchFirstGroup(output, @"(?:System serial number|Processor board ID)\s*[:#]?\s*(?<value>\S+)", node.SerialNumber);
        node.Model = MatchFirstGroup(output, @"(?:cisco\s+)?(?<value>[A-Za-z0-9\-]+)\s+bytes of memory", node.Model);
        node.Model = MatchFirstGroup(output, @"Model number\s*:\s*(?<value>\S+)", node.Model);
        node.Manufacturer = DetectManufacturer(output, node.Manufacturer);
    }

    private static void ParseHostname(Node node, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;

        var match = Regex.Match(output, @"^hostname\s+(?<value>\S+)" , RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (match.Success)
            node.Hostname = match.Groups["value"].Value.Trim();
    }

    private static void ParseInventory(Node node, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;

        var match = Regex.Match(output, @"PID\s*:\s*(?<pid>\S+)", RegexOptions.IgnoreCase);
        if (match.Success)
            node.PartNumber = match.Groups["pid"].Value.Trim();

        if (string.IsNullOrWhiteSpace(node.Model))
        {
            var modelMatch = Regex.Match(output, @"PID\s*:\s*(?<pid>\S+)" , RegexOptions.IgnoreCase);
            if (modelMatch.Success)
                node.Model = modelMatch.Groups["pid"].Value.Trim();
        }
    }

    private static void ParseArpTable(Node node, string output)
    {
        node.ArpTable.Clear();
        if (string.IsNullOrWhiteSpace(output))
            return;

        var regex = new Regex(@"(?<ip>\d+\.\d+\.\d+\.\d+)\s+\d+\s+(?<mac>[0-9a-fA-F\.:-]+)\s+\S+\s+(?<iface>\S+)", RegexOptions.IgnoreCase);
        foreach (Match match in regex.Matches(output))
        {
            node.ArpTable.Add(new ArpEntry
            {
                IpAddress = Normalize(match.Groups["ip"].Value),
                MacAddress = NormalizeMac(match.Groups["mac"].Value),
                Interface = Normalize(match.Groups["iface"].Value)
            });
        }
    }

    private static void ParseMacAddressTable(Node node, string output)
    {
        node.MacTable.Clear();
        if (string.IsNullOrWhiteSpace(output))
            return;

        var regex = new Regex(@"^\s*(?<vlan>\d+)\s+(?<mac>[0-9a-fA-F\.:-]+)\s+(?<type>\S+)\s+(?<port>\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        foreach (Match match in regex.Matches(output))
        {
            node.MacTable.Add(new MacTableEntry
            {
                VlanId = Normalize(match.Groups["vlan"].Value),
                MacAddress = NormalizeMac(match.Groups["mac"].Value),
                Type = Normalize(match.Groups["type"].Value),
                Port = Normalize(match.Groups["port"].Value)
            });
        }
    }

    private static void ParseVlanBrief(Node node, string output)
    {
        node.Vlans.Clear();
        if (string.IsNullOrWhiteSpace(output))
            return;

        var regex = new Regex(@"^\s*(?<vlan>\d+)\s+(?<name>\S+)\s+(?<status>\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        foreach (Match match in regex.Matches(output))
        {
            node.Vlans.Add(new VlanEntry
            {
                VlanId = Normalize(match.Groups["vlan"].Value),
                Name = Normalize(match.Groups["name"].Value),
                Status = Normalize(match.Groups["status"].Value)
            });
        }
    }

    private static void BuildDeviceList(Node node)
    {
        node.Devices.Clear();

        var deviceByMac = node.ArpTable
            .Where(arp => !string.IsNullOrEmpty(arp.MacAddress))
            .GroupBy(arp => arp.MacAddress, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var macEntry in node.MacTable)
        {
            if (!deviceByMac.TryGetValue(macEntry.MacAddress, out var arpEntry))
            {
                node.Devices.Add(new Device
                {
                    Port = macEntry.Port,
                    MacAddress = macEntry.MacAddress,
                    IpAddress = string.Empty,
                    Vendor = LookupVendor(macEntry.MacAddress),
                    Description = macEntry.Type
                });
                continue;
            }

            node.Devices.Add(new Device
            {
                Port = macEntry.Port,
                MacAddress = macEntry.MacAddress,
                IpAddress = arpEntry.IpAddress,
                Vendor = LookupVendor(macEntry.MacAddress),
                Description = arpEntry.Interface
            });
        }
    }

    private static async Task IdentifyDevicesAsync(Node node, CancellationToken cancellationToken)
    {
        foreach (var device in node.Devices)
        {
            var (deviceType, productName, vendor) = await DeviceIdentifier.IdentifyAsync(
                device.IpAddress, device.MacAddress, cancellationToken).ConfigureAwait(false);

            device.DeviceType = deviceType;
            device.ProductName = productName;
            if (!string.IsNullOrEmpty(vendor) && vendor != "Unknown")
                device.Vendor = vendor;
        }
    }

    private void CommitDeviceNodes(Node switchNode)
    {
        foreach (var device in switchNode.Devices)
        {
            // Build a human-readable name: prefer product name, then IP, then last 6 of MAC
            string suggestion = !string.IsNullOrWhiteSpace(device.ProductName)
                ? device.ProductName
                : !string.IsNullOrWhiteSpace(device.IpAddress)
                    ? device.IpAddress
                    : device.MacAddress.Replace(".", string.Empty)[^Math.Min(6, device.MacAddress.Replace(".", string.Empty).Length)..];

            // Key by IP if we have one, otherwise by MAC (treated as device ID)
            string? keyIp = string.IsNullOrWhiteSpace(device.IpAddress) ? null : device.IpAddress;
            string? keyId = string.IsNullOrWhiteSpace(device.MacAddress) ? null : device.MacAddress;

            var devNode = _graph.GetOrCreateNode(keyIp, keyId, NodeType.EndDevice, suggestion);
            devNode.ManagementIp = device.IpAddress;
            devNode.DeviceId = device.MacAddress;
            if (string.IsNullOrEmpty(devNode.Manufacturer))
                devNode.Manufacturer = device.Vendor;
            if (string.IsNullOrEmpty(devNode.Model))
                devNode.Model = device.DeviceType;
            if (string.IsNullOrEmpty(devNode.Hostname))
                devNode.Hostname = device.ProductName;

            _graph.AddLink(switchNode, device.Port, devNode, string.Empty, "MAC");
        }
    }

    private static IEnumerable<NeighborInfo> ParseNeighbors(string output, string protocol)
    {
        if (string.IsNullOrWhiteSpace(output))
            return Array.Empty<NeighborInfo>();

        var blocks = Regex.Split(output.Trim(), @"(?=^Device ID:|^------------------------------------------------------------)" , RegexOptions.Multiline);
        var neighbors = new List<NeighborInfo>();

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block))
                continue;

            var info = new NeighborInfo { Protocol = protocol };

            // CDP uses "Device ID:", LLDP uses "Chassis id:" or "System Name:"
            info.DeviceId = MatchFirstGroup(block, @"Device ID:\s*(?<value>.+)");
            if (string.IsNullOrWhiteSpace(info.DeviceId))
                info.DeviceId = MatchFirstGroup(block, @"Chassis id:\s*(?<value>.+)");

            info.SystemName = MatchFirstGroup(block, @"System Name:\s*(?<value>.+)");
            info.Platform = MatchFirstGroup(block, @"Platform:\s*(?<value>.+?)(?:\s*,|$)");
            info.SystemDescription = MatchFirstGroup(block, @"System Description:\s*(?<value>.+)");
            info.ManagementAddress = MatchFirstGroup(block, @"IP\s*:\s*(?<value>\d+\.\d+\.\d+\.\d+)");

            // CDP uses "Port ID (outgoing port):", LLDP uses "Port id:"
            info.RemotePort = MatchFirstGroup(block, @"Port ID \(outgoing port\):\s*(?<value>.+)");
            if (string.IsNullOrWhiteSpace(info.RemotePort))
                info.RemotePort = MatchFirstGroup(block, @"Port id:\s*(?<value>.+)");

            // CDP uses "Interface:", LLDP uses "Local Intf:"
            info.LocalPort = MatchFirstGroup(block, @"Interface:\s*(?<value>[^,]+)");
            if (string.IsNullOrWhiteSpace(info.LocalPort))
                info.LocalPort = MatchFirstGroup(block, @"Local Intf:\s*(?<value>\S+)");

            info.Capabilities = MatchFirstGroup(block, @"Capabilities:\s*(?<value>.+)");
            info.Manufacturer = DetectManufacturer(block, string.Empty);
            info.SoftwareVersion = MatchFirstGroup(block, @"Version\s+(?<value>[\S]+)");
            info.SerialNumber = MatchFirstGroup(block, @"(?:System serial number|Processor board ID)\s*[:#]?\s*(?<value>\S+)");

            if (!string.IsNullOrWhiteSpace(info.DeviceId) || !string.IsNullOrWhiteSpace(info.ManagementAddress)
                || !string.IsNullOrWhiteSpace(info.SystemName))
                neighbors.Add(info);
        }

        return neighbors;
    }

    private static NodeType DetermineNodeType(NeighborInfo neighbor)
    {
        if (!string.IsNullOrWhiteSpace(neighbor.SystemDescription)
            && neighbor.SystemDescription.Contains("Device Level Ring", StringComparison.OrdinalIgnoreCase))
        {
            return NodeType.Dlr;
        }

        if (!string.IsNullOrWhiteSpace(neighbor.DeviceId)
            && neighbor.DeviceId.Contains("DLR", StringComparison.OrdinalIgnoreCase))
        {
            return NodeType.Dlr;
        }

        if (!string.IsNullOrWhiteSpace(neighbor.Platform)
            && neighbor.Platform.Contains("Router", StringComparison.OrdinalIgnoreCase))
        {
            return NodeType.Router;
        }

        if (!string.IsNullOrWhiteSpace(neighbor.Capabilities)
            && neighbor.Capabilities.Contains("Router", StringComparison.OrdinalIgnoreCase))
        {
            return NodeType.Router;
        }

        if (!string.IsNullOrWhiteSpace(neighbor.Platform)
            && neighbor.Platform.Contains("Modem", StringComparison.OrdinalIgnoreCase))
        {
            return NodeType.Modem;
        }

        string desc = neighbor.SystemDescription ?? string.Empty;
        string plat = neighbor.Platform ?? string.Empty;

        if (desc.Contains("Cisco", StringComparison.OrdinalIgnoreCase)
            || plat.Contains("Cisco", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Stratix", StringComparison.OrdinalIgnoreCase)
            || plat.Contains("Stratix", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Rockwell", StringComparison.OrdinalIgnoreCase)
            || plat.Contains("Rockwell", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Allen-Bradley", StringComparison.OrdinalIgnoreCase)
            || plat.Contains("Allen-Bradley", StringComparison.OrdinalIgnoreCase))
        {
            return NodeType.ManagedSwitch;
        }

        return NodeType.UnmanagedSwitch;
    }

    private static string LookupVendor(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return string.Empty;

        var oui = mac.Replace(".", string.Empty).Replace(":", string.Empty).Replace("-", string.Empty).ToUpperInvariant();
        if (oui.Length >= 6)
            oui = oui.Substring(0, 6);

        return oui switch
        {
            "000A95" => "Cisco",
            "001C73" => "Rockwell",
            "001B63" => "HP",
            "001992" => "Siemens",
            _ => "Unknown"
        };
    }

    private static string DetectManufacturer(string output, string currentValue)
    {
        if (string.IsNullOrWhiteSpace(output))
            return currentValue;

        if (output.Contains("Cisco", StringComparison.OrdinalIgnoreCase))
            return "Cisco";

        if (output.Contains("Rockwell", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Allen-Bradley", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Stratix", StringComparison.OrdinalIgnoreCase))
        {
            return "Rockwell/Stratix";
        }

        return currentValue;
    }

    private static string MatchFirstGroup(string text, string pattern, string fallback = "")
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["value"].Value.Trim() : fallback;
    }

    private static string Normalize(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeMac(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var chars = value.Where(c => char.IsLetterOrDigit(c)).ToArray();
        if (chars.Length != 12)
            return value.Trim();

        return string.Join('.', Enumerable.Range(0, 6).Select(i => new string(chars, i * 2, 2))).ToLowerInvariant();
    }

    private sealed class NeighborInfo
    {
        public string Protocol { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string SystemName { get; set; } = string.Empty;
        public string Platform { get; set; } = string.Empty;
        public string SystemDescription { get; set; } = string.Empty;
        public string ManagementAddress { get; set; } = string.Empty;
        public string LocalPort { get; set; } = string.Empty;
        public string RemotePort { get; set; } = string.Empty;
        public string Capabilities { get; set; } = string.Empty;
        public string Manufacturer { get; set; } = string.Empty;
        public string SoftwareVersion { get; set; } = string.Empty;
        public string SerialNumber { get; set; } = string.Empty;
    }
}
