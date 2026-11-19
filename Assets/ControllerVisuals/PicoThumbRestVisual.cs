using ByteDance.PICO.XR.Input;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using InputSystemUsages = UnityEngine.InputSystem.CommonUsages;

[DisallowMultipleComponent]
public sealed class PicoThumbRestVisual : MonoBehaviour
{
    private static readonly InputFeatureUsage<bool> ThumbRestTouch =
        new InputFeatureUsage<bool>("ThumbRestTouch");

    [SerializeField] private bool leftHand;
    private Animator handAnimator;
    private int restLayer = -1;
    private PXR_Controller picoController;

    private void Awake()
    {
        var driver = GetComponentInChildren<ByteDance.PICO.XR.PXR_ControllerWithHandAnimator>(true);
        if (driver != null && driver.TryGetComponent(out handAnimator))
            restLayer = handAnimator.GetLayerIndex("thumbstick_touch");
    }

    private void LateUpdate()
    {
        if (restLayer < 0)
            return;

        bool touching = ReadThumbRestTouch();
        float weight = Mathf.MoveTowards(handAnimator.GetLayerWeight(restLayer),
            touching ? 1f : 0f, Time.unscaledDeltaTime * 20f);
        // The SDK's higher-priority button and stick layers still take precedence.
        handAnimator.SetLayerWeight(restLayer, weight);
    }

    private bool ReadThumbRestTouch()
    {
        var xrDevice = InputDevices.GetDeviceAtXRNode(leftHand ? XRNode.LeftHand : XRNode.RightHand);
        if (xrDevice.TryGetFeatureValue(ThumbRestTouch, out bool touching))
            return touching;

        if (picoController == null || !picoController.added)
            picoController = InputSystem.GetDevice<PXR_Controller>(
                leftHand ? InputSystemUsages.LeftHand : InputSystemUsages.RightHand);
        // PICO's integration layout aliases this specific control to ThumbRestTouch.
        return picoController?.thumbstickTouched?.isPressed == true;
    }

    private void OnDisable()
    {
        if (handAnimator != null && restLayer >= 0)
            handAnimator.SetLayerWeight(restLayer, 0f);
        picoController = null;
    }
}
