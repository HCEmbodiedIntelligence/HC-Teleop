using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The existing connection label acts as the dropdown button. Expanding the
// list temporarily covers the toolbar; closed state consumes no extra layout.
public sealed class DeviceSourceSelector : MonoBehaviour
{
    private UdpPoseSender sender;
    private RectTransform panel;
    private TMP_Text caption;
    private Button trigger;
    private GameObject overlay;
    private string signature;

    public void Configure(UdpPoseSender source, RectTransform parent)
    {
        sender = source;
        panel = parent;
        caption = GetComponent<TMP_Text>();
        if (trigger == null)
        {
            trigger = GetComponent<Button>();
            if (trigger == null) trigger = gameObject.AddComponent<Button>();
            trigger.targetGraphic = caption;
            trigger.transition = Selectable.Transition.None;
            trigger.onClick.AddListener(Toggle);
        }
        bool multiple = sender != null && (sender.DeviceSources.Length > 1 ||
            (sender.DeviceSources.Length == 1 && !sender.HasReceiver));
        trigger.interactable = multiple;
        caption.raycastTarget = multiple;
    }

    private void Toggle()
    {
        if (overlay != null) { Close(); return; }
        if (sender == null || sender.DeviceSources.Length == 0 || GripHeld()) return;
        var sources = sender.DeviceSources;
        signature = Signature();
        var blocker = Rect("SourceSelectionOverlay", panel);
        blocker.anchorMin = Vector2.zero;
        blocker.anchorMax = Vector2.one;
        blocker.offsetMin = blocker.offsetMax = Vector2.zero;
        overlay = blocker.gameObject;
        var blockerImage = overlay.AddComponent<Image>();
        blockerImage.color = Color.clear;
        var outside = overlay.AddComponent<Button>();
        outside.transition = Selectable.Transition.None;
        outside.onClick.AddListener(Close);

        float height = Mathf.Min(sources.Length, 5) * 48f + 12f;
        var list = Rect("DeviceSourceDropdown", blocker);
        list.anchorMin = list.anchorMax = new Vector2(0.5f, 1f);
        list.pivot = new Vector2(0.5f, 1f);
        list.anchoredPosition = new Vector2(0f, -66f);
        list.sizeDelta = new Vector2(580f, height);
        var background = list.gameObject.AddComponent<Image>();
        background.color = new Color(0.04f, 0.07f, 0.11f, 1f);
        var viewport = Rect("Viewport", list);
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = new Vector2(6f, 6f);
        viewport.offsetMax = new Vector2(-6f, -6f);
        viewport.gameObject.AddComponent<Image>().color = Color.white;
        viewport.gameObject.AddComponent<Mask>().showMaskGraphic = false;
        var content = Rect("Content", viewport);
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = Vector2.one;
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = new Vector2(0f, sources.Length * 48f);
        var scroll = list.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        for (int i = 0; i < sources.Length; i++)
        {
            var source = sources[i];
            string ip = source.Ip;
            var row = Rect("Source_" + ip, content);
            row.anchorMin = new Vector2(0f, 1f);
            row.anchorMax = Vector2.one;
            row.pivot = new Vector2(0.5f, 1f);
            row.anchoredPosition = new Vector2(0f, -i * 48f);
            row.sizeDelta = new Vector2(0f, 46f);
            var image = row.gameObject.AddComponent<Image>();
            image.color = ip == sender.ReceiverIpAddress ? new Color(0f, 0.3f, 0.4f) : new Color(0.09f, 0.13f, 0.18f);
            var button = row.gameObject.AddComponent<Button>();
            button.onClick.AddListener(() => { sender.SelectDeviceSource(ip); Close(); });
            var label = Rect("Label", row).gameObject.AddComponent<TextMeshProUGUI>();
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = new Vector2(12f, 0f);
            label.rectTransform.offsetMax = new Vector2(-12f, 0f);
            label.font = caption.font;
            label.fontSize = 20f;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.richText = false;
            label.raycastTarget = false;
            label.text = ip + ":" + source.Port + (source.Busy ? "  · 已占用 " + source.Owner : "  · 可连接");
        }
    }

    private static RectTransform Rect(string name, Transform parent)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }

    private string Signature() => sender == null ? string.Empty :
        string.Join("|", Array.ConvertAll(sender.DeviceSources, s => s.Ip + ":" + s.Port + ":" + s.Busy + ":" + s.Owner));

    private bool GripHeld()
    {
        foreach (var node in new[] { UnityEngine.XR.XRNode.LeftHand, UnityEngine.XR.XRNode.RightHand })
        {
            var device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(node);
            if ((device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.gripButton, out bool pressed) && pressed) ||
                (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.grip, out float value) && value >= 0.55f)) return true;
        }
        return false;
    }

    private void Update()
    {
        if (overlay != null && (!InterfaceVisibilityController.IsInterfaceVisible || GripHeld() || Signature() != signature)) Close();
    }
    private void OnDisable() { Close(); }
    private void OnDestroy() { Close(); }
    private void Close()
    {
        if (overlay != null) { overlay.SetActive(false); Destroy(overlay); }
        overlay = null;
    }
}
