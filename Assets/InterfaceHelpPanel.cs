using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Only the question button owns hover state. Clicking switches to persistent
// help; clicking it again closes help even while the pointer remains on it.
public sealed class InterfaceHelpVisibility
{
    private readonly HashSet<int> buttonPointers = new HashSet<int>();
    private bool dismissed;
    public bool Visible => Pinned || (!dismissed && buttonPointers.Count > 0);
    public bool Pinned { get; private set; }

    public void Enter(int pointerId)
    {
        buttonPointers.Add(pointerId);
    }

    public void Exit(int pointerId)
    {
        buttonPointers.Remove(pointerId);
        if (buttonPointers.Count == 0) dismissed = false;
    }

    public void TogglePin()
    {
        Pinned = !Pinned;
        dismissed = !Pinned && buttonPointers.Count > 0;
    }

    public void Reset()
    {
        buttonPointers.Clear();
        dismissed = false;
        Pinned = false;
    }
}

[DisallowMultipleComponent]
public sealed class InterfaceHelpPanel : MonoBehaviour
{
    private struct Entry
    {
        public readonly string title;
        public readonly string body;
        public Entry(string title, string body) { this.title = title; this.body = body; }
    }

    private static readonly string[] PageNames = { "手柄按键", "界面按钮", "布局调整", "状态提示", "机器人操作" };
    private static readonly Entry[][] Pages =
    {
        new[]
        {
            new Entry("右手 A · 开启 / 恢复", "开启 UDP。急停或重放结束后，\n按 A 请求恢复，等待中间件确认。"),
            new Entry("右手 B · 关闭 UDP", "停止发送遥操作数据。\n未连接时也可关闭；无需长按。"),
            new Entry("左手 X / Y · 录制", "X 开始录制，Y 停止录制。\n需连接中间件并开启 UDP。"),
            new Entry("右侧圆圈键 · 界面复位", "长按系统圆圈键，系统复位完成\n后界面回到眼前；短按打开系统。"),
            new Entry("双摇杆按下 · 显示 / 隐藏", "同时按下左右摇杆切换所有界面。\n两边都松开后，再按可切换回来。"),
            new Entry("射线 + 扳机 · 点击", "将手柄射线指向按钮，扣动扳机。\n指向菱形问号即可悬停阅读说明。")
        },
        new[]
        {
            new Entry("UDP · 数据发送开关", "点击切换发送状态，也可用 A / B。\n开启后等待自动发现中间件。"),
            new Entry("CAM · 全部相机", "统一开启或关闭全部相机窗口。\n关闭相机会停止对应视频连接。"),
            new Entry("REC · 录制状态", "显示录制状态与时长。\n使用左手 X / Y 开始或停止录制。"),
            new Entry("XYZ · 坐标标记", "显示或隐藏手柄的 XYZ 坐标标记。\n不会关闭手柄追踪。"),
            new Entry("复位 · 恢复默认布局", "点击后将面板移回眼前，\n并恢复相机窗口的默认大小。"),
            new Entry("相机窗口 · 单路开关", "点击头部、俯视或腕部相机按钮，\n单独显示 / 隐藏；数字为帧率。")
        },
        new[]
        {
            new Entry("移动面板", "先按 B 关闭 UDP。射线指向面板，\n再按住侧握把键，移动手柄拖动。"),
            new Entry("缩放相机窗口", "关闭 UDP 后，射线指向相机边缘，\n按住侧握把键拖动，松开结束。"),
            new Entry("握把操作时机", "先指向，再按握把。\n开启 UDP 后，面板拖动与缩放禁用。"),
            new Entry("界面隐藏期间", "双摇杆只隐藏界面和射线。\nUDP、录制和相机连接继续运行。"),
            new Entry("找回界面", "再按一次双摇杆可显示界面。\n也可长按右侧圆圈键复位到眼前。"),
            new Entry("保留相机选择", "双摇杆隐藏后再显示，\n会保留各相机原有的开关状态。")
        },
        new[]
        {
            new Entry("连接与 UDP 指示", "顶部显示 PICO 与 PC 地址。\nUDP 绿：连接；黄：等待；红：停发。"),
            new Entry("急停同步", "收到中间件急停后，显示红色原因提示，\nUDP 图标切换为“急停”。"),
            new Entry("急停恢复", "排除原因并确认连接后按 A。\n只有中间件确认恢复，急停提示才解除。"),
            new Entry("追踪诊断", "头显、左手、右手分别显示状态。\n绿色表示已追踪，黄色表示未追踪。"),
            new Entry("相机指示", "青色表示视频已连接；黄色表示等待。\n灰色表示窗口隐藏；-- 表示暂无帧率。"),
            new Entry("录制与重放", "红色 REC 与计时表示正在录制。\n顶部显示重放状态；结束后按 A 恢复。")
        },
        new[]
        {
            new Entry("以中间件配置为准", "以下为配套中间件的控制方式，\n具体动作取决于当前机器人配置。"),
            new Entry("右侧握把 · 跟随使能", "按住使能双臂与夹爪跟随，\n松开保持。需先开启 UDP。"),
            new Entry("左右扳机 · 夹爪", "控制对应夹爪开合。\n扳机同时也用于界面点击。"),
            new Entry("左侧握把 · 底盘 / 腰部", "配合摇杆 / 头部动作控制底盘、\n腰部；仅适用于支持该功能的机器人。"),
            new Entry("摇杆向外拨 · 机器人回零", "左杆向左、右杆向右：机器人回零。\n向下按两摇杆才是界面显隐。"),
            new Entry("查看说明期间", "说明面板不会暂停遥操作。\n需要停止发送时，先按 B 关闭 UDP。")
        }
    };

    private readonly InterfaceHelpVisibility visibility = new InterfaceHelpVisibility();
    private readonly TMP_Text[] entryTitles = new TMP_Text[6];
    private readonly TMP_Text[] entryBodies = new TMP_Text[6];
    private readonly Image[] tabImages = new Image[PageNames.Length];
    private readonly TMP_Text[] tabLabels = new TMP_Text[PageNames.Length];
    private RectTransform popup;
    private TMP_Text footer;
    private Image diamond;
    private TMP_FontAsset font;
    private Sprite cardSprite;
    private int currentPage;
    private static readonly Color Cyan = new Color(0f, 0.78f, 1f);
    private static readonly Color Muted = new Color(0.67f, 0.75f, 0.86f);
    private static readonly Color Card = new Color(0.075f, 0.105f, 0.155f);

    public void Initialize(TMP_FontAsset chineseFont, Sprite roundedSprite)
    {
        if (popup != null) return;
        font = chineseFont;
        cardSprite = roundedSprite;
        Place(transform as RectTransform, Vector2.zero, new Vector2(900f, 560f));
        transform.SetAsLastSibling();

        RectTransform trigger = Box(transform, "HelpButton", Vector2.zero,
            new Vector2(62f, 56f), Color.clear, false);
        trigger.anchorMin = trigger.anchorMax = Vector2.one;
        trigger.anchoredPosition = new Vector2(-52f, -42f);
        diamond = Box(trigger, "Diamond", Vector2.zero, new Vector2(34f, 34f), Cyan, false).GetComponent<Image>();
        diamond.raycastTarget = false;
        diamond.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
        Image inset = Box(diamond.transform, "Inset", Vector2.zero, new Vector2(29f, 29f), Card, false).GetComponent<Image>();
        inset.raycastTarget = false;
        Text(trigger, "QuestionMark", "?", Vector2.zero, new Vector2(40f, 42f), 30f, true, TextAlignmentOptions.Center);
        Button helpButton = trigger.gameObject.AddComponent<Button>();
        helpButton.targetGraphic = diamond;
        helpButton.onClick.AddListener(() => { visibility.TogglePin(); ApplyVisibility(); });
        AddHover(trigger);

        // Same canvas as the toolbar: global UI hiding also hides help. Its
        // opaque raycast surface prevents clicking controls through the text.
        popup = Box(transform, "HelpPopup", new Vector2(0f, -28f),
            new Vector2(846f, 472f), new Color(0.035f, 0.05f, 0.08f), true);
        Text(popup, "Title", "使用说明", new Vector2(-262f, 202f), new Vector2(270f, 38f), 27f, true);

        for (int i = 0; i < PageNames.Length; i++)
        {
            int page = i;
            Button tab = ActionButton(popup, "Page" + i, PageNames[i], new Vector2(-320f + i * 160f, 151f),
                new Vector2(150f, 40f), () => ShowPage(page));
            tabImages[i] = tab.GetComponent<Image>();
            tabLabels[i] = tab.GetComponentInChildren<TMP_Text>();
        }
        for (int i = 0; i < 6; i++)
        {
            RectTransform card = Box(popup, "Instruction" + i, new Vector2(i % 2 == 0 ? -202f : 202f, 73f - i / 2 * 105f),
                new Vector2(394f, 97f), Card, true);
            card.GetComponent<Image>().raycastTarget = false;
            entryTitles[i] = Text(card, "Heading", "", new Vector2(0f, 26f), new Vector2(366f, 30f), 22f, true);
            entryTitles[i].color = Cyan;
            entryBodies[i] = Text(card, "Body", "", new Vector2(0f, -17f), new Vector2(366f, 57f), 20f, false);
            entryBodies[i].enableWordWrapping = true;
        }
        footer = Text(popup, "HelpHint", "", new Vector2(0f, -210f), new Vector2(802f, 28f), 19f, false, TextAlignmentOptions.Center);
        footer.color = Muted;
        ShowPage(0);
        ApplyVisibility();
    }

    private void ShowPage(int page)
    {
        currentPage = Mathf.Clamp(page, 0, Pages.Length - 1);
        for (int i = 0; i < 6; i++)
        {
            entryTitles[i].text = Pages[currentPage][i].title;
            entryBodies[i].text = Pages[currentPage][i].body;
        }
        for (int i = 0; i < PageNames.Length; i++)
        {
            tabImages[i].color = i == currentPage ? new Color(0.035f, 0.25f, 0.34f) : Card;
            tabLabels[i].color = i == currentPage ? Cyan : Muted;
        }
    }

    private void AddHover(RectTransform target)
    {
        EventTrigger trigger = target.gameObject.AddComponent<EventTrigger>();
        EventTrigger.Entry enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
        enter.callback.AddListener(data =>
        {
            visibility.Enter(((PointerEventData)data).pointerId);
            ApplyVisibility();
        });
        EventTrigger.Entry leave = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
        leave.callback.AddListener(data =>
        {
            visibility.Exit(((PointerEventData)data).pointerId);
            ApplyVisibility();
        });
        trigger.triggers.Add(enter);
        trigger.triggers.Add(leave);
    }

    private void Update()
    {
        if (!InterfaceVisibilityController.IsInterfaceVisible) visibility.Reset();
        ApplyVisibility();
    }

    private void OnDisable()
    {
        visibility.Reset();
        ApplyVisibility();
    }

    private void OnApplicationFocus(bool focused)
    {
        if (focused) return;
        visibility.Reset();
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        if (popup == null) return;
        if (popup.gameObject.activeSelf != visibility.Visible) popup.gameObject.SetActive(visibility.Visible);
        footer.text = visibility.Pinned
            ? "已固定 · 可切换分类阅读 · 再次点击右上角问号关闭"
            : "指向问号查看 · 离开问号关闭 · 点击问号固定后可切换分类";
    }

    private RectTransform Box(Transform parent, string name, Vector2 position, Vector2 size, Color color, bool rounded)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        obj.transform.SetParent(parent, false);
        RectTransform rect = obj.GetComponent<RectTransform>();
        Place(rect, position, size);
        Image image = obj.GetComponent<Image>();
        image.color = color;
        if (rounded) { image.sprite = cardSprite; image.type = Image.Type.Sliced; }
        return rect;
    }

    private TMP_Text Text(Transform parent, string name, string value, Vector2 position, Vector2 size,
        float fontSize, bool bold, TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        obj.transform.SetParent(parent, false);
        Place(obj.GetComponent<RectTransform>(), position, size);
        TMP_Text text = obj.GetComponent<TMP_Text>();
        if (font != null) { text.font = font; text.fontSharedMaterial = font.material; }
        text.text = value;
        text.fontSize = fontSize;
        text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        text.alignment = alignment;
        text.enableWordWrapping = false;
        text.color = new Color(0.89f, 0.94f, 1f);
        text.raycastTarget = false;
        return text;
    }

    private Button ActionButton(Transform parent, string name, string caption, Vector2 position,
        Vector2 size, UnityEngine.Events.UnityAction onClick)
    {
        RectTransform rect = Box(parent, name, position, size, Card, true);
        Button button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = rect.GetComponent<Image>();
        ColorBlock colors = button.colors;
        colors.highlightedColor = new Color(0.65f, 0.9f, 1f);
        colors.pressedColor = new Color(0.4f, 0.75f, 0.9f);
        button.colors = colors;
        button.onClick.AddListener(onClick);
        Text(rect, "Caption", caption, Vector2.zero, size, 21f, true, TextAlignmentOptions.Center);
        return button;
    }

    private static void Place(RectTransform rect, Vector2 position, Vector2 size)
    {
        if (rect == null) return;
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }
}
