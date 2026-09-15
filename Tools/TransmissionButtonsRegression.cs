// Compile as a standalone console program and run with Unity's bundled Mono.
// Arguments: freshly compiled project DLL, Editor/Data/Managed/UnityEngine.
using System;
using System.IO;
using System.Reflection;

public static class TransmissionButtonsRegression
{
    private const ushort A = 1;
    private const ushort B = 2;
    private static Type buttonsType;
    private static MethodInfo updateMethod;
    private static int assertions;

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: TransmissionButtonsRegression.exe <project.dll> <UnityEngine directory>");
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
            buttonsType = project.GetType("TransmissionButtonState", true);
            updateMethod = buttonsType.GetMethod("Update");

            CheckOfflineEnableThenStop();
            CheckUiEnableAndStopPriority();
            CheckHeldACannotRestart();
            CheckHistoryAcrossExternalStateChanges();
            CheckRepeatedToggles();
            CheckOtherButtons();
            Console.WriteLine("PASS: " + assertions + " transmission-button assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void CheckOfflineEnableThenStop()
    {
        object buttons = Activator.CreateInstance(buttonsType);
        bool enabled = false;
        Check(!Apply(buttons, ref enabled, 0), "neutral startup stays disabled");
        Check(Apply(buttons, ref enabled, A), "A enables without a receiver");
        for (int frame = 0; frame < 5; frame++)
            Check(Apply(buttons, ref enabled, A), "held A while waiting for receiver stays enabled: " + frame);
        Check(Apply(buttons, ref enabled, 0), "releasing A does not stop transmission");
        Check(!Apply(buttons, ref enabled, B), "B disables without any intervening packet capture");
        Check(!Apply(buttons, ref enabled, B), "held B stays disabled");
        Check(!Apply(buttons, ref enabled, 0), "B release stays disabled");
    }

    private static void CheckUiEnableAndStopPriority()
    {
        object buttons = Activator.CreateInstance(buttonsType);
        bool enabled = true; // UI enabled UDP with no A press.
        Check(!Apply(buttons, ref enabled, B), "B stops UDP enabled from the UI");
        enabled = true; // UI tries enabling again while B is still held.
        Check(!Apply(buttons, ref enabled, B), "held B overrides UI enable");
        Check(!Apply(buttons, ref enabled, A | B), "A pressed while B is held cannot enable");

        buttons = Activator.CreateInstance(buttonsType);
        enabled = false;
        Check(!Apply(buttons, ref enabled, A | B), "simultaneous A+B from off stays off");
        enabled = true;
        Check(!Apply(buttons, ref enabled, A | B), "simultaneous A+B from on stops");
    }

    private static void CheckHeldACannotRestart()
    {
        object buttons = Activator.CreateInstance(buttonsType);
        bool enabled = false;
        Check(Apply(buttons, ref enabled, A), "held-A scenario begins enabled");
        Check(!Apply(buttons, ref enabled, A | B), "B stops while A remains held");
        Check(!Apply(buttons, ref enabled, A), "releasing only B cannot restart");
        for (int frame = 0; frame < 5; frame++)
            Check(!Apply(buttons, ref enabled, A), "continuously held A cannot restart: " + frame);
        Check(!Apply(buttons, ref enabled, 0), "full release does not enable");
        Check(Apply(buttons, ref enabled, A), "fresh A press after release enables again");
    }

    private static void CheckHistoryAcrossExternalStateChanges()
    {
        object buttons = Activator.CreateInstance(buttonsType);
        bool enabled = false;
        Check(Apply(buttons, ref enabled, A), "history scenario begins with A press");
        enabled = false; // An external stop does not create a new physical A press.
        Check(!Apply(buttons, ref enabled, A), "external state change cannot turn held A into a new press");
        Check(!Apply(buttons, ref enabled, A), "same retained input history remains stopped across reconnect");
        Check(!Apply(buttons, ref enabled, 0), "neutral after reconnect stays stopped");
        Check(Apply(buttons, ref enabled, A), "new A press after reconnect works");
        Check(Apply(buttons, ref enabled, 0), "neutral retains an enabled state");
    }

    private static void CheckRepeatedToggles()
    {
        object buttons = Activator.CreateInstance(buttonsType);
        bool enabled = false;
        for (int cycle = 0; cycle < 4; cycle++)
        {
            Check(Apply(buttons, ref enabled, A), "repeated A enable: " + cycle);
            Check(Apply(buttons, ref enabled, 0), "repeated A release: " + cycle);
            Check(!Apply(buttons, ref enabled, B), "repeated B stop: " + cycle);
            Check(!Apply(buttons, ref enabled, 0), "repeated B release: " + cycle);
        }
    }

    private static void CheckOtherButtons()
    {
        object buttons = Activator.CreateInstance(buttonsType);
        bool enabled = false;
        for (int bit = 2; bit < 16; bit++)
        {
            ushort held = (ushort)(1 << bit);
            Check(!Apply(buttons, ref enabled, held), "other button cannot enable: " + bit);
            enabled = true;
            Check(Apply(buttons, ref enabled, held), "other button cannot stop: " + bit);
            enabled = false;
        }
    }

    private static bool Apply(object buttons, ref bool enabled, int held)
    {
        enabled = (bool)updateMethod.Invoke(buttons, new object[] { enabled, (ushort)held });
        return enabled;
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new Exception("FAIL: " + description);
        assertions++;
    }
}
