using UnityEngine;
using UnityEngine.XR;
using System;
using XRCommonUsages = UnityEngine.XR.CommonUsages;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

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

public class RightControllerLongPressRecenter : MonoBehaviour
{
    [Header("Long press")]
    [Min(0.2f)] public float holdSeconds = 1.2f;
    [Min(0f)] public float releaseGraceSeconds = 0.12f;
    public bool listenPrimaryButton = false;
    public bool listenSecondaryButton = false;
    public bool listenMenuButton = true;
    public bool listenPrimaryAxisClick = true;

    private UnityEngine.XR.InputDevice rightController;
    private double holdStartedAt = -1.0;
    private double lastPressedAt = double.NegativeInfinity;
    private bool wasPressed;
    private bool triggeredThisPress;
    private string activeSource = string.Empty;
    private UdpPoseSender poseSender;
#if ENABLE_INPUT_SYSTEM
    private InputAction rightPrimaryAction;
    private InputAction rightSecondaryAction;
    private InputAction rightAxisClickAction;
#endif

    private void Awake()
    {
#if ENABLE_INPUT_SYSTEM
        rightPrimaryAction = new InputAction(
            "ResetWithRightA",
            InputActionType.Button);
        rightPrimaryAction.AddBinding(
            "<XRController>{RightHand}/primaryButton");
        rightPrimaryAction.AddBinding(
            "<PXR_Controller>{RightHand}/primaryButton");
        rightPrimaryAction.AddBinding(
            "<PICO4UltraController>{RightHand}/primaryButton");

        rightSecondaryAction = new InputAction(
            "ResetWithRightB",
            InputActionType.Button);
        rightSecondaryAction.AddBinding(
            "<XRController>{RightHand}/secondaryButton");
        rightSecondaryAction.AddBinding(
            "<PXR_Controller>{RightHand}/secondaryButton");
        rightSecondaryAction.AddBinding(
            "<PICO4UltraController>{RightHand}/secondaryButton");

        rightAxisClickAction = new InputAction(
            "ResetWithRightStickClick",
            InputActionType.Button);
        rightAxisClickAction.AddBinding(
            "<XRController>{RightHand}/primary2DAxisClick");
        rightAxisClickAction.AddBinding(
            "<PXR_Controller>{RightHand}/thumbstickClicked");
        rightAxisClickAction.AddBinding(
            "<PICO4UltraController>{RightHand}/thumbstickClicked");
#endif

        poseSender = GetComponent<UdpPoseSender>();
        if (poseSender == null)
            poseSender = FindObjectOfType<UdpPoseSender>(true);

    }

    private void OnEnable()
    {
#if ENABLE_INPUT_SYSTEM
        rightPrimaryAction?.Enable();
        rightSecondaryAction?.Enable();
        rightAxisClickAction?.Enable();
#endif
    }

    private void Start()
    {
#if ENABLE_INPUT_SYSTEM
        Debug.Log(
            "Reset input bindings: right A=" +
            (rightPrimaryAction == null ? 0 : rightPrimaryAction.controls.Count) +
            ", right B=" +
            (rightSecondaryAction == null ? 0 : rightSecondaryAction.controls.Count) +
            ", right stick=" +
            (rightAxisClickAction == null ? 0 : rightAxisClickAction.controls.Count));
#endif
    }

    private void Update()
    {
        if (poseSender == null)
            poseSender = FindObjectOfType<UdpPoseSender>(true);

        if (!rightController.isValid)
            rightController = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

        bool pressed = IsRecenterButtonPressed();
        double now = Time.realtimeSinceStartupAsDouble;

        if (pressed)
            lastPressedAt = now;

        // A short grace interval prevents a single missed XR sample from
        // cancelling a deliberate long press.
        bool logicallyPressed = pressed ||
            (wasPressed && now - lastPressedAt <= releaseGraceSeconds);

        if (!logicallyPressed)
        {
            holdStartedAt = -1.0;
            wasPressed = false;
            triggeredThisPress = false;
            activeSource = string.Empty;
            return;
        }

        if (!wasPressed)
        {
            holdStartedAt = now;
            wasPressed = true;
            Debug.Log("Interface reset hold started: " + activeSource);
        }

        if (!triggeredThisPress &&
            now - holdStartedAt >= holdSeconds)
        {
            triggeredThisPress = true;
            ResetInterfacePanels();
        }
    }

    private bool IsRecenterButtonPressed()
    {
        bool primaryPressed = false;
        bool secondaryPressed = false;
        bool menuPressed = false;
        bool axisClickPressed = false;
#if ENABLE_INPUT_SYSTEM
        bool inputSystemPrimaryPressed =
            listenPrimaryButton &&
            rightPrimaryAction != null &&
            rightPrimaryAction.IsPressed();
        bool inputSystemSecondaryPressed =
            listenSecondaryButton &&
            rightSecondaryAction != null &&
            rightSecondaryAction.IsPressed();
        bool inputSystemAxisClickPressed =
            listenPrimaryAxisClick &&
            rightAxisClickAction != null &&
            rightAxisClickAction.IsPressed();
#else
        const bool inputSystemPrimaryPressed = false;
        const bool inputSystemSecondaryPressed = false;
        const bool inputSystemAxisClickPressed = false;
#endif

        if (rightController.isValid && listenPrimaryButton)
        {
            rightController.TryGetFeatureValue(
                XRCommonUsages.primaryButton,
                out primaryPressed);
        }

        if (rightController.isValid && listenSecondaryButton)
        {
            rightController.TryGetFeatureValue(
                XRCommonUsages.secondaryButton,
                out secondaryPressed);
        }

        if (rightController.isValid && listenMenuButton)
        {
            rightController.TryGetFeatureValue(
                XRCommonUsages.menuButton,
                out menuPressed);
        }

        if (rightController.isValid && listenPrimaryAxisClick)
        {
            rightController.TryGetFeatureValue(
                XRCommonUsages.primary2DAxisClick,
                out axisClickPressed);
        }

        // UdpPoseSender is already confirmed to expose the PICO controller
        // buttons correctly (the same values are visible on the PC dashboard).
        // Merge those values so reset and UDP can never disagree about B or
        // the thumbstick click.
        if (poseSender != null)
        {
            if (listenPrimaryButton)
            {
                primaryPressed |= poseSender.IsRightButtonHeld(
                    UdpPoseSender.ControllerButton.Primary);
            }

            if (listenSecondaryButton)
            {
                secondaryPressed |= poseSender.IsRightButtonHeld(
                    UdpPoseSender.ControllerButton.Secondary);
            }

            if (listenMenuButton)
            {
                menuPressed |= poseSender.IsRightButtonHeld(
                    UdpPoseSender.ControllerButton.Menu);
            }

            if (listenPrimaryAxisClick)
            {
                axisClickPressed |= poseSender.IsRightButtonHeld(
                    UdpPoseSender.ControllerButton.PrimaryAxisClick);
            }
        }

        primaryPressed |= inputSystemPrimaryPressed;
        secondaryPressed |= inputSystemSecondaryPressed;
        axisClickPressed |= inputSystemAxisClickPressed;

        if (axisClickPressed)
            activeSource = "right thumbstick click";
        else if (secondaryPressed)
            activeSource = "right B button";
        else if (primaryPressed)
            activeSource = "right A button";
        else if (menuPressed)
            activeSource = "right menu button";

        return axisClickPressed || secondaryPressed ||
               primaryPressed || menuPressed;
    }

    public bool ResetNow() => ResetInterfacePanels();

    public bool ResetInterfacePanels()
    {
        int resetCount = InterfaceLayoutResetService.RequestReset(
            string.IsNullOrEmpty(activeSource)
                ? "reset controller"
                : activeSource);
        return resetCount > 0;
    }

    // Keep the old public method name so any existing UnityEvent reference
    // continues to work, but its behavior is now interface reset.
    public bool RecenterView() => ResetInterfacePanels();

    private void OnDisable()
    {
#if ENABLE_INPUT_SYSTEM
        rightPrimaryAction?.Disable();
        rightSecondaryAction?.Disable();
        rightAxisClickAction?.Disable();
#endif
        holdStartedAt = -1.0;
        lastPressedAt = double.NegativeInfinity;
        wasPressed = false;
        triggeredThisPress = false;
        activeSource = string.Empty;
    }

    private void OnDestroy()
    {
#if ENABLE_INPUT_SYSTEM
        rightPrimaryAction?.Dispose();
        rightSecondaryAction?.Dispose();
        rightAxisClickAction?.Dispose();
#endif
    }
}
