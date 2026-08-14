using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;

public sealed class ControllerVisualState : MonoBehaviour
{
    private Transform leftController;
    private Transform rightController;
    private GameObject leftMarker;
    private GameObject rightMarker;
    private XRInteractorLineVisual leftRayVisual;
    private XRInteractorLineVisual rightRayVisual;
    private readonly List<GameObject> leftModelRoots = new List<GameObject>();
    private readonly List<GameObject> rightModelRoots = new List<GameObject>();

    private bool markersRequested = true;
    private bool applicationFocused = true;
    private bool applicationPaused;
    private bool systemButtonHeld;

    public bool MarkersRequested => markersRequested;

    public void Configure(Transform left, Transform right)
    {
        leftController = left;
        rightController = right;
        CacheVisualObjects();
        ApplyVisualState();
    }

    public void SetMarkersVisible(bool visible)
    {
        markersRequested = visible;
        ApplyVisualState();
    }

    private void Awake()
    {
        applicationFocused = Application.isFocused;
    }

    private void Update()
    {
        if (leftController == null || rightController == null)
            TryConfigureFromPoseReader();

        bool menuPressed = IsRightMenuButtonPressed();
        if (menuPressed)
        {
            systemButtonHeld = true;
            HideAllVisuals();
            return;
        }

        if (systemButtonHeld)
            systemButtonHeld = false;

        ApplyVisualState();
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        applicationFocused = hasFocus;
        if (!hasFocus)
            HideAllVisuals();
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        applicationPaused = pauseStatus;
        if (pauseStatus)
            HideAllVisuals();
    }

    private void TryConfigureFromPoseReader()
    {
        ControllerPoseReader poseReader = FindObjectOfType<ControllerPoseReader>(true);
        if (poseReader != null)
            Configure(poseReader.leftController, poseReader.rightController);
    }

    private void CacheVisualObjects()
    {
        leftRayVisual = leftController != null
            ? leftController.GetComponent<XRInteractorLineVisual>()
            : null;
        rightRayVisual = rightController != null
            ? rightController.GetComponent<XRInteractorLineVisual>()
            : null;

        CacheControllerVisuals(
            leftController,
            out leftMarker,
            leftModelRoots);
        CacheControllerVisuals(
            rightController,
            out rightMarker,
            rightModelRoots);
    }

    private static void CacheControllerVisuals(
        Transform controller,
        out GameObject marker,
        List<GameObject> modelRoots)
    {
        marker = null;
        modelRoots.Clear();

        if (controller == null)
            return;

        for (int index = 0; index < controller.childCount; index++)
        {
            Transform child = controller.GetChild(index);
            bool isMarker =
                child.GetComponent<RuntimeAxesMarker>() != null ||
                child.name.IndexOf("Marker", System.StringComparison.OrdinalIgnoreCase) >= 0;

            if (isMarker)
                marker = child.gameObject;
            else
                modelRoots.Add(child.gameObject);
        }
    }

    private void ApplyVisualState()
    {
        bool applicationActive =
            applicationFocused &&
            !applicationPaused &&
            !systemButtonHeld;

        bool leftTracked = applicationActive && IsTracked(XRNode.LeftHand);
        bool rightTracked = applicationActive && IsTracked(XRNode.RightHand);

        SetObjectsActive(leftModelRoots, leftTracked);
        SetObjectsActive(rightModelRoots, rightTracked);
        SetRayVisible(leftRayVisual, leftTracked);
        SetRayVisible(rightRayVisual, rightTracked);

        if (leftMarker != null)
            leftMarker.SetActive(leftTracked && markersRequested);
        if (rightMarker != null)
            rightMarker.SetActive(rightTracked && markersRequested);
    }

    private void HideAllVisuals()
    {
        SetObjectsActive(leftModelRoots, false);
        SetObjectsActive(rightModelRoots, false);
        SetRayVisible(leftRayVisual, false);
        SetRayVisible(rightRayVisual, false);

        if (leftMarker != null)
            leftMarker.SetActive(false);
        if (rightMarker != null)
            rightMarker.SetActive(false);
    }

    private static void SetObjectsActive(List<GameObject> objects, bool active)
    {
        foreach (GameObject item in objects)
        {
            if (item != null && item.activeSelf != active)
                item.SetActive(active);
        }
    }

    private static void SetRayVisible(
        XRInteractorLineVisual rayVisual,
        bool visible)
    {
        if (rayVisual != null && rayVisual.enabled != visible)
            rayVisual.enabled = visible;
    }

    private static bool IsTracked(XRNode node)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (!device.isValid)
            return false;

        if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked))
            return tracked;

        if (device.TryGetFeatureValue(
                CommonUsages.trackingState,
                out InputTrackingState trackingState))
        {
            return (trackingState &
                    (InputTrackingState.Position | InputTrackingState.Rotation)) != 0;
        }

        return true;
    }

    private static bool IsRightMenuButtonPressed()
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        return device.isValid &&
               device.TryGetFeatureValue(CommonUsages.menuButton, out bool pressed) &&
               pressed;
    }
}
