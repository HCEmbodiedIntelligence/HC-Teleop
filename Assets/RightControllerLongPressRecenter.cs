using System;
using System.Threading;
using ByteDance.PICO.XR;
using UnityEngine;
using UnityEngine.XR;
/// <summary>
/// Single entry point for restoring the application's interface layout.
/// UI buttons and controller shortcuts both call this service so they can
/// never drift into two different reset behaviours.
/// </summary>
public static class InterfaceLayoutResetService
{
    public static event Action<string, int> ResetCompleted;

    public static int RequestReset(string source)
    {
        int resetCount = GripDraggablePanel.ResetAllPanels();
        string safeSource = string.IsNullOrEmpty(source)
            ? "unknown"
            : source;

        if (resetCount > 0)
        {
            Debug.Log(
                "Interface layout reset: " + resetCount +
                " panel(s), source=" + safeSource);
        }
        else
        {
            Debug.LogWarning(
                "Interface layout reset requested by " + safeSource +
                ", but no draggable panels were found.");
        }

        ResetCompleted?.Invoke(safeSource, resetCount);
        return resetCount;
    }
}

// A gesture must start from both sticks released, including after focus/tracking loss.
// Holding one stick and tapping the other again is still the same physical chord.
public sealed class BothThumbsticksGesture
{
    private bool armed;

    public void Disarm() => armed = false;

    public bool Sample(bool left, bool right, bool available)
    {
        if (!available)
        {
            armed = false;
            return false;
        }
        if (!left && !right)
            armed = true;
        if (!armed || !left || !right)
            return false;
        armed = false;
        return true;
    }

    public static ushort RemoveClick(ushort mask)
    {
        return (ushort)(mask & ~(1 << 5));
    }
}

// Keep the original component/script GUID and public UnityEvent entry points.
// PICO owns the right Home/circle key; it is not a normal XR menuButton.
public class RightControllerLongPressRecenter : MonoBehaviour
{
    [Header("System Home recenter")]
    [Min(0f)] public float recenterSettleSeconds = 0.15f;
    [Header("Interface shortcut")]
    public bool enableBothThumbsticksToggle = true;
    [Tooltip("Reserve stick clicks for local UI so the middleware does not mark recordings.")]
    public bool consumeThumbstickClicks = true;

    private readonly BothThumbsticksGesture gesture = new BothThumbsticksGesture();
    private InterfaceVisibilityController visibility;
    private int pendingSystemRecenter;
    private bool applicationFocused;
    private bool applicationPaused;
    private int resetAfterFrame = -1;
    private double resetAfterTime;

    public bool ConsumesThumbstickClicks => isActiveAndEnabled &&
        enableBothThumbsticksToggle && consumeThumbstickClicks;

    private void Awake()
    {
        visibility = GetComponent<InterfaceVisibilityController>();
        if (visibility == null)
            visibility = gameObject.AddComponent<InterfaceVisibilityController>();
        applicationFocused = Application.isFocused;
    }

    private void OnEnable()
    {
        gesture.Disarm();
        PXR_Plugin.System.RecenterSuccess += OnSystemRecenter;
    }

    private void OnSystemRecenter()
    {
        // The SDK calls this from a native callback; Unity objects are main-thread only.
        Interlocked.Exchange(ref pendingSystemRecenter, 1);
    }

    private void LateUpdate()
    {
        if (Interlocked.Exchange(ref pendingSystemRecenter, 0) != 0)
        {
            resetAfterFrame = Time.frameCount + 1;
            resetAfterTime = Time.realtimeSinceStartupAsDouble + recenterSettleSeconds;
            Debug.Log("[Interface] PICO system recenter received; waiting for updated head pose.");
        }
        if (resetAfterFrame < 0 || !applicationFocused || applicationPaused ||
            Time.frameCount <= resetAfterFrame || Time.realtimeSinceStartupAsDouble < resetAfterTime)
            return;

        if (!IsHeadTracked())
            return;
        resetAfterFrame = -1;
        ResetInterfacePanels();
    }

    private static bool IsHeadTracked()
    {
        InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        if (!head.isValid)
            return false;
        if (head.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked))
            return tracked;
        return head.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state) &&
            (state & (InputTrackingState.Position | InputTrackingState.Rotation)) != 0;
    }

    // Called immediately after UdpPoseSender samples both hands, even with UDP off.
    public void ProcessThumbstickInput(bool left, bool right, bool trackingAvailable)
    {
        bool available = isActiveAndEnabled && enableBothThumbsticksToggle &&
            applicationFocused && !applicationPaused && trackingAvailable;
        if (gesture.Sample(left, right, available))
        {
            visibility.ToggleVisibility();
            UdpPoseSender.TriggerHapticImpulse(XRNode.LeftHand, 0.3f, 0.06f);
            UdpPoseSender.TriggerHapticImpulse(XRNode.RightHand, 0.3f, 0.06f);
        }
    }

    public bool ResetNow() => ResetInterfacePanels();

    public bool ResetInterfacePanels()
    {
        // A deliberate recenter also retrieves UI previously hidden with the chord.
        visibility.SetVisible(true);
        return InterfaceLayoutResetService.RequestReset("system Home / interface reset") > 0;
    }

    public bool RecenterView() => ResetInterfacePanels();

    private void OnApplicationFocus(bool focused)
    {
        applicationFocused = focused;
        gesture.Disarm();
        // Focus alone is not a recenter event (short Home also changes focus).
        if (resetAfterFrame >= 0)
        {
            resetAfterFrame = Time.frameCount + 1;
            resetAfterTime = Time.realtimeSinceStartupAsDouble + recenterSettleSeconds;
        }
    }

    private void OnApplicationPause(bool paused)
    {
        applicationPaused = paused;
        gesture.Disarm();
        if (!paused && resetAfterFrame >= 0)
        {
            resetAfterFrame = Time.frameCount + 1;
            resetAfterTime = Time.realtimeSinceStartupAsDouble + recenterSettleSeconds;
        }
    }

    private void OnDisable()
    {
        PXR_Plugin.System.RecenterSuccess -= OnSystemRecenter;
        Interlocked.Exchange(ref pendingSystemRecenter, 0);
        resetAfterFrame = -1;
        gesture.Disarm();
    }
}
