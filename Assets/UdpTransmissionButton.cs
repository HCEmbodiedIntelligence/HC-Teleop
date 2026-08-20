using System;
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
    [Range(0.1f, 2f)] public float statusRefreshInterval = 0.2f;

    private Button udpButton;
    private CameraFrameViewer videoViewer;
    private ControllerVisualState visualState;

    // UI 控件引用
    private Image udpRing;
    private TMP_Text udpCaption;
    private TMP_Text udpIcon;

    private Image cameraRing;
    private TMP_Text cameraCaption;
    private TMP_Text cameraIcon;

    private Image recRing;
    private TMP_Text recCaption;
    private TMP_Text recIcon;
    private Button recButton;

    private Image appFpsRing;
    private TMP_Text appFpsValue;
    private Image cameraFpsRing;
    private TMP_Text cameraFpsValue;

    private Image markerRing;
    private TMP_Text markerCaption;

    private Image headStatusDot;
    private Image leftStatusDot;
    private Image rightStatusDot;
    private TMP_Text trackingSummaryText;

    private float refreshTimer;
    private float fpsTimer;
    private int fpsFrameCount;
    private float displayedFps;

    // 缓存的高清程序化纹理 Sprites
    private static Sprite circleSprite;
    private static Sprite ringSprite;
    private static Sprite roundedCardSprite;
    private static Sprite smallPillSprite;

    // 现代配色方案 (Sci-Fi Glassmorphism)
    private readonly Color colorActiveGreen = new Color(0.06f, 0.94f, 0.54f, 1f);     // #0FF08A
    private readonly Color colorActiveCyan = new Color(0.0f, 0.78f, 1.0f, 1f);        // #00C7FF
    private readonly Color colorActiveRed = new Color(1.0f, 0.22f, 0.32f, 1f);        // #FF3852
    private readonly Color colorWarningAmber = new Color(1.0f, 0.74f, 0.16f, 1f);     // #FFBC29
    private readonly Color colorOffRed = new Color(0.85f, 0.2f, 0.25f, 0.85f);
    private readonly Color colorNeutralRing = new Color(0.35f, 0.42f, 0.52f, 0.65f);
    private readonly Color colorTextMuted = new Color(0.62f, 0.70f, 0.80f, 1f);

    private readonly Color colorPanelBg = new Color(0.045f, 0.06f, 0.09f, 0.94f);
    private readonly Color colorRowBg = new Color(0.08f, 0.105f, 0.145f, 0.92f);
    private readonly Color colorButtonBg = new Color(0.14f, 0.18f, 0.24f, 0.96f);
    private readonly Color colorCardBg = new Color(0.11f, 0.14f, 0.19f, 0.85f);

    private void Awake()
    {
        udpButton = GetComponent<Button>();
        videoViewer = FindObjectOfType<CameraFrameViewer>(true);
        visualState = EnsureControllerVisualState();
        BuildInterface();
    }

    private void BuildInterface()
    {
        RectTransform panel = transform.parent as RectTransform;
        if (panel == null)
            return;

        // 面板主体 (大尺寸现代悬浮卡片)
        panel.sizeDelta = new Vector2(900f, 440f);
        Image panelImage = panel.GetComponent<Image>();
        if (panelImage != null)
        {
            panelImage.sprite = GetRoundedCardSprite(28f);
            panelImage.type = Image.Type.Sliced;
            panelImage.color = colorPanelBg;
        }

        // 顶部品牌与状态栏 (Header Bar)
        BuildHeader(panel);

        // 主控制行 Row 1 (UDP, 相机, 录制, 帧率)
        RectTransform row1 = Background(panel, "PrimaryToolbarRow",
            new Vector2(0f, 58f), new Vector2(846f, 172f));
        ConfigureUdpButton(row1, -315f);
        CreateCameraButton(row1, -105f);
        CreateRecordingButton(row1, 105f);
        CreateDualFpsGauge(row1, 315f);

        // 辅助控制行 Row 2 (Marker, 复位, 追踪诊断卡片)
        RectTransform row2 = Background(panel, "SecondaryToolbarRow",
            new Vector2(0f, -118f), new Vector2(846f, 126f));
        CreateMarkerButton(row2, -315f);
        CreateRoundButton(row2, "ResetInterfaceButton", "复位", "重置界面位置", -150f, ResetInterface);
        CreateTrackingDiagnosticCard(row2, 160f);
    }

    private void BuildHeader(RectTransform panel)
    {
        // 标题与版本徽标
        GameObject titleObj = PlainObject(panel, "HeaderTitleGroup");
        ConfigureRect(titleObj.GetComponent<RectTransform>(), new Vector2(-280f, 178f), new Vector2(260f, 40f));
        TMP_Text title = Label(titleObj.transform, "Brand", "HC-TELEOP", new Vector2(-30f, 0f),
            new Vector2(160f, 36f), 24f, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
        title.color = colorActiveCyan;

        TMP_Text tag = Label(titleObj.transform, "Tag", "v2.3 真机遥操作", new Vector2(85f, -1f),
            new Vector2(120f, 26f), 14f, TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
        tag.color = colorTextMuted;

        ConfigureStatusText(panel);
    }

    private void ConfigureStatusText(Transform panel)
    {
        if (statusText == null)
            return;

        statusText.transform.SetParent(panel, false);
        RectTransform rect = statusText.rectTransform;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(145f, 178f);
        rect.sizeDelta = new Vector2(560f, 38f);
        statusText.raycastTarget = false;
        statusText.enableWordWrapping = false;
        statusText.enableAutoSizing = true;
        statusText.fontSizeMin = 14f;
        statusText.fontSizeMax = 18f;
        statusText.alignment = TextAlignmentOptions.MidlineRight;
        statusText.color = new Color(0.72f, 0.78f, 0.88f, 1f);
    }

    private void ConfigureUdpButton(Transform row, float x)
    {
        transform.SetParent(row, false);
        ConfigureRect(transform as RectTransform, new Vector2(x, 12f), new Vector2(106f, 106f));

        Image image = GetComponent<Image>();
        SetCircleImage(image);
        udpButton.targetGraphic = image;
        SetButtonColors(udpButton);

        if (buttonText != null)
        {
            buttonText.text = "UDP";
            buttonText.fontSize = 26f;
            buttonText.fontStyle = FontStyles.Bold;
            buttonText.alignment = TextAlignmentOptions.Center;
            buttonText.color = Color.white;
            RectTransform textRect = buttonText.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            udpIcon = buttonText;
        }

        udpRing = Ring(transform, "UdpRing", 126f, colorOffRed);
        udpRing.transform.SetAsFirstSibling();
        udpCaption = Label(transform, "UdpCaption", "按A开启 (UDP)", new Vector2(0f, -76f),
            new Vector2(150f, 32f), 17f, TextAlignmentOptions.Center, FontStyles.Normal);
    }

    private void CreateCameraButton(Transform row, float x)
    {
        GameObject obj = ButtonObject(row, "CameraCircleButton");
        ConfigureRect(obj.GetComponent<RectTransform>(), new Vector2(x, 12f), new Vector2(106f, 106f));
        SetCircleImage(obj.GetComponent<Image>());

        Button cameraButton = obj.GetComponent<Button>();
        cameraButton.targetGraphic = obj.GetComponent<Image>();
        SetButtonColors(cameraButton);
        cameraButton.onClick.RemoveAllListeners();
        cameraButton.onClick.AddListener(ToggleCamera);

        cameraIcon = Label(obj.transform, "Icon", "CAM", Vector2.zero, new Vector2(94f, 50f),
            24f, TextAlignmentOptions.Center, FontStyles.Bold);
        cameraRing = Ring(obj.transform, "CameraRing", 126f, colorOffRed);
        cameraRing.transform.SetAsFirstSibling();
        cameraCaption = Label(obj.transform, "Caption", "开启相机", new Vector2(0f, -76f),
            new Vector2(150f, 32f), 17f, TextAlignmentOptions.Center, FontStyles.Normal);
    }

    private void CreateRecordingButton(Transform row, float x)
    {
        GameObject obj = ButtonObject(row, "RecordingCircleButton");
        ConfigureRect(obj.GetComponent<RectTransform>(), new Vector2(x, 12f), new Vector2(106f, 106f));
        SetCircleImage(obj.GetComponent<Image>());

        recButton = obj.GetComponent<Button>();
        recButton.targetGraphic = obj.GetComponent<Image>();
        SetButtonColors(recButton);
        recButton.onClick.RemoveAllListeners();
        recButton.onClick.AddListener(ToggleRecording);

        recIcon = Label(obj.transform, "Icon", "REC", Vector2.zero, new Vector2(94f, 50f),
            24f, TextAlignmentOptions.Center, FontStyles.Bold);
        recRing = Ring(obj.transform, "RecRing", 126f, colorNeutralRing);
        recRing.transform.SetAsFirstSibling();
        recCaption = Label(obj.transform, "Caption", "按X录制 (Y停止)", new Vector2(0f, -76f),
            new Vector2(160f, 32f), 16f, TextAlignmentOptions.Center, FontStyles.Normal);
    }

    private void CreateDualFpsGauge(Transform row, float x)
    {
        GameObject obj = PlainObject(row, "DualFpsGaugeCard");
        ConfigureRect(obj.GetComponent<RectTransform>(), new Vector2(x, 0f), new Vector2(180f, 150f));

        // 应用帧率
        GameObject appObj = PlainObject(obj.transform, "AppFpsPart");
        ConfigureRect(appObj.GetComponent<RectTransform>(), new Vector2(-44f, 12f), new Vector2(80f, 80f));
        appFpsRing = Ring(appObj.transform, "Ring", 76f, colorNeutralRing);
        appFpsValue = Label(appObj.transform, "Value", "--", Vector2.zero, new Vector2(70f, 40f),
            22f, TextAlignmentOptions.Center, FontStyles.Bold);
        Label(appObj.transform, "Caption", "应用FPS", new Vector2(0f, -60f), new Vector2(80f, 26f),
            14f, TextAlignmentOptions.Center, FontStyles.Normal);

        // 相机帧率
        GameObject camObj = PlainObject(obj.transform, "CamFpsPart");
        ConfigureRect(camObj.GetComponent<RectTransform>(), new Vector2(44f, 12f), new Vector2(80f, 80f));
        cameraFpsRing = Ring(camObj.transform, "Ring", 76f, colorNeutralRing);
        cameraFpsValue = Label(camObj.transform, "Value", "--", Vector2.zero, new Vector2(70f, 40f),
            22f, TextAlignmentOptions.Center, FontStyles.Bold);
        Label(camObj.transform, "Caption", "相机FPS", new Vector2(0f, -60f), new Vector2(80f, 26f),
            14f, TextAlignmentOptions.Center, FontStyles.Normal);
    }

    private void CreateMarkerButton(Transform row, float x)
    {
        GameObject obj = ButtonObject(row, "MarkerCircleButton");
        ConfigureRect(obj.GetComponent<RectTransform>(), new Vector2(x, 4f), new Vector2(76f, 76f));
        SetCircleImage(obj.GetComponent<Image>());

        Button markerButton = obj.GetComponent<Button>();
        markerButton.targetGraphic = obj.GetComponent<Image>();
        SetButtonColors(markerButton);
        markerButton.onClick.RemoveAllListeners();
        markerButton.onClick.AddListener(ToggleMarkers);

        Label(obj.transform, "Icon", "XYZ", Vector2.zero, new Vector2(66f, 40f),
            20f, TextAlignmentOptions.Center, FontStyles.Bold);
        markerRing = Ring(obj.transform, "MarkerRing", 90f, colorActiveGreen);
        markerRing.transform.SetAsFirstSibling();
        markerCaption = Label(obj.transform, "Caption", "隐藏 Marker", new Vector2(0f, -54f),
            new Vector2(160f, 28f), 16f, TextAlignmentOptions.Center, FontStyles.Normal);
    }

    private void CreateRoundButton(Transform row, string name, string icon, string caption,
        float x, UnityEngine.Events.UnityAction callback)
    {
        GameObject obj = ButtonObject(row, name);
        ConfigureRect(obj.GetComponent<RectTransform>(), new Vector2(x, 4f), new Vector2(76f, 76f));
        SetCircleImage(obj.GetComponent<Image>());

        Button actionButton = obj.GetComponent<Button>();
        actionButton.targetGraphic = obj.GetComponent<Image>();
        SetButtonColors(actionButton);
        actionButton.onClick.RemoveAllListeners();
        actionButton.onClick.AddListener(callback);

        Label(obj.transform, "Icon", icon, Vector2.zero, new Vector2(66f, 40f),
            20f, TextAlignmentOptions.Center, FontStyles.Bold);
        Ring(obj.transform, "Ring", 90f, colorNeutralRing).transform.SetAsFirstSibling();
        Label(obj.transform, "Caption", caption, new Vector2(0f, -54f), new Vector2(160f, 28f),
            16f, TextAlignmentOptions.Center, FontStyles.Normal);
    }

    private void CreateTrackingDiagnosticCard(Transform row, float x)
    {
        GameObject card = PlainObject(row, "TrackingDiagnosticCard");
        ConfigureRect(card.GetComponent<RectTransform>(), new Vector2(x, 4f), new Vector2(440f, 96f));
        Image cardImg = card.AddComponent<Image>();
        cardImg.sprite = GetRoundedCardSprite(16f);
        cardImg.type = Image.Type.Sliced;
        cardImg.color = colorCardBg;

        // 诊断标签
        Label(card.transform, "Title", "6-DoF 空间追踪诊断", new Vector2(-110f, 22f), new Vector2(180f, 28f),
            15f, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);

        trackingSummaryText = Label(card.transform, "Summary", "追踪正常", new Vector2(120f, 22f), new Vector2(140f, 28f),
            14f, TextAlignmentOptions.MidlineRight, FontStyles.Normal);

        // 设备状态 Pill Badges (头显 / 左手 / 右手)
        CreateTrackingPill(card.transform, "HmdPill", "头显", -140f, -16f, out headStatusDot);
        CreateTrackingPill(card.transform, "LeftPill", "左手", 0f, -16f, out leftStatusDot);
        CreateTrackingPill(card.transform, "RightPill", "右手", 140f, -16f, out rightStatusDot);
    }

    private void CreateTrackingPill(Transform parent, string name, string label, float x, float y, out Image dot)
    {
        GameObject pill = PlainObject(parent, name);
        ConfigureRect(pill.GetComponent<RectTransform>(), new Vector2(x, y), new Vector2(120f, 36f));
        Image pillImg = pill.AddComponent<Image>();
        pillImg.sprite = GetRoundedCardSprite(10f);
        pillImg.type = Image.Type.Sliced;
        pillImg.color = new Color(0.16f, 0.20f, 0.28f, 0.9f);

        // 状态指示圆点
        GameObject dotObj = PlainObject(pill.transform, "StatusDot");
        ConfigureRect(dotObj.GetComponent<RectTransform>(), new Vector2(-36f, 0f), new Vector2(14f, 14f));
        dot = dotObj.AddComponent<Image>();
        dot.sprite = GetCircleSprite();
        dot.color = colorWarningAmber;

        Label(pill.transform, "Label", label, new Vector2(14f, 0f), new Vector2(68f, 28f),
            16f, TextAlignmentOptions.MidlineLeft, FontStyles.Normal);
    }

    private RectTransform Background(Transform parent, string name, Vector2 position, Vector2 size)
    {
        Transform found = parent.Find(name);
        GameObject obj = found != null ? found.gameObject : new GameObject(name,
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        if (found == null)
            obj.transform.SetParent(parent, false);

        RectTransform rect = obj.GetComponent<RectTransform>();
        ConfigureRect(rect, position, size);
        Image image = obj.GetComponent<Image>();
        image.sprite = GetRoundedCardSprite(20f);
        image.type = Image.Type.Sliced;
        image.color = colorRowBg;
        image.raycastTarget = false;
        rect.SetAsFirstSibling();
        return rect;
    }

    private GameObject PlainObject(Transform parent, string name)
    {
        Transform found = parent.Find(name);
        if (found != null)
            return found.gameObject;

        GameObject obj = new GameObject(name, typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        return obj;
    }

    private GameObject ButtonObject(Transform parent, string name)
    {
        Transform found = parent.Find(name);
        if (found != null)
            return found.gameObject;

        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer),
            typeof(Image), typeof(Button));
        obj.transform.SetParent(parent, false);
        return obj;
    }

    private TMP_FontAsset chineseFontAsset;

    private TMP_FontAsset GetChineseFont()
    {
        if (chineseFontAsset != null)
            return chineseFontAsset;

        if (statusText != null && statusText.font != null)
            chineseFontAsset = statusText.font;
        else if (buttonText != null && buttonText.font != null)
            chineseFontAsset = buttonText.font;
        else if (TMP_Settings.defaultFontAsset != null)
            chineseFontAsset = TMP_Settings.defaultFontAsset;

        return chineseFontAsset;
    }

    private TMP_Text Label(Transform parent, string name, string text, Vector2 position,
        Vector2 size, float fontSize, TextAlignmentOptions alignment, FontStyles style)
    {
        Transform found = parent.Find(name);
        GameObject obj = found != null ? found.gameObject : new GameObject(name,
            typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        if (found == null)
            obj.transform.SetParent(parent, false);

        ConfigureRect(obj.GetComponent<RectTransform>(), position, size);
        TMP_Text tmp = obj.GetComponent<TMP_Text>();
        TMP_FontAsset font = GetChineseFont();
        if (font != null)
        {
            tmp.font = font;
            if (font.material != null)
                tmp.fontSharedMaterial = font.material;
        }
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.fontStyle = style;
        tmp.alignment = alignment;
        tmp.color = Color.white;
        tmp.raycastTarget = false;
        tmp.enableWordWrapping = false;
        return tmp;
    }

    private Image Ring(Transform parent, string name, float size, Color color)
    {
        Transform found = parent.Find(name);
        GameObject obj = found != null ? found.gameObject : new GameObject(name,
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        if (found == null)
            obj.transform.SetParent(parent, false);

        ConfigureRect(obj.GetComponent<RectTransform>(), Vector2.zero, new Vector2(size, size));
        Image image = obj.GetComponent<Image>();
        image.sprite = GetRingSprite();
        image.type = Image.Type.Simple;
        image.preserveAspect = true;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static void ConfigureRect(RectTransform rect, Vector2 position, Vector2 size)
    {
        if (rect == null)
            return;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }

    private void SetCircleImage(Image image)
    {
        if (image == null)
            return;
        image.sprite = GetCircleSprite();
        image.type = Image.Type.Simple;
        image.preserveAspect = true;
        image.color = colorButtonBg;
    }

    private void SetButtonColors(Button target)
    {
        ColorBlock colors = target.colors;
        colors.normalColor = colorButtonBg;
        colors.highlightedColor = new Color(0.24f, 0.28f, 0.38f, 1f);
        colors.pressedColor = new Color(0.10f, 0.12f, 0.16f, 1f);
        colors.selectedColor = colors.highlightedColor;
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.1f;
        target.colors = colors;
    }

    #region 高清抗锯齿程序化纹理生成器 (SDF Antialiasing)

    private static Sprite GetCircleSprite()
    {
        if (circleSprite == null)
            circleSprite = GenerateSdfRadialSprite("HdCircle", false);
        return circleSprite;
    }

    private static Sprite GetRingSprite()
    {
        if (ringSprite == null)
            ringSprite = GenerateSdfRadialSprite("HdRing", true);
        return ringSprite;
    }

    private static Sprite GetRoundedCardSprite(float radius = 24f)
    {
        if (roundedCardSprite == null)
            roundedCardSprite = GenerateSdfRoundedRectSprite("HdRoundedCard", radius);
        return roundedCardSprite;
    }

    private static Sprite GenerateSdfRadialSprite(string name, bool ringOnly)
    {
        const int size = 256;
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.name = name;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
        float outerRadius = size * 0.46f;
        float innerRadius = ringOnly ? size * 0.38f : 0f;
        float aa = 2.5f;

        Color[] colors = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), center);
                float alpha = 0f;
                if (ringOnly)
                {
                    float outer = Mathf.Clamp01((outerRadius - d) / aa);
                    float inner = Mathf.Clamp01((d - innerRadius) / aa);
                    alpha = outer * inner;
                }
                else
                {
                    alpha = Mathf.Clamp01((outerRadius - d) / aa);
                }
                colors[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }
        texture.SetPixels(colors);
        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    private static Sprite GenerateSdfRoundedRectSprite(string name, float cornerRadius)
    {
        const int size = 128;
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.name = name;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        float r = cornerRadius;
        float aa = 2.0f;

        Color[] colors = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(0, Mathf.Abs(x - (size - 1) * 0.5f) - ((size - 1) * 0.5f - r));
                float dy = Mathf.Max(0, Mathf.Abs(y - (size - 1) * 0.5f) - ((size - 1) * 0.5f - r));
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float alpha = Mathf.Clamp01((r - d) / aa);
                colors[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }
        texture.SetPixels(colors);
        texture.Apply();
        Vector4 border = new Vector4(r, r, r, r);
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
    }

    #endregion

    private ControllerVisualState EnsureControllerVisualState()
    {
        ControllerVisualState state = FindObjectOfType<ControllerVisualState>(true);
        ControllerPoseReader reader = FindObjectOfType<ControllerPoseReader>(true);
        if (state == null && reader != null)
            state = reader.gameObject.AddComponent<ControllerVisualState>();
        if (state != null && reader != null)
            state.Configure(reader.leftController, reader.rightController);
        return state;
    }

    private void ToggleCamera()
    {
        if (videoViewer == null)
            videoViewer = FindObjectOfType<CameraFrameViewer>(true);
        if (videoViewer != null)
            videoViewer.SetWindowVisible(!videoViewer.IsWindowVisible);
        RefreshPanel();
    }

    private void ToggleMarkers()
    {
        if (visualState == null)
            visualState = EnsureControllerVisualState();
        if (visualState != null)
            visualState.SetMarkersVisible(!visualState.MarkersRequested);
        RefreshPanel();
    }

    private void ToggleRecording()
    {
        // 界面点击按钮提示
        if (poseSender != null && poseSender.IsRecording)
        {
            Debug.Log("[VR Teleop] 请按左手柄 Y 键停止录制");
        }
        else
        {
            Debug.Log("[VR Teleop] 请按左手柄 X 键开始录制");
        }
    }

    private void ResetInterface()
    {
        InterfaceLayoutResetService.RequestReset("interface button");
    }

    private void OnEnable()
    {
        if (udpButton != null)
            udpButton.onClick.AddListener(ToggleUdp);
        if (poseSender != null)
        {
            poseSender.TransmissionStateChanged += OnTransmissionChanged;
            poseSender.NetworkStatusChanged += RefreshPanel;
            poseSender.RecordingStateChanged += OnRecordingChanged;
        }
    }

    private void Start()
    {
        if (poseSender == null)
        {
            Debug.LogError("UdpPoseSender is not assigned.");
            if (udpButton != null)
                udpButton.interactable = false;
            return;
        }
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
        if (udpButton != null)
            udpButton.onClick.RemoveListener(ToggleUdp);
        if (poseSender != null)
        {
            poseSender.TransmissionStateChanged -= OnTransmissionChanged;
            poseSender.NetworkStatusChanged -= RefreshPanel;
            poseSender.RecordingStateChanged -= OnRecordingChanged;
        }
    }

    private void ToggleUdp()
    {
        poseSender?.ToggleTransmission();
    }

    private void OnTransmissionChanged(bool value)
    {
        RefreshPanel();
    }

    private void OnRecordingChanged(bool recording, string info)
    {
        RefreshPanel();
    }

    private void RefreshPanel()
    {
        if (poseSender == null)
            return;

        if (videoViewer == null)
            videoViewer = FindObjectOfType<CameraFrameViewer>(true);
        if (visualState == null)
            visualState = EnsureControllerVisualState();

        // 1. 顶部网络与录制状态
        if (statusText != null)
        {
            string pc = string.IsNullOrEmpty(poseSender.ReceiverAddress)
                ? "<color=#FFB826>未连接</color>" : "<color=#00E676>" + poseSender.ReceiverAddress + "</color>";
            string recInfo = "";
            if (poseSender.IsRecording)
            {
                float elapsed = Time.realtimeSinceStartup - poseSender.RecordingStartTime;
                int min = (int)elapsed / 60;
                int sec = (int)elapsed % 60;
                recInfo = string.Format("   <color=#FF3852>[● 录制中 {0:D2}:{1:D2}]</color>", min, sec);
            }
            else if (!string.IsNullOrEmpty(poseSender.LastRecordingMessage))
            {
                recInfo = "   <color=#00C7FF>[" + poseSender.LastRecordingMessage + "]</color>";
            }
            statusText.text = "PICO  " + poseSender.LocalIpAddress + "  ⇄  PC  " + pc + recInfo;
        }

        // 2. UDP 传输控制状态
        Color udpColor = !poseSender.IsTransmissionEnabled
            ? colorOffRed : poseSender.HasReceiver ? colorActiveGreen : colorWarningAmber;
        if (udpRing != null) udpRing.color = udpColor;
        if (udpCaption != null)
            udpCaption.text = poseSender.IsTransmissionEnabled ? "按B关闭 (UDP)" : "按A开启 (UDP)";
        if (udpIcon != null)
            udpIcon.color = poseSender.IsTransmissionEnabled ? colorActiveGreen : Color.white;

        // 3. CAM 相机控制状态
        bool cameraVisible = videoViewer != null && videoViewer.IsWindowVisible;
        bool cameraConnected = videoViewer != null && videoViewer.IsVideoConnected;
        Color camColor = cameraConnected ? colorActiveCyan : cameraVisible ? colorWarningAmber : colorOffRed;
        if (cameraRing != null) cameraRing.color = camColor;
        if (cameraCaption != null)
            cameraCaption.text = cameraVisible ? "关闭相机" : "开启相机";
        if (cameraIcon != null)
            cameraIcon.color = cameraConnected ? colorActiveCyan : Color.white;

        // 4. REC 数据集录制控制状态 (呼吸灯与计时)
        if (recRing != null && recCaption != null && recIcon != null)
        {
            if (poseSender.IsRecording)
            {
                float elapsed = Time.realtimeSinceStartup - poseSender.RecordingStartTime;
                int min = (int)elapsed / 60;
                int sec = (int)elapsed % 60;
                recCaption.text = string.Format("● 录制中 {0:D2}:{1:D2}", min, sec);
                recCaption.color = colorActiveRed;

                // 呼吸闪烁红环
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 5f);
                recRing.color = Color.Lerp(new Color(0.6f, 0.1f, 0.15f, 0.8f), colorActiveRed, pulse);
                recIcon.color = colorActiveRed;
            }
            else
            {
                recCaption.text = "按X录制 (Y停止)";
                recCaption.color = colorTextMuted;
                recRing.color = colorNeutralRing;
                recIcon.color = Color.white;
            }
        }

        // 5. 帧率仪表
        if (appFpsValue != null)
            appFpsValue.text = displayedFps > 0f ? displayedFps.ToString("F0") : "--";
        if (appFpsRing != null)
            appFpsRing.color = displayedFps >= 70f ? colorActiveGreen : displayedFps >= 45f ? colorWarningAmber : colorOffRed;

        if (cameraFpsValue != null)
            cameraFpsValue.text = videoViewer != null && videoViewer.DisplayedVideoFps > 0f
                ? videoViewer.DisplayedVideoFps.ToString("F0") : "--";
        if (cameraFpsRing != null)
            cameraFpsRing.color = cameraConnected ? colorActiveCyan : colorNeutralRing;

        // 6. XYZ Marker 状态
        bool markers = visualState == null || visualState.MarkersRequested;
        if (markerRing != null) markerRing.color = markers ? colorActiveGreen : colorNeutralRing;
        if (markerCaption != null)
            markerCaption.text = markers ? "隐藏 Marker" : "显示 Marker";

        // 7. 空间设备追踪诊断 (独立状态灯)
        if (headStatusDot != null)
            headStatusDot.color = poseSender.headTracked ? colorActiveGreen : colorWarningAmber;
        if (leftStatusDot != null)
            leftStatusDot.color = poseSender.leftTracked ? colorActiveGreen : colorWarningAmber;
        if (rightStatusDot != null)
            rightStatusDot.color = poseSender.rightTracked ? colorActiveGreen : colorWarningAmber;

        if (trackingSummaryText != null)
        {
            bool allGood = poseSender.headTracked && poseSender.leftTracked && poseSender.rightTracked;
            trackingSummaryText.text = allGood ? "<color=#0FF08A>全部正常</color>" : "<color=#FFBC29>部分未追踪</color>";
        }
    }
}
