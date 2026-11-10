// Compile together with Assets/DeviceSourceRegistry.cs; no Unity or XR needed.
using System;
using System.Linq;

public static class DeviceSourcesRegression
{
    private static int assertions;
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        assertions++;
    }

    public static int Main()
    {
        var registry = new DeviceSourceRegistry();
        Check(!registry.Observe("10.0.0.1", "bad", 0), "ignore unrelated datagrams");
        foreach (string packet in new[] { "PICO_RECEIVER_V1|0", "PICO_RECEIVER_V1|65536", "PICO_RECEIVER_BUSY|bad", "PICO_RECEIVER_V1|5005|junk" })
            Check(!registry.Observe("10.0.0.1", packet, 0), "reject malformed discovery: " + packet);
        registry.Observe("10.0.0.1", "PICO_RECEIVER_V1|5005", 0);
        registry.Tick(1.4, 6);
        Check(registry.Selected == null, "collect replies before single-source auto selection");
        registry.Tick(1.6, 6);
        Check(registry.Selected.Ip == "10.0.0.1", "single source auto selects");
        registry.Observe("10.0.0.1", "PICO_RECEIVER_BUSY|5005|10.0.0.90", 2);
        Check(registry.Selected.Busy && registry.Selected.Owner == "10.0.0.90", "busy source is visible without granting control");
        registry.Observe("10.0.0.1", "PICO_RECEIVER_V1|5005", 3);
        Check(!registry.Selected.Busy && registry.Selected.Owner == null, "release updates selected source availability");
        registry.Observe("10.0.0.2", "PICO_RECEIVER_V1|5006", 3);
        registry.Tick(4, 6);
        Check(registry.Selected == null, "second source clears automatic selection");
        Check(!registry.Select("10.0.0.99"), "cannot select an undiscovered source");
        Check(registry.Select("10.0.0.2"), "explicit dropdown selection accepted");
        for (int i = 0; i < 20; i++)
        {
            registry.Observe("10.0.0.1", "PICO_RECEIVER_V1|5005", 4 + i);
            registry.Observe("10.0.0.2", "PICO_RECEIVER_V1|5006", 4 + i);
            registry.Tick(4 + i, 6);
            Check(registry.Selected.Ip == "10.0.0.2", "reply order cannot rotate explicit selection");
        }
        registry.Observe("10.0.0.1", "PICO_RECEIVER_V1|5005", 30);
        registry.Tick(30, 6);
        Check(registry.Selected == null && registry.Sources.Length == 1, "offline selected source does not switch robot");
        registry.Observe("10.0.0.2", "PICO_RECEIVER_V1|5006", 31);
        registry.Tick(31, 6);
        Check(registry.Selected.Ip == "10.0.0.2", "selected source reconnects when it returns");
        foreach (bool reverse in new[] { false, true })
        {
            var two = new DeviceSourceRegistry();
            foreach (string ip in reverse ? new[] { "10.0.0.2", "10.0.0.1" } : new[] { "10.0.0.1", "10.0.0.2" })
                two.Observe(ip, "PICO_RECEIVER_V1|5005", 0);
            two.Tick(2, 6);
            Check(two.Selected == null, "multiple initial replies require choice regardless of arrival order");
            Check(two.Sources.Select(s => s.Ip).SequenceEqual(new[] { "10.0.0.1", "10.0.0.2" }), "dropdown order is stable");
        }
        var auto = new DeviceSourceRegistry();
        auto.Observe("10.0.0.1", "PICO_RECEIVER_V1|5005", 0);
        auto.Tick(2, 6);
        auto.Tick(10, 6);
        auto.Observe("10.0.0.2", "PICO_RECEIVER_V1|5005", 11);
        auto.Tick(12, 6);
        Check(auto.Selected == null, "lost auto-selected robot is not replaced by another robot");

        foreach (bool transmitting in new[] { false, true })
        foreach (bool busy in new[] { false, true })
        foreach (bool locked in new[] { false, true })
        foreach (bool own in new[] { false, true })
        {
            bool allowed = CameraControlAccess.IsAllowed(transmitting, busy, "robot", "robot",
                own ? "10.0.0.90" : "10.0.0.91", locked, 0.2, new[] { "10.0.0.90" });
            Check(allowed == (transmitting && !busy && locked && own), "camera requires actual owner and active transmission");
        }
        foreach (double age in new[] { -1.0, 2.5, 3.0, double.PositiveInfinity, double.NaN })
            Check(!CameraControlAccess.IsAllowed(true, false, "robot", "robot", "local", true, age, new[] { "local" }), "stale or invalid ownership is denied");
        Check(!CameraControlAccess.IsAllowed(true, false, "new", "old", "local", true, 0, new[] { "local" }), "previous source grant cannot authorize new source");
        Check(!CameraControlAccess.IsAllowed(true, false, "", "", "local", true, 0, new[] { "local" }), "no selected source denies camera");
        Check(!CameraControlAccess.IsAllowed(true, false, "robot", "robot", null, true, 0, new[] { "local" }), "unowned source is not a control grant");
        Console.WriteLine("PASS: " + assertions + " device-source and camera-access assertions");
        return 0;
    }
}
