# Industrial Ethernet Network Mapper

A command-line tool that automatically maps an industrial Ethernet network by walking managed switches via SSH, discovering topology through LLDP/CDP, and identifying connected end devices via EtherNet/IP (CIP). Results are exported to CSV files formatted for import into Microsoft Visio.

---

## How It Works

1. You provide the IP address of the top-level managed switch and read-only credentials
2. The tool SSHs in (Cisco IOS CLI — Stratix 5200/5700, Stratix 5400, or any Cisco IOS switch)
3. For each switch it collects: ARP table, MAC address table, VLAN list, inventory, and LLDP/CDP neighbors
4. Each LLDP/CDP neighbor becomes a new node and is recursively visited the same way
5. End devices (PLCs, VFDs, HMIs, PCs, etc.) on each port are identified by probing EtherNet/IP (CIP) on port 44818 UDP — this returns the exact product name and ODVA device type code. If a device doesn't answer CIP, the MAC OUI is used to identify the vendor and device category
6. All nodes and links are committed to a graph (handles rings and multi-path topologies — not limited to trees)
7. Results are exported to three CSV files in a timestamped folder

---

## Prerequisites

- Windows x64 (the `publish/` folder contains a self-contained build — no .NET install required)
- Network access to the managed switches via SSH (port 22)
- A read-only account on the switches (Cisco/Stratix default: `admin` / `switch`)

---

## Running

```
publish\Industrial-Ethernet-Mapper.exe
```

You will be prompted for:

| Prompt | Description |
|---|---|
| Top-level switch IP | IP address of the switch to start from |
| Default read-only username | Used on all switches; leave blank to be prompted per-switch |
| Default read-only password | Masked input |

If the default credentials fail on a downstream switch, the tool will prompt you for alternate credentials for that switch, or let you skip it (it will be recorded as a stub node).

---

## Output

Each run creates a timestamped folder (e.g. `NetworkMap_20260605_143022\`) containing:

| File | Contents |
|---|---|
| `nodes.csv` | One row per switch or device — name, type, IP, hostname, model, part number, serial, firmware |
| `links.csv` | One row per connection — source node/port → destination node/port, discovery protocol (LLDP / CDP / MAC) |
| `devices.csv` | One row per end device — switch port, MAC, IP, vendor, device type, product name |

### Node types

| Name | Description |
|---|---|
| `ManagedSwitch` | Cisco or Stratix managed switch discovered via LLDP/CDP |
| `Router` | Router or layer-3 device |
| `Modem` | Modem or WAN gateway |
| `Dlr` | Device Level Ring supervisor |
| `UnmanagedSwitch` | Unmanaged switch (no LLDP/CDP response) |
| `EndDevice` | PLC, VFD, HMI, PC, sensor, or other end device on a port |
| `Stub` | Node that could not be logged into — recorded with whatever info was available |

### Device types (from EtherNet/IP CIP identity)

PLC, VFD (AC Drive), VFD (DC Drive), HMI, Encoder, Safety I/O, Discrete I/O, Motor Starter, Soft Starter, Communications Adapter, Managed Switch, and more per the ODVA device type table.

---

## Importing to Visio

1. Open Visio → **File → New → Network Diagram**
2. Use **Data → Link Data to Shapes** or the built-in **Import** wizard
3. Import `nodes.csv` as the shape data source (use `Name` as the key)
4. Import `links.csv` to draw connections between shapes

---

## Building from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download) and the [stratix-ssh](https://github.com/Rszalay/stratix-ssh) library checked out at the same directory level:

```
repos\
  Industrial-Ethernet-Mapper\
  stratix-ssh\
```

```
cd Industrial-Ethernet-Mapper\Industrial-Ethernet-Mapper
dotnet build
```

To publish a new release build:

```
dotnet publish /p:PublishProfile=win-x64-folder
```

Output goes to `repos\publish\`.

---

## Supported Hardware

- Allen-Bradley Stratix 5200, 5400, 5700 (Cisco IOS)
- Cisco Catalyst, IE series (industrial Ethernet)
- Any managed switch running Cisco IOS CLI with SSH and LLDP/CDP enabled
- End devices: any device supporting EtherNet/IP (CIP) — CompactLogix, ControlLogix, PowerFlex drives, PanelView HMIs, Kinetix drives, and devices from Siemens, Schneider, ABB, Omron, Beckhoff, and others
