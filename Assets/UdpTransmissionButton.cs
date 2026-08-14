using TMPro;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Button))]
public class UdpTransmissionButton : MonoBehaviour
{
    [Header("引用")]
    public UdpPoseSender poseSender;
    public TMP_Text buttonText;
    public TMP_Text statusText;

    [Header("状态刷新")]
    [Range(0.1f, 2f)] public float statusRefreshInterval = 0.25f;

    private Button button;
    private float refreshTimer;
    private float fpsTimer;
    private int fpsFrameCount;
    private float displayedFps;

    private readonly Color enabledColor = new Color(0.20f, 0.70f, 0.30f);
    private readonly Color searchingColor = new Color(0.85f, 0.60f, 0.15f);
    private readonly Color disabledColor = new Color(0.75f, 0.25f, 0.25f);

    private void Awake()
    {
        button = GetComponent<Button>();
        CreateControlToggles();

        if (statusText != null)
        {
            statusText.raycastTarget = false;
            statusText.enableWordWrapping = true;
            statusText.enableAutoSizing = true;
            statusText.fontSizeMin = 22f;
            statusText.fontSizeMax = 32f;
            statusText.alignment = TextAlignmentOptions.TopLeft;
            statusText.margin = new Vector4(16f, 10f, 16f, 10f);

            RectTransform statusRect = statusText.rectTransform;
            statusRect.anchoredPosition = new Vector2(0f, 200f);
            statusRect.sizeDelta = new Vector2(620f, 360f);
        }
    }

    private void CreateControlToggles()
    {
        Transform panel = transform.parent;
        if (panel == null)
            return;

        RectTransform panelRect = panel as RectTransform;
        if (panelRect != null)
            panelRect.sizeDelta = new Vector2(panelRect.sizeDelta.x, 760f);

        D435FrameViewer viewer = FindObjectOfType<D435FrameViewer>(true);
        CreateStyledToggle(
            panel,
            "VideoWindowToggle",
            "显示相机窗口",
            -245f,
            viewer == null || viewer.IsWindowVisible,
            isOn =>
            {
                D435FrameViewer currentViewer =
                    FindObjectOfType<D435FrameViewer>(true);
                if (currentViewer != null)
                    currentViewer.SetWindowVisible(isOn);
            });

        ControllerVisualState visualState = EnsureControllerVisualState();
        CreateStyledToggle(
            panel,
            "ControllerMarkerToggle",
            "显示手柄 Marker",
            -320f,
            visualState == null || visualState.MarkersRequested,
            isOn =>
            {
                ControllerVisualState currentState =
                    FindObjectOfType<ControllerVisualState>(true);
                if (currentState != null)
                    currentState.SetMarkersVisible(isOn);
            });
    }

    private ControllerVisualState EnsureControllerVisualState()
    {
        ControllerVisualState state =
            FindObjectOfType<ControllerVisualState>(true);
        ControllerPoseReader poseReader =
            FindObjectOfType<ControllerPoseReader>(true);

        if (state == null && poseReader != null)
            state = poseReader.gameObject.AddComponent<ControllerVisualState>();

        if (state != null && poseReader != null)
            state.Configure(poseReader.leftController, poseReader.rightController);

        return state;
    }

    private void CreateStyledToggle(
        Transform panel,
        string objectName,
        string labelText,
        float positionY,
        bool initialValue,
        UnityEngine.Events.UnityAction<bool> changedAction)
    {
        if (panel.Find(objectName) != null)
            return;

        GameObject toggleObject = new GameObject(
            objectName,
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Toggle));
        toggleObject.transform.SetParent(panel, false);

        RectTransform toggleRect = toggleObject.GetComponent<RectTransform>();
        toggleRect.anchorMin = new Vector2(0.5f, 0.5f);
        toggleRect.anchorMax = new Vector2(0.5f, 0.5f);
        toggleRect.pivot = new Vector2(0.5f, 0.5f);
        toggleRect.anchoredPosition = new Vector2(0f, positionY);
        toggleRect.sizeDelta = new Vector2(500f, 70f);

        Image toggleBackground = toggleObject.GetComponent<Image>();
        toggleBackground.color = new Color(0.08f, 0.10f, 0.12f, 0.92f);

        GameObject boxObject = new GameObject(
            "Checkbox",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        boxObject.transform.SetParent(toggleObject.transform, false);

        RectTransform boxRect = boxObject.GetComponent<RectTransform>();
        boxRect.anchorMin = new Vector2(0f, 0.5f);
        boxRect.anchorMax = new Vector2(0f, 0.5f);
        boxRect.pivot = new Vector2(0.5f, 0.5f);
        boxRect.anchoredPosition = new Vector2(42f, 0f);
        boxRect.sizeDelta = new Vector2(42f, 42f);

        Image boxImage = boxObject.GetComponent<Image>();
        boxImage.color = new Color(0.85f, 0.88f, 0.92f, 1f);
        boxImage.raycastTarget = false;

        GameObject checkObject = new GameObject(
            "Checkmark",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        checkObject.transform.SetParent(boxObject.transform, false);

        RectTransform checkRect = checkObject.GetComponent<RectTransform>();
        checkRect.anchorMin = new Vector2(0.5f, 0.5f);
        checkRect.anchorMax = new Vector2(0.5f, 0.5f);
        checkRect.pivot = new Vector2(0.5f, 0.5f);
        checkRect.anchoredPosition = Vector2.zero;
        checkRect.sizeDelta = new Vector2(28f, 28f);

        Image checkImage = checkObject.GetComponent<Image>();
        checkImage.color = new Color(0.15f, 0.75f, 0.30f, 1f);
        checkImage.raycastTarget = false;

        GameObject labelObject = new GameObject(
            "Label",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(TextMeshProUGUI));
        labelObject.transform.SetParent(toggleObject.transform, false);

        RectTransform labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = new Vector2(0f, 0f);
        labelRect.anchorMax = new Vector2(1f, 1f);
        labelRect.offsetMin = new Vector2(82f, 0f);
        labelRect.offsetMax = new Vector2(-16f, 0f);

        TextMeshProUGUI label = labelObject.GetComponent<TextMeshProUGUI>();
        label.text = labelText;
        label.fontSize = 30f;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.color = Color.white;
        label.raycastTarget = false;
        if (buttonText != null && buttonText.font != null)
            label.font = buttonText.font;

        Toggle toggle = toggleObject.GetComponent<Toggle>();
        toggle.targetGraphic = toggleBackground;
        toggle.graphic = checkImage;
        toggle.isOn = initialValue;
        toggle.onValueChanged.AddListener(changedAction);
    }

    private void OnEnable()
    {
        button.onClick.AddListener(OnButtonClicked);
        if (poseSender != null)
        {
            poseSender.TransmissionStateChanged += UpdateButton;
            poseSender.NetworkStatusChanged += RefreshPanel;
        }
    }

    private void Start()
    {
        if (poseSender == null)
        {
            Debug.LogError("UdpPoseSender is not assigned.");
            button.interactable = false;
            return;
        }

        UpdateButton(poseSender.IsTransmissionEnabled);
        RefreshPanel();
    }

    private void Update()
    {
        fpsFrameCount++;
        fpsTimer += Time.unscaledDeltaTime;
        refreshTimer += Time.unscaledDeltaTime;

        if (fpsTimer >= 0.5f)
        {
            displayedFps = fpsFrameCount / fpsTimer;
            fpsFrameCount = 0;
            fpsTimer = 0f;
        }

        if (refreshTimer >= statusRefreshInterval)
        {
            refreshTimer = 0f;
            RefreshPanel();
        }
    }

    private void OnDisable()
    {
        button.onClick.RemoveListener(OnButtonClicked);
        if (poseSender != null)
        {
            poseSender.TransmissionStateChanged -= UpdateButton;
            poseSender.NetworkStatusChanged -= RefreshPanel;
        }
    }

    private void OnButtonClicked() => poseSender?.ToggleTransmission();

    private void UpdateButton(bool enabled)
    {
        if (poseSender == null)
            return;

        if (buttonText != null)
            buttonText.text = enabled ? "关闭 UDP 传输" : "开启 UDP 传输";

        ApplyButtonColor();
        RefreshPanel();
    }

    private void RefreshPanel()
    {
        if (poseSender == null)
            return;

        if (statusText != null)
        {
            statusText.text =
                "本机 IP: " + poseSender.LocalIpAddress + "\n" +
                "PC 接收端: " + poseSender.ReceiverAddress + "\n" +
                "应用 FPS: " + displayedFps.ToString("F1") + "\n" +
                "状态: " + poseSender.CurrentStatus;
        }

        D435FrameViewer videoViewer = FindObjectOfType<D435FrameViewer>(true);
        if (statusText != null && videoViewer != null)
            statusText.text += "\n\n" + videoViewer.CompactStatus;

        ApplyButtonColor();
    }

    private void ApplyButtonColor()
    {
        Color color = !poseSender.IsTransmissionEnabled
            ? disabledColor
            : poseSender.HasReceiver ? enabledColor : searchingColor;

        ColorBlock colors = button.colors;
        colors.normalColor = color;
        colors.selectedColor = color;
        button.colors = colors;
    }
}
