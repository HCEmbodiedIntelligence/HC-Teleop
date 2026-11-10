using System;
using System.Collections.Generic;
using System.Linq;

// Discovery is an inventory, never a control grant. Keep selection independent
// of reply arrival order, including when the selected source disappears.
public sealed class DeviceSourceRegistry
{
    public sealed class Source
    {
        public string Ip;
        public int Port;
        public bool Busy;
        public string Owner;
        public double LastSeen;
    }

    private readonly Dictionary<string, Source> sources = new Dictionary<string, Source>();
    private double firstSeen = double.NaN;
    private bool manualSelectionRequired;
    public string SelectedIp { get; private set; }
    public Source[] Sources => sources.Values.OrderBy(s => s.Ip, StringComparer.Ordinal).ToArray();
    public Source Selected => SelectedIp != null && sources.TryGetValue(SelectedIp, out var source) ? source : null;

    public bool Observe(string ip, string response, double now)
    {
        const string available = "PICO_RECEIVER_V1|";
        const string busy = "PICO_RECEIVER_BUSY|";
        bool occupied = response.StartsWith(busy, StringComparison.Ordinal);
        if (!occupied && !response.StartsWith(available, StringComparison.Ordinal)) return false;
        string[] parts = response.Substring(occupied ? busy.Length : available.Length).Split('|');
        if (!int.TryParse(parts[0], out int port) || port < 1 || port > 65535) return false;
        if (!occupied && parts.Length != 1) return false;
        if (double.IsNaN(firstSeen)) firstSeen = now;
        sources[ip] = new Source { Ip = ip, Port = port, Busy = occupied,
            Owner = occupied && parts.Length > 1 ? parts[1] : null, LastSeen = now };
        if (sources.Count > 1 && !manualSelectionRequired)
        {
            SelectedIp = null;
            manualSelectionRequired = true;
        }
        return true;
    }

    public void Tick(double now, double timeout)
    {
        foreach (string ip in sources.Where(p => now - p.Value.LastSeen > timeout).Select(p => p.Key).ToArray())
            sources.Remove(ip);
        // Do not silently select another robot if a user-selected one vanishes.
        if (!manualSelectionRequired && sources.Count == 1 && now - firstSeen >= 1.5 &&
            (SelectedIp == null || sources.ContainsKey(SelectedIp)))
            SelectedIp = sources.Keys.First();
    }

    public bool Select(string ip)
    {
        if (!sources.ContainsKey(ip)) return false;
        SelectedIp = ip;
        manualSelectionRequired = true;
        return true;
    }
}

public static class CameraControlAccess
{
    public static bool IsAllowed(bool transmitting, bool busy, string selectedIp,
        string verifiedServerIp, string ownerIp, bool locked, double age, IEnumerable<string> localIps)
    {
        return transmitting && !busy && !string.IsNullOrEmpty(selectedIp) &&
            selectedIp == verifiedServerIp && locked && age >= 0 && age < 2.5 &&
            !string.IsNullOrEmpty(ownerIp) && localIps.Contains(ownerIp);
    }
}
