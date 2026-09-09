// Run with Unity's bundled Mono runtime. Pass a freshly compiled project DLL
// and Unity's Editor/Data/Managed/UnityEngine directory as arguments.
using System;
using System.IO;
using System.Reflection;


public static class SafetyStateRegression
{
    private static object sender;
    private static Type senderType;
    private static Assembly project;
    private static int assertions;

    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string path = Path.Combine(args[1], new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        project = Assembly.LoadFrom(args[0]);
        senderType = project.GetType("MiddlewareSafetyState", true);
        // Avoid creating Unity objects or starting networking outside the player.
        sender = Activator.CreateInstance(senderType);

        Send("safety_stop", 10, "no VR pose data for 201 ms");
        Check((bool)Get("IsEmergencyStopped"), "timeout latches stop");
        Check((string)Get("EmergencyStopMessage") == "VR 数据超时：201 ms", "localized timeout reason");
        Send("recording_status", 11, null);
        Check((bool)Get("IsEmergencyStopped"), "recording does not clear stop");
        Send("replay_reset", 12, null);
        Check((bool)Get("IsEmergencyStopped"), "replay reset does not clear stop");
        Send("safety_resume", 9, null);
        Check((bool)Get("IsEmergencyStopped"), "old resume cannot clear newer stop");
        Send("safety_resume", 13, null);
        Check(!(bool)Get("IsEmergencyStopped"), "confirmed resume clears stop");
        Check(Get("EmergencyStopReason") == null, "resume clears reason");
        Send("safety_stop", 10, "old stop");
        Check(!(bool)Get("IsEmergencyStopped"), "old stop cannot replace newer resume");
        Send("safety_stop", 14, "VR pose sample stale for 250 ms", true);
        Check((string)Get("EmergencyStopMessage") == "VR 位姿采样停滞：250 ms", "legacy type and stale-sample reason");
        Send("safety_stop", 14, "VR pose sample stale for 250 ms");
        Check((bool)Get("IsEmergencyStopped"), "duplicate stop remains latched");
        Send("safety_stop", 15, "head tracking invalid");
        Check((string)Get("EmergencyStopMessage") == "头显追踪丢失", "tracking loss reason");
        Send("safety_stop", 16, null);
        Check((string)Get("EmergencyStopMessage") == "中间件触发急停", "missing reason fallback");
        Send("replay_completed", 17, null, false, true);
        Send("safety_resume", 18, null);
        Check(!(bool)Get("IsEmergencyStopped"), "resume after replay clears safety latch");
        senderType.GetMethod("Apply").Invoke(sender, new object[] { null });
        Check(!(bool)Get("IsEmergencyStopped"), "null event leaves state unchanged");
        Console.WriteLine("PASS: " + assertions + " safety-state assertions");
        return 0;
    }

    private static object Get(string name) => senderType.GetProperty(name).GetValue(sender);

    private static void Send(string kind, double timestamp, string reason,
        bool legacy = false, bool requiresReset = false)
    {
        Type eventType = project.GetType("MiddlewareEventJson", true);
        Type payloadType = project.GetType("MiddlewareEventPayload", true);
        object evt = Activator.CreateInstance(eventType);
        object payload = Activator.CreateInstance(payloadType);
        eventType.GetField(legacy ? "type" : "kind").SetValue(evt, kind);
        eventType.GetField("timestamp").SetValue(evt, timestamp);
        payloadType.GetField("reason").SetValue(payload, reason);
        payloadType.GetField("requires_reset").SetValue(payload, requiresReset);
        eventType.GetField("payload").SetValue(evt, payload);
        senderType.GetMethod("Apply")
            .Invoke(sender, new[] { evt });
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        assertions++;
    }
}
