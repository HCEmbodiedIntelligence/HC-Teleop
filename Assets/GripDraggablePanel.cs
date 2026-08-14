using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

[RequireComponent(typeof(RectTransform))]
public class GripDraggablePanel : MonoBehaviour
{
    [Header("Tracking sources")]
    public Transform head;
    public Transform leftController;
    public Transform rightController;

    [Header("Free 3D drag")]
    [Range(0.1f, 1f)] public float gripThreshold = 0.55f;
    [Range(0.3f, 5f)] public float minimumDistance = 0.4f;
    [Range(0.3f, 5f)] public float maximumDistance = 3f;
    public bool keepUpright = true;

    private static GripDraggablePanel activePanel;
    private static GripDraggablePanel lastInteractedPanel;
    private static readonly List<GripDraggablePanel> registeredPanels =
        new List<GripDraggablePanel>();
    private static int lastDepthSortFrame = -1;

    private RectTransform panel;
    private Transform draggingController;
    private XRNode draggingNode;
    private float dragDistance;
    private Vector3 grabOffset;

    private void Awake()
    {
        panel = GetComponent<RectTransform>();
        FindHeadIfNeeded();
    }

    private void OnEnable()
    {
        if (!registeredPanels.Contains(this))
            registeredPanels.Add(this);
    }

    private void Update()
    {
        FindHeadIfNeeded();

        if (draggingController != null)
        {
            if (!IsGripPressed(draggingNode))
            {
                EndDrag();
                return;
            }

            UpdateFreeDrag();
            return;
        }

        if (activePanel != null)
            return;

        if (IsGripPressed(XRNode.LeftHand) &&
            TryBeginDrag(leftController, XRNode.LeftHand))
        {
            return;
        }

        if (IsGripPressed(XRNode.RightHand))
            TryBeginDrag(rightController, XRNode.RightHand);
    }

    private void LateUpdate()
    {
        if (lastDepthSortFrame == Time.frameCount)
            return;

        lastDepthSortFrame = Time.frameCount;
        SortPanelsByDistance();
    }

    private void FindHeadIfNeeded()
    {
        if (head == null && Camera.main != null)
            head = Camera.main.transform;
    }

    private bool TryBeginDrag(Transform controller, XRNode node)
    {
        if (controller == null || head == null)
            return false;

        if (!TryGetClosestPanelHit(
                controller,
                out GripDraggablePanel closestPanel,
                out float panelHitDistance,
                out Vector3 panelHitPoint) ||
            closestPanel != this)
        {
            return false;
        }

        dragDistance = Mathf.Max(0.05f, panelHitDistance);
        grabOffset = panel.position - panelHitPoint;

        activePanel = this;
        lastInteractedPanel = this;
        draggingController = controller;
        draggingNode = node;
        return true;
    }

    private static bool TryGetClosestPanelHit(
        Transform controller,
        out GripDraggablePanel closestPanel,
        out float closestDistance,
        out Vector3 closestPoint)
    {
        closestPanel = null;
        closestDistance = float.PositiveInfinity;
        closestPoint = Vector3.zero;

        if (controller == null)
            return false;

        registeredPanels.RemoveAll(item => item == null);
        Ray controllerRay = new Ray(controller.position, controller.forward);

        foreach (GripDraggablePanel candidate in registeredPanels)
        {
            if (candidate == null ||
                !candidate.isActiveAndEnabled ||
                !candidate.IsPanelInteractionEnabled() ||
                !candidate.TryRaycastPanel(
                    controllerRay,
                    out float hitDistance,
                    out Vector3 hitPoint))
            {
                continue;
            }

            bool isCloser = hitDistance < closestDistance - 0.0001f;
            bool sameDepthButDrawnLater =
                Mathf.Abs(hitDistance - closestDistance) <= 0.0001f &&
                (closestPanel == null ||
                 candidate.transform.GetSiblingIndex() >
                 closestPanel.transform.GetSiblingIndex());

            if (!isCloser && !sameDepthButDrawnLater)
                continue;

            closestPanel = candidate;
            closestDistance = hitDistance;
            closestPoint = hitPoint;
        }

        return closestPanel != null;
    }

    private bool TryRaycastPanel(
        Ray controllerRay,
        out float hitDistance,
        out Vector3 hitPoint)
    {
        hitDistance = 0f;
        hitPoint = Vector3.zero;

        if (panel == null)
            panel = GetComponent<RectTransform>();

        Plane panelPlane = new Plane(panel.forward, panel.position);
        if (!panelPlane.Raycast(controllerRay, out hitDistance) ||
            hitDistance < 0f)
        {
            return false;
        }

        hitPoint = controllerRay.GetPoint(hitDistance);
        Vector3 localHitPoint = panel.InverseTransformPoint(hitPoint);
        return panel.rect.Contains(new Vector2(localHitPoint.x, localHitPoint.y));
    }

    private bool IsPanelInteractionEnabled()
    {
        CanvasGroup canvasGroup = GetComponent<CanvasGroup>();
        return canvasGroup == null ||
               (canvasGroup.alpha > 0.001f && canvasGroup.blocksRaycasts);
    }

    private void UpdateFreeDrag()
    {
        if (head == null || draggingController == null)
            return;

        Vector3 candidatePosition =
            draggingController.position +
            draggingController.forward * dragDistance +
            grabOffset;

        Vector3 fromHead = candidatePosition - head.position;
        float distanceFromHead = fromHead.magnitude;
        if (distanceFromHead < 0.0001f)
            return;

        float clampedDistance = Mathf.Clamp(
            distanceFromHead,
            minimumDistance,
            maximumDistance);
        panel.position = head.position + fromHead.normalized * clampedDistance;

        Vector3 forward = panel.position - head.position;
        if (forward.sqrMagnitude < 0.0001f)
            return;

        Vector3 up = keepUpright ? Vector3.up : head.up;
        panel.rotation = Quaternion.LookRotation(forward.normalized, up);
    }

    private bool IsGripPressed(XRNode node)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (!device.isValid)
            return false;

        if (device.TryGetFeatureValue(
                CommonUsages.gripButton,
                out bool buttonPressed) &&
            buttonPressed)
        {
            return true;
        }

        return device.TryGetFeatureValue(
                   CommonUsages.grip,
                   out float gripValue) &&
               gripValue >= gripThreshold;
    }

    private void EndDrag()
    {
        draggingController = null;
        if (activePanel == this)
            activePanel = null;
    }

    private static void SortPanelsByDistance()
    {
        registeredPanels.RemoveAll(item => item == null);

        registeredPanels.Sort((first, second) =>
        {
            float firstDistance = first.DistanceToHeadSquared();
            float secondDistance = second.DistanceToHeadSquared();
            float difference = firstDistance - secondDistance;

            // Far panels are drawn first. Near panels are later siblings and
            // therefore cover farther panels inside the same world-space Canvas.
            if (Mathf.Abs(difference) > 0.000001f)
                return secondDistance.CompareTo(firstDistance);

            if (first == lastInteractedPanel)
                return 1;
            if (second == lastInteractedPanel)
                return -1;
            return first.GetInstanceID().CompareTo(second.GetInstanceID());
        });

        foreach (GripDraggablePanel item in registeredPanels)
        {
            if (item != null)
                item.transform.SetAsLastSibling();
        }
    }

    private float DistanceToHeadSquared()
    {
        FindHeadIfNeeded();
        if (head == null)
            return float.PositiveInfinity;

        return (transform.position - head.position).sqrMagnitude;
    }

    private void OnDisable()
    {
        EndDrag();
        registeredPanels.Remove(this);
    }
}
