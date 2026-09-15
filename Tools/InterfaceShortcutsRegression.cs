// Compile as a standalone console program and run with Unity's bundled Mono.
// Arguments: freshly compiled project DLL, Editor/Data/Managed/UnityEngine.
using System;
using System.IO;
using System.Reflection;

public static class InterfaceShortcutsRegression
{
    private static Type gestureType;
    private static MethodInfo sampleMethod;
    private static MethodInfo disarmMethod;
    private static MethodInfo removeClickMethod;
    private static int assertions;

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: InterfaceShortcutsRegression.exe <project.dll> <UnityEngine directory>");
            return 2;
        }

        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
            {
                string path = Path.Combine(args[1], new AssemblyName(eventArgs.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            Assembly project = Assembly.LoadFrom(args[0]);
            gestureType = project.GetType("BothThumbsticksGesture", true);
            sampleMethod = gestureType.GetMethod("Sample");
            disarmMethod = gestureType.GetMethod("Disarm");
            removeClickMethod = gestureType.GetMethod("RemoveClick");

            CheckStartupAndChordRearming();
            CheckTrackingLoss();
            CheckFocusDisarm();
            CheckProtocolMask();
            Console.WriteLine("PASS: " + assertions + " interface-shortcut assertions; all 65,536 ushort masks checked");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void CheckStartupAndChordRearming()
    {
        object gesture = Activator.CreateInstance(gestureType);
        Check(!Sample(gesture, true, true), "sticks held at startup cannot toggle");
        Check(!Sample(gesture, true, false), "partial release at startup does not arm");
        Check(!Sample(gesture, true, true), "startup requires both sticks released");
        Check(!Sample(gesture, false, false), "neutral arms without toggling");
        Check(!Sample(gesture, true, false), "first stick alone does not toggle");
        Check(Sample(gesture, true, true), "staggered presses complete one chord");
        for (int frame = 0; frame < 12; frame++)
            Check(!Sample(gesture, true, true), "holding the chord does not repeat, frame " + frame);

        Check(!Sample(gesture, false, true), "left release alone does not rearm");
        Check(!Sample(gesture, true, true), "left repress while right remains held does not toggle");
        Check(!Sample(gesture, true, false), "right release alone does not rearm");
        Check(!Sample(gesture, true, true), "right repress while left remains held does not toggle");
        Check(!Sample(gesture, false, false), "both released rearm without toggling");
        Check(!Sample(gesture, false, true), "right may be pressed first on the next chord");
        Check(Sample(gesture, true, true), "opposite staggered order toggles after full release");
    }

    private static void CheckTrackingLoss()
    {
        object gesture = Activator.CreateInstance(gestureType);
        Check(!Sample(gesture, false, false), "tracking test begins neutral");
        Check(!Sample(gesture, false, false, false), "unavailable neutral input does not count as release");
        Check(!Sample(gesture, true, true), "tracking reconnect with both held does not toggle");
        Check(!Sample(gesture, false, false), "tracked neutral rearms");
        Check(!Sample(gesture, true, false), "first half of chord waits");
        Check(!Sample(gesture, true, true, false), "tracking loss during the chord cannot toggle");
        Check(!Sample(gesture, true, true), "tracking loss discards the pending chord");
        Check(!Sample(gesture, false, false), "fresh release rearms after loss");
        Check(Sample(gesture, true, true), "fresh complete chord works after tracking returns");
    }

    private static void CheckFocusDisarm()
    {
        object gesture = Activator.CreateInstance(gestureType);
        Check(!Sample(gesture, false, false), "focus test begins neutral");
        Check(!Sample(gesture, true, false), "focus test starts a partial chord");
        disarmMethod.Invoke(gesture, null);
        Check(!Sample(gesture, true, true), "focus Disarm cancels an armed partial chord");
        Check(!Sample(gesture, false, true), "partial release after focus change cannot arm");
        Check(!Sample(gesture, true, true), "focus recovery with a held stick cannot toggle");
        Check(!Sample(gesture, false, false), "both released after focus change rearm");
        Check(Sample(gesture, true, true), "fresh chord toggles after focus recovery");
        disarmMethod.Invoke(gesture, null);
        Check(!Sample(gesture, true, true), "Disarm also leaves an already-consumed held chord inactive");
    }

    private static void CheckProtocolMask()
    {
        Check(RemoveClick(0) == 0, "empty controller state remains empty");
        Check(RemoveClick(32) == 0, "reserved thumbstick click is removed");
        Check(RemoveClick(3) == 3, "A/B or X/Y bits remain unchanged");
        Check(RemoveClick(35) == 3, "A/B survive when pressed together with stick click");
        Check(RemoveClick(ushort.MaxValue) == 65503, "all other controller bits survive");

        // Check the public wire-format contract for every possible input;
        // do not reimplement the gesture or production filtering method.
        for (int value = 0; value <= ushort.MaxValue; value++)
        {
            ushort result = RemoveClick((ushort)value);
            Check((result & 32) == 0, "wire mask excludes reserved click: " + value);
            Check(((value ^ result) & 65503) == 0, "wire mask preserves every other bit: " + value);
        }
    }

    private static bool Sample(object gesture, bool left, bool right, bool available = true)
    {
        return (bool)sampleMethod.Invoke(gesture, new object[] { left, right, available });
    }

    private static ushort RemoveClick(ushort value)
    {
        return (ushort)removeClickMethod.Invoke(null, new object[] { value });
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new Exception("FAIL: " + description);
        assertions++;
    }
}
