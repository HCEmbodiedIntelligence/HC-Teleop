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
    private static readonly HashSet<int> positionedCanvasIds =
        new HashSet<int>();
    private static int lastDepthSortFrame = -1;
    private static UdpPoseSender cachedPoseSender;

    public static bool IsTeleopActive()
    {
        if (cachedPoseSender == null)
            cachedPoseSender = FindObjectOfType<UdpPoseSender>(true);
        return cachedPoseSender != null && cachedPoseSender.IsTransmissionEnabled;
    }

    public static void EndAllDrags()
    {
        activePanel = null;
        lastInteractedPanel = null;
        foreach (GripDraggablePanel p in registeredPanels)
        {
            if (p != null)
                p.EndDrag();
        }
    }

    private RectTransform panel;
    private Transform draggingController;
    private XRNode draggingNode;
    private float dragDistance;
    private Vector3 grabOffset;
    private bool wasLeftGripPressed;
    private bool wasRightGripPressed;
    private Vector3 initialLocalPosition;
    private Quaternion initialLocalRotation;
    private Vector3 initialLocalScale;
    private bool hasInitialPose;

    // 与截图中左侧控制栏相同的舒适观看距离。之前场景使用 1.5 m，
    // 在头显里会显得过小；0.95 m 可以保持完整视野并提高可读性。
    private const float ComfortableCanvasDistance = 0.95f;
    private const float ComfortableCanvasYOffset = -0.035f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticCanvasState()
    {
        positionedCanvasIds.Clear();
    }

    private void Awake()
    {
        panel = GetComponent<RectTransform>();
        initialLocalPosition = panel.localPosition;
        initialLocalRotation = panel.localRotation;
        initialLocalScale = panel.localScale;
        hasInitialPose = true;
        FindHeadIfNeeded();
        PositionSharedCanvas(panel.parent as RectTransform, false);
    }

    private void OnEnable()
    {
        // Synchronize the current state so a grip that was pressed outside
        // the panel cannot become a new press when this panel is enabled.
        wasLeftGripPressed = IsGripPressed(XRNode.LeftHand);
        wasRightGripPressed = IsGripPressed(XRNode.RightHand);

        if (!registeredPanels.Contains(this))
            registeredPanels.Add(this);
    }

    private void Update()
    {
        FindHeadIfNeeded();

        // 开启遥操作后彻底禁止拖动画面与面板
        if (IsTeleopActive())
        {
            if (draggingController != null || activePanel == this)
            {
                EndDrag();
            }
            return;
        }

        bool leftGripPressed = IsGripPressed(XRNode.LeftHand);
        bool rightGripPressed = IsGripPressed(XRNode.RightHand);
        bool leftGripPressedThisFrame =
            leftGripPressed && !wasLeftGripPressed;
        bool rightGripPressedThisFrame =
            rightGripPressed && !wasRightGripPressed;

        wasLeftGripPressed = leftGripPressed;
        wasRightGripPressed = rightGripPressed;

        if (draggingController != null)
        {
            bool draggingGripPressed = draggingNode == XRNode.LeftHand
                ? leftGripPressed
                : rightGripPressed;

            if (!draggingGripPressed)
            {
                EndDrag();
                return;
            }

            UpdateFreeDrag();
            return;
        }

        if (activePanel != null)
            return;

        // A drag may only begin on the rising edge while the ray is already
        // inside the closest visible panel. Holding outside and moving onto a
        // panel intentionally does not begin a drag.
        if (leftGripPressedThisFrame &&
            TryBeginDrag(leftController, XRNode.LeftHand))
        {
            return;
        }

        if (rightGripPressedThisFrame)
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
        if (IsTeleopActive() || controller == null || head == null)
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

    public void ResetToInitialPose()
    {
        EndDrag();

        if (panel == null)
            panel = GetComponent<RectTransform>();

        // An inactive scene object may not have received Awake yet. In that
        // case its current serialized transform is already its reset pose.
        if (!hasInitialPose)
        {
            initialLocalPosition = panel.localPosition;
            initialLocalRotation = panel.localRotation;
            initialLocalScale = panel.localScale;
            hasInitialPose = true;
            return;
        }

        panel.localPosition = initialLocalPosition;
        panel.localRotation = initialLocalRotation;
        panel.localScale = initialLocalScale;
        lastInteractedPanel = null;
    }

    public static int ResetAllPanels()
    {
        GripDraggablePanel[] panels =
            FindObjectsOfType<GripDraggablePanel>(true);

        activePanel = null;
        lastInteractedPanel = null;

        var validPanels = new List<GripDraggablePanel>();
        foreach (GripDraggablePanel item in panels)
        {
            if (item != null)
            {
                item.EndDrag();
                if (item.panel == null)
                    item.panel = item.GetComponent<RectTransform>();
                validPanels.Add(item);
            }
        }

        if (validPanels.Count == 0)
            return 0;

        // Prefer a predictable left-to-right order when the two main windows
        // are brought back into view.
        validPanels.Sort((a, b) =>
        {
            int aOrder = GetResetOrder(a.name);
            int bOrder = GetResetOrder(b.name);
            return aOrder != bOrder
                ? aOrder.CompareTo(bOrder)
                : string.CompareOrdinal(a.name, b.name);
        });

        RectTransform sharedParent = validPanels[0].panel.parent as RectTransform;
        bool sameParent = sharedParent != null;
        foreach (GripDraggablePanel item in validPanels)
            sameParent &= item.panel.parent == sharedParent;

        if (sameParent)
        {
            PositionSharedCanvas(sharedParent, true);
            ArrangeOnSharedCanvas(validPanels, sharedParent);
        }
        else
            ArrangeInWorld(validPanels);

        return validPanels.Count;
    }

    private static int GetResetOrder(string objectName)
    {
        if (objectName.IndexOf("Network", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return 0;
        if (objectName.IndexOf("Video", System.StringComparison.OrdinalIgnoreCase) >= 0)
            return 1;
        return 2;
    }

    private static void PositionSharedCanvas(RectTransform canvasRoot, bool force)
    {
        if (canvasRoot == null)
            return;

        Canvas canvas = canvasRoot.GetComponent<Canvas>();
        if (canvas == null || canvas.renderMode != RenderMode.WorldSpace)
            return;

        int id = canvasRoot.GetInstanceID();
        if (!force && positionedCanvasIds.Contains(id))
            return;

        Transform view = Camera.main != null ? Camera.main.transform : null;
        if (view == null)
            return;

        // 当前场景的 Canvas 是 Main Camera 的直接子对象。使用局部坐标
        // 可以让初始和重置位置严格一致，也能保证界面始终正对视线。
        if (canvasRoot.parent == view)
        {
            canvasRoot.localPosition = new Vector3(
                0f,
                ComfortableCanvasYOffset,
                ComfortableCanvasDistance);
            canvasRoot.localRotation = Quaternion.identity;
        }
        else
        {
            canvasRoot.position = view.position +
                                  view.forward * ComfortableCanvasDistance +
                                  view.up * ComfortableCanvasYOffset;
            canvasRoot.rotation = Quaternion.LookRotation(
                canvasRoot.position - view.position,
                view.up);
        }

        positionedCanvasIds.Add(id);
    }

    private static void ArrangeOnSharedCanvas(
        List<GripDraggablePanel> panels,
        RectTransform parent)
    {
        var cameraPanels = new List<GripDraggablePanel>();
        var dashboardPanels = new List<GripDraggablePanel>();
        foreach (GripDraggablePanel item in panels)
        {
            if (item.GetComponentInChildren<CameraFrameViewer>(true) != null ||
                item.name.IndexOf("Video", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                cameraPanels.Add(item);
            }
            else
            {
                dashboardPanels.Add(item);
            }
        }

        // Four camera windows must be treated as a 2x2 block. Laying all five
        // panels out in one row made the B-button reset scale them to roughly
        // half their useful size.
        if (cameraPanels.Count >= 2)
        {
            ArrangeDashboardAndCameraGrid(
                dashboardPanels,
                cameraPanels,
                parent);
            return;
        }

        const float gap = 64f;
        const float horizontalMargin = 100f;
        const float verticalMargin = 80f;

        float rawWidth = gap * Mathf.Max(0, panels.Count - 1);
        float rawMaxHeight = 0f;
        foreach (GripDraggablePanel item in panels)
        {
            rawWidth += Mathf.Max(1f, item.panel.rect.width);
            rawMaxHeight = Mathf.Max(rawMaxHeight, item.panel.rect.height);
        }

        float availableWidth = Mathf.Max(400f, parent.rect.width - horizontalMargin);
        float availableHeight = Mathf.Max(300f, parent.rect.height - verticalMargin);
        float fitScale = Mathf.Min(
            1f,
            availableWidth / Mathf.Max(1f, rawWidth),
            availableHeight / Mathf.Max(1f, rawMaxHeight));

        float fittedTotalWidth = rawWidth * fitScale;
        float cursor = -fittedTotalWidth * 0.5f;

        foreach (GripDraggablePanel item in panels)
        {
            float fittedWidth = item.panel.rect.width * fitScale;
            item.panel.localRotation = Quaternion.identity;
            item.panel.localScale = Vector3.one * fitScale;
            item.panel.anchoredPosition = new Vector2(
                cursor + fittedWidth * 0.5f,
                0f);
            cursor += fittedWidth + gap * fitScale;
        }
    }

    private static void ArrangeDashboardAndCameraGrid(
        List<GripDraggablePanel> dashboardPanels,
        List<GripDraggablePanel> cameraPanels,
        RectTransform parent)
    {
        const float sectionGap = 56f;
        const float cellGap = 28f;
        const float horizontalMargin = 100f;
        const float verticalMargin = 80f;
        const int columns = 2;

        float dashboardWidth = 0f;
        float dashboardHeight = 0f;
        foreach (GripDraggablePanel item in dashboardPanels)
        {
            dashboardWidth = Mathf.Max(dashboardWidth, item.panel.rect.width);
            dashboardHeight += Mathf.Max(1f, item.panel.rect.height);
        }
        if (dashboardPanels.Count > 1)
            dashboardHeight += cellGap * (dashboardPanels.Count - 1);

        float cameraWidth = 1f;
        float cameraHeight = 1f;
        foreach (GripDraggablePanel item in cameraPanels)
        {
            cameraWidth = Mathf.Max(cameraWidth, item.panel.rect.width);
            cameraHeight = Mathf.Max(cameraHeight, item.panel.rect.height);
        }

        int rows = Mathf.CeilToInt((float)cameraPanels.Count / columns);
        float cameraGridWidth = cameraWidth * columns + cellGap * (columns - 1);
        float cameraGridHeight = cameraHeight * rows + cellGap * (rows - 1);
        float rawGroupWidth = cameraGridWidth;
        if (dashboardPanels.Count > 0)
            rawGroupWidth += dashboardWidth + sectionGap;
        float rawGroupHeight = Mathf.Max(dashboardHeight, cameraGridHeight);

        float availableWidth = Mathf.Max(600f, parent.rect.width - horizontalMargin);
        float availableHeight = Mathf.Max(400f, parent.rect.height - verticalMargin);
        float fitScale = Mathf.Min(
            0.9f,
            availableWidth / Mathf.Max(1f, rawGroupWidth),
            availableHeight / Mathf.Max(1f, rawGroupHeight));

        // Keep the reset comfortably readable. The 2x2 layout normally fits
        // around 0.7-0.8 on the current world-space canvas.
        fitScale = Mathf.Clamp(fitScale, 0.65f, 0.9f);

        float fittedGroupWidth = rawGroupWidth * fitScale;
        float groupLeft = -fittedGroupWidth * 0.5f;

        if (dashboardPanels.Count > 0)
        {
            float y = dashboardHeight * fitScale * 0.5f;
            foreach (GripDraggablePanel item in dashboardPanels)
            {
                float height = item.panel.rect.height * fitScale;
                item.panel.localRotation = Quaternion.identity;
                item.panel.localScale = Vector3.one * fitScale;
                item.panel.anchoredPosition = new Vector2(
                    groupLeft + dashboardWidth * fitScale * 0.5f,
                    y - height * 0.5f);
                y -= height + cellGap * fitScale;
            }
        }

        float cameraLeft = groupLeft;
        if (dashboardPanels.Count > 0)
            cameraLeft += (dashboardWidth + sectionGap) * fitScale;

        for (int index = 0; index < cameraPanels.Count; index++)
        {
            int row = index / columns;
            int column = index % columns;
            int itemsInRow = Mathf.Min(columns, cameraPanels.Count - row * columns);
            float rowWidth = cameraWidth * itemsInRow + cellGap * (itemsInRow - 1);
            float rowLeft = cameraLeft +
                (cameraGridWidth - rowWidth) * fitScale * 0.5f;

            GripDraggablePanel item = cameraPanels[index];
            item.panel.localRotation = Quaternion.identity;
            item.panel.localScale = Vector3.one * fitScale;
            item.panel.anchoredPosition = new Vector2(
                rowLeft +
                    (column * (cameraWidth + cellGap) + cameraWidth * 0.5f) *
                    fitScale,
                ((rows - 1) * 0.5f - row) *
                    (cameraHeight + cellGap) * fitScale);
        }
    }

    private static void ArrangeInWorld(List<GripDraggablePanel> panels)
    {
        Transform view = Camera.main != null ? Camera.main.transform : null;
        if (view == null)
        {
            foreach (GripDraggablePanel item in panels)
                item.ResetToInitialPose();
            return;
        }

        float spacing = 0.72f;
        float start = -spacing * (panels.Count - 1) * 0.5f;
        Vector3 center = view.position +
                         view.forward * ComfortableCanvasDistance +
                         view.up * ComfortableCanvasYOffset;

        for (int index = 0; index < panels.Count; index++)
        {
            RectTransform rect = panels[index].panel;
            rect.position = center + view.right * (start + spacing * index);
            rect.rotation = Quaternion.LookRotation(
                rect.position - view.position,
                Vector3.up);
        }
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
        wasLeftGripPressed = false;
        wasRightGripPressed = false;
        registeredPanels.Remove(this);
    }
}
