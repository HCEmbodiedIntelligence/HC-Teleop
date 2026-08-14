using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class RightControllerLongPressRecenter : MonoBehaviour
{
    [Header("Long press")]
    [Min(0.2f)] public float holdSeconds = 1.2f;
    public bool listenSecondaryButton = true;
    public bool listenMenuButton = true;

    private readonly List<XRInputSubsystem> inputSubsystems =
        new List<XRInputSubsystem>();

    private InputDevice rightController;
    private float heldTime;
    private bool wasPressed;
    private bool triggeredThisPress;

    private void Update()
    {
        if (!rightController.isValid)
            rightController = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);

        bool pressed = IsRecenterButtonPressed();

        if (!pressed)
        {
            heldTime = 0f;
            wasPressed = false;
            triggeredThisPress = false;
            return;
        }

        if (!wasPressed)
        {
            heldTime = 0f;
            wasPressed = true;
        }

        heldTime += Time.unscaledDeltaTime;

        if (!triggeredThisPress && heldTime >= holdSeconds)
        {
            triggeredThisPress = true;
            RecenterView();
        }
    }

    private bool IsRecenterButtonPressed()
    {
        if (!rightController.isValid)
            return false;

        bool secondaryPressed = false;
        bool menuPressed = false;

        if (listenSecondaryButton)
        {
            rightController.TryGetFeatureValue(
                CommonUsages.secondaryButton,
                out secondaryPressed);
        }

        if (listenMenuButton)
        {
            rightController.TryGetFeatureValue(
                CommonUsages.menuButton,
                out menuPressed);
        }

        return secondaryPressed || menuPressed;
    }

    public bool RecenterView()
    {
        inputSubsystems.Clear();
        SubsystemManager.GetInstances(inputSubsystems);

        bool succeeded = false;

        foreach (XRInputSubsystem subsystem in inputSubsystems)
        {
            if (subsystem != null && subsystem.running)
                succeeded |= subsystem.TryRecenter();
        }

        if (succeeded)
            Debug.Log("XR view recentered by right-controller long press.");
        else
            Debug.LogWarning("XR recenter was rejected by the active tracking origin mode.");

        return succeeded;
    }
}
