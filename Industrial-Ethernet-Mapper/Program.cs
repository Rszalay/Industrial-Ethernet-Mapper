using Industrial_Ethernet_Mapper.Services;

Console.WriteLine("Industrial Ethernet Network Mapper\n");

Console.Write("Top-level switch IP: ");
string? rootIp = Console.ReadLine()?.Trim();
while (string.IsNullOrWhiteSpace(rootIp))
{
    Console.Write("Top-level switch IP is required. Enter IP: ");
    rootIp = Console.ReadLine()?.Trim();
}

Console.Write("Default read-only username: ");
string? defaultUsername = Console.ReadLine()?.Trim();
if (string.IsNullOrWhiteSpace(defaultUsername))
    defaultUsername = "";

Console.Write("Default read-only password: ");
string defaultPassword = ReadPassword();
Console.WriteLine();

var mapper = new NetworkMapper();
var graph = await mapper.ScanNetworkAsync(rootIp, defaultUsername, defaultPassword, CancellationToken.None);

string exportFolder = graph.ExportToCsv();
Console.WriteLine($"\nExport completed to: {exportFolder}");
Console.WriteLine("Generated files:");
foreach (var file in Directory.GetFiles(exportFolder))
{
    Console.WriteLine($" - {Path.GetFileName(file)}");
}

static string ReadPassword()
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
