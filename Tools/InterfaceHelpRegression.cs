// Compile as a standalone console program and run with Unity's bundled Mono.
// Arguments: freshly compiled project DLL, Editor/Data/Managed/UnityEngine.
// These checks exercise help visibility state, not XR raycast or focus delivery.
using System;
using System.IO;
using System.Reflection;

public static class InterfaceHelpRegression
{
    private static Type visibilityType;
    private static int assertions;

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: InterfaceHelpRegression.exe <project.dll> <UnityEngine directory>");
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
            visibilityType = project.GetType("InterfaceHelpVisibility", true);

            CheckImmediateHoverDismissal();
            CheckMultiplePointers();
            CheckPinAndUnpin();
            CheckExplicitCloseSuppression();
            CheckResetForFocusAndGlobalHide();
            CheckEmergencyAndGripAccess();
            Console.WriteLine("PASS: " + assertions + " help-visibility assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void CheckImmediateHoverDismissal()
    {
        object state = NewState();
        Expect(state, false, false, "help begins closed");
        Enter(state, 10);
        Expect(state, true, false, "question-button hover opens temporary help");
        Exit(state, 10);
        Expect(state, false, false, "leaving question button closes help immediately");
        Enter(state, 10);
        Expect(state, true, false, "later hover opens again");
        Exit(state, 10);
        Expect(state, false, false, "every unpinned hover ends on exit");
    }

    private static void CheckMultiplePointers()
    {
        object state = NewState();
        Enter(state, 30);
        Enter(state, 31);
        Exit(state, 30);
        Expect(state, true, false, "one controller leaving icon does not dismiss the other");
        Exit(state, 30);
        Exit(state, 99);
        Expect(state, true, false, "duplicate and unknown exits do not dismiss another pointer");
        Exit(state, 31);
        Expect(state, false, false, "last controller leaving immediately dismisses help");

        // Duplicate enter delivery must not leave a phantom pointer behind.
        Enter(state, 32);
        Enter(state, 32);
        Exit(state, 32);
        Expect(state, false, false, "duplicate enters do not require duplicate exits");
    }

    private static void CheckPinAndUnpin()
    {
        object state = NewState();
        Enter(state, 40);
        Call(state, "TogglePin");
        Expect(state, true, true, "first icon click pins hovered help");
        Exit(state, 40);
        Expect(state, true, true, "pinned help remains after all pointers leave");
        Enter(state, 41);
        Exit(state, 41);
        Expect(state, true, true, "later hover and exit do not unpin help");
        Call(state, "TogglePin");
        Expect(state, false, false, "second icon click closes pinned help");
        Call(state, "TogglePin");
        Expect(state, true, true, "click can open and pin without a preceding hover event");
        Call(state, "TogglePin");
        Expect(state, false, false, "another click closes without a preceding hover event");
        Enter(state, 40);
        Expect(state, true, false, "closing without hovering permits the next hover");
    }

    private static void CheckExplicitCloseSuppression()
    {
        object state = NewState();
        Enter(state, 50);
        Call(state, "TogglePin");
        Call(state, "TogglePin");
        Expect(state, false, false, "clicking pinned icon closes immediately");
        Enter(state, 50);
        Expect(state, false, false, "remaining over icon cannot reopen dismissed help");
        Enter(state, 51);
        Expect(state, false, false, "second pointer cannot defeat explicit dismissal");
        Exit(state, 50);
        Enter(state, 50);
        Expect(state, false, false, "dismissal lasts while another pointer remains");
        Exit(state, 50);
        Exit(state, 51);
        Expect(state, false, false, "last exit rearms hover without opening help");
        Enter(state, 50);
        Expect(state, true, false, "new hover opens after all pointers have left");

        Call(state, "TogglePin");
        Call(state, "TogglePin");
        Expect(state, false, false, "second click suppresses the current hover again");
        Call(state, "TogglePin");
        Expect(state, true, true, "explicit click can reopen without requiring hover exit");
        Exit(state, 50);
        Expect(state, true, true, "explicit reopening stays pinned after hover exits");
    }

    private static void CheckResetForFocusAndGlobalHide()
    {
        object state = NewState();
        Enter(state, 60);
        Enter(state, 61);
        Call(state, "TogglePin");
        Call(state, "Reset");
        Expect(state, false, false, "focus loss or global hide clears pinned help");
        Enter(state, 60);
        Exit(state, 60);
        Expect(state, false, false, "reset clears all other controller hover history");

        Enter(state, 60);
        Call(state, "TogglePin");
        Call(state, "TogglePin");
        Expect(state, false, false, "explicit close suppresses hover before reset");
        Call(state, "Reset");
        Enter(state, 60);
        Expect(state, true, false, "reset removes explicit-close suppression");
        Call(state, "Reset");
        Call(state, "Reset");
        Expect(state, false, false, "repeated hidden-frame resets are harmless");
    }

    private static void CheckEmergencyAndGripAccess()
    {
        object state = Activator.CreateInstance(visibilityType);
        Enter(state, 70);
        Call(state, "TogglePin");
        Expect(state, false, false, "unknown initial safety state blocks help");
        foreach (bool stopped in new[] { false, true })
        foreach (bool transmitting in new[] { false, true })
        foreach (bool grip in new[] { false, true })
        foreach (bool shown in new[] { false, true })
        {
            Call(state, "Reset");
            Call(state, "SetAccess", stopped, grip, shown, transmitting);
            Enter(state, 70);
            bool allowed = (stopped || !transmitting) && !grip && shown;
            Expect(state, allowed, false, "hover respects emergency, grip and visibility gates");
            Call(state, "TogglePin");
            Expect(state, allowed, allowed, "click respects emergency, grip and visibility gates");
        }
        Call(state, "SetAccess", true, false, true, true);
        Enter(state, 70);
        Call(state, "TogglePin");
        Call(state, "SetAccess", true, true, true, true);
        Expect(state, false, false, "grip press immediately closes pinned help");
        Call(state, "TogglePin");
        Enter(state, 71);
        Call(state, "SetAccess", true, false, true, true);
        Expect(state, false, false, "grip release cannot restore stale hover or pin");
        Enter(state, 71);
        Expect(state, true, false, "fresh hover after grip release opens help");
        Call(state, "TogglePin");
        Call(state, "SetAccess", false, false, true, true);
        Expect(state, false, false, "confirmed safety resume closes pinned help");
        Call(state, "SetAccess", true, false, true, true);
        Expect(state, false, false, "another stop does not resurrect previous help");

        Call(state, "SetAccess", false, false, true, false);
        Enter(state, 72);
        Call(state, "TogglePin");
        Expect(state, true, true, "UDP off permits pinned help without an emergency or receiver");
        Call(state, "SetAccess", false, false, true, true);
        Expect(state, false, false, "enabling UDP outside emergency closes pinned help");
        Call(state, "SetAccess", false, false, true, false);
        Expect(state, false, false, "disabling UDP does not restore stale hover or pin");
        Enter(state, 72);
        Call(state, "TogglePin");
        Call(state, "SetAccess", true, false, true, false);
        Call(state, "SetAccess", false, false, true, false);
        Expect(state, true, true, "safety resume while UDP remains off keeps help available");
        Call(state, "SetAccess", false, true, true, false);
        Expect(state, false, false, "grip still closes help with UDP off");
    }

    private static object NewState()
    {
        object state = Activator.CreateInstance(visibilityType);
        Call(state, "SetAccess", true, false, true, true);
        return state;
    }
    private static void Enter(object state, int pointerId) { Call(state, "Enter", pointerId); }
    private static void Exit(object state, int pointerId) { Call(state, "Exit", pointerId); }
    private static void Call(object state, string method, params object[] args)
    {
        visibilityType.GetMethod(method).Invoke(state, args);
    }

    private static void Expect(object state, bool visible, bool pinned, string description)
    {
        bool actualVisible = (bool)visibilityType.GetProperty("Visible").GetValue(state);
        bool actualPinned = (bool)visibilityType.GetProperty("Pinned").GetValue(state);
        if (actualVisible != visible || actualPinned != pinned)
            throw new Exception("FAIL: " + description + "; expected Visible=" + visible + ", Pinned=" + pinned
                + "; actual Visible=" + actualVisible + ", Pinned=" + actualPinned);
        assertions++;
    }
}
