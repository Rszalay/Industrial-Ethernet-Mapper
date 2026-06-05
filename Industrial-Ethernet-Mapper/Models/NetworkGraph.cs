namespace Industrial_Ethernet_Mapper.Models;

public enum NodeType
{
    ManagedSwitch,
    Router,
    Modem,
    Dlr,
    UnmanagedSwitch,
    EndDevice,
    Stub
}

public sealed class NetworkGraph
{
    private readonly Dictionary<string, Node> _nodesByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedNames = new(StringComparer.OrdinalIgnoreCase);
    private int _managedCounter;
    private int _dlrCounter;
    private int _unmanagedCounter;
    private int _routerCounter;
    private int _modemCounter;
    private int _endDeviceCounter;

    public IReadOnlyCollection<Node> Nodes => _nodesByKey.Values;
    public List<Link> Links { get; } = new();

    public Node GetOrCreateNode(string? managementIp, string? deviceId, NodeType type, string? suggestion = null)
    {
        string key = GenerateKey(managementIp, deviceId);
        if (!string.IsNullOrWhiteSpace(key) && _nodesByKey.TryGetValue(key, out var existing))
        {
            existing.Type = MergeNodeType(existing.Type, type);
            return existing;
        }

        string name = AllocateNodeName(type, suggestion ?? deviceId ?? managementIp ?? "Node");
        var node = new Node
        {
            Name = name,
            ManagementIp = NormalizeEmpty(managementIp),
            DeviceId = NormalizeEmpty(deviceId),
            Type = type
        };

        if (!string.IsNullOrWhiteSpace(key))
            _nodesByKey[key] = node;
        else
            _nodesByKey[Guid.NewGuid().ToString()] = node;

        return node;
    }

    public void AddLink(Node source, string sourcePort, Node destination, string destinationPort, string protocol)
    {
        sourcePort ??= string.Empty;
        destinationPort ??= string.Empty;
        protocol ??= string.Empty;

        if (Links.Any(link => string.Equals(link.SourceNode, source.Name, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(link.DestinationNode, destination.Name, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(link.SourcePort, sourcePort, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(link.DestinationPort, destinationPort, StringComparison.OrdinalIgnoreCase)
                           && string.Equals(link.Protocol, protocol, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Links.Add(new Link
        {
            SourceNode = source.Name,
            SourcePort = sourcePort,
            DestinationNode = destination.Name,
            DestinationPort = destinationPort,
            Protocol = protocol
        });
    }

    public string ExportToCsv()
    {
        string folder = Path.Combine(Environment.CurrentDirectory, $"NetworkMap_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, "nodes.csv"), BuildNodesCsv());
        File.WriteAllText(Path.Combine(folder, "links.csv"), BuildLinksCsv());
        File.WriteAllText(Path.Combine(folder, "devices.csv"), BuildDevicesCsv());

        return folder;
    }

    private string BuildNodesCsv()
    {
        var lines = new List<string>
        {
            "Name,Type,ManagementIp,DeviceId,Hostname,Manufacturer,Model,PartNumber,SoftwareVersion,SerialNumber,IsStub,Notes,ArpEntryCount,MacTableEntryCount,VlanCount,DeviceCount"
        };

        foreach (var node in Nodes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            string notes = string.Join("; ", node.Notes);
            lines.Add(EscapeCsv(node.Name) + ","
                    + EscapeCsv(node.Type.ToString()) + ","
                    + EscapeCsv(node.ManagementIp) + ","
                    + EscapeCsv(node.DeviceId) + ","
                    + EscapeCsv(node.Hostname) + ","
                    + EscapeCsv(node.Manufacturer) + ","
                    + EscapeCsv(node.Model) + ","
                    + EscapeCsv(node.PartNumber) + ","
                    + EscapeCsv(node.SoftwareVersion) + ","
                    + EscapeCsv(node.SerialNumber) + ","
                    + EscapeCsv(node.IsStub ? "true" : "false") + ","
                    + EscapeCsv(notes) + ","
                    + EscapeCsv(node.ArpTable.Count.ToString()) + ","
                    + EscapeCsv(node.MacTable.Count.ToString()) + ","
                    + EscapeCsv(node.Vlans.Count.ToString()) + ","
                    + EscapeCsv(node.Devices.Count.ToString()));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string BuildLinksCsv()
    {
        var lines = new List<string>
        {
            "SourceNode,SourcePort,DestinationNode,DestinationPort,Protocol"
        };

        foreach (var link in Links)
        {
            lines.Add(EscapeCsv(link.SourceNode) + ","
                    + EscapeCsv(link.SourcePort) + ","
                    + EscapeCsv(link.DestinationNode) + ","
                    + EscapeCsv(link.DestinationPort) + ","
                    + EscapeCsv(link.Protocol));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string BuildDevicesCsv()
    {
        var lines = new List<string>
        {
            "NodeName,Port,MacAddress,IpAddress,Vendor,DeviceType,ProductName,Description"
        };

        foreach (var node in Nodes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var device in node.Devices)
            {
                lines.Add(EscapeCsv(node.Name) + ","
                        + EscapeCsv(device.Port) + ","
                        + EscapeCsv(device.MacAddress) + ","
                        + EscapeCsv(device.IpAddress) + ","
                        + EscapeCsv(device.Vendor) + ","
                        + EscapeCsv(device.DeviceType) + ","
                        + EscapeCsv(device.ProductName) + ","
                        + EscapeCsv(device.Description));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string EscapeCsv(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r')
                ? '"' + value.Replace("\"", "\"\"") + '"'
                : value;

    private static string NormalizeEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string GenerateKey(string? managementIp, string? deviceId)
        => !string.IsNullOrWhiteSpace(managementIp)
            ? $"IP:{managementIp.Trim()}"
            : !string.IsNullOrWhiteSpace(deviceId)
                ? $"ID:{deviceId.Trim()}"
                : string.Empty;

    private string AllocateNodeName(NodeType type, string suggestion)
    {
        string candidate;
        suggestion = NormalizeEmpty(suggestion);

        if (type == NodeType.Dlr)
            candidate = $"DLR{++_dlrCounter}";
        else if (type == NodeType.UnmanagedSwitch)
            candidate = $"UNM{++_unmanagedCounter}";
        else if (type == NodeType.Router)
            candidate = $"RTR{++_routerCounter}";
        else if (type == NodeType.Modem)
            candidate = $"MDM{++_modemCounter}";
        else if (type == NodeType.EndDevice)
            candidate = string.IsNullOrWhiteSpace(suggestion) ? $"DEV{++_endDeviceCounter}" : NormalizeName(suggestion);
        else
            candidate = string.IsNullOrWhiteSpace(suggestion) ? $"SW{++_managedCounter}" : NormalizeName(suggestion);

        while (_usedNames.Contains(candidate))
        {
            candidate = candidate + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        _usedNames.Add(candidate);
        return candidate;
    }

    private static string NormalizeName(string value)
    {
        value = value.Trim();
        value = string.Concat(value.Where(c => !char.IsControl(c)));
        return string.IsNullOrWhiteSpace(value) ? "SW" : value.Replace(' ', '_');
    }

    private static NodeType MergeNodeType(NodeType existing, NodeType incoming)
    {
        // DLR is the most specific — never overwrite it
        if (existing == NodeType.Dlr || incoming == NodeType.Dlr)
            return NodeType.Dlr;

        if (existing == NodeType.ManagedSwitch || incoming == NodeType.ManagedSwitch)
            return NodeType.ManagedSwitch;

        // Managed infrastructure types win over EndDevice
        if (existing == NodeType.EndDevice && incoming != NodeType.Stub)
            return incoming;
        if (incoming == NodeType.EndDevice && existing != NodeType.Stub)
            return existing;

        if (existing == NodeType.Stub)
            return incoming;

        if (incoming == NodeType.Stub)
            return existing;

        if (existing == incoming)
            return existing;

        return incoming;
    }
}

public sealed class Node
{
    public string Name { get; set; } = string.Empty;
    public string ManagementIp { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string SoftwareVersion { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public NodeType Type { get; set; }
    public bool Visited { get; set; }
    public bool IsStub => Type == NodeType.Stub;
    public List<ArpEntry> ArpTable { get; } = new();
    public List<MacTableEntry> MacTable { get; } = new();
    public List<VlanEntry> Vlans { get; } = new();
    public List<Device> Devices { get; } = new();
    public List<string> Notes { get; } = new();
}

public sealed class ArpEntry
{
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string Interface { get; set; } = string.Empty;
}

public sealed class MacTableEntry
{
    public string VlanId { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Port { get; set; } = string.Empty;
}

public sealed class VlanEntry
{
    public string VlanId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public sealed class Device
{
    public string Port { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class Link
{
    public string SourceNode { get; set; } = string.Empty;
    public string SourcePort { get; set; } = string.Empty;
    public string DestinationNode { get; set; } = string.Empty;
    public string DestinationPort { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
}
