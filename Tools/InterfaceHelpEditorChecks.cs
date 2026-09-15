#if UNITY_EDITOR
// Copy into Assets/Editor and invoke Run(outputDirectory) in Edit Mode.
// Uses a preview scene and a temporary font atlas; no networking or source
// scene changes. Screenshots exercise the actual toolbar/help builders.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class InterfaceHelpEditorChecks
{
    public static string Run(string outputDirectory)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before help checks.");
        Scene preview = EditorSceneManager.NewPreviewScene();
        TMP_FontAsset font = null;
        RenderTexture target = null;
        Texture2D pixels = null;
        try
        {
            GameObject root = new GameObject("HelpPreview");
            SceneManager.MoveGameObjectToScene(root, preview);
            TMP_FontAsset source = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/Fonts/NotoSansSCLight-3 SDF.asset");
            font = TMP_FontAsset.CreateFontAsset(source.sourceFontFile);
            font.hideFlags = HideFlags.HideAndDontSave;
            GameObject canvasObj = Child(root.transform, "Canvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(CanvasGroup));
            Canvas canvas = canvasObj.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            GameObject panel = Child(canvasObj.transform, "Dashboard", typeof(RectTransform), typeof(Image));
            GameObject udpObj = Child(panel.transform, "UdpButton", typeof(RectTransform), typeof(Image), typeof(Button));
            UdpTransmissionButton toolbar = udpObj.AddComponent<UdpTransmissionButton>();
            toolbar.buttonText = Child(udpObj.transform, "UdpLabel", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TMP_Text>();
            toolbar.statusText = Child(panel.transform, "Status", typeof(RectTransform), typeof(TextMeshProUGUI)).GetComponent<TMP_Text>();
            toolbar.buttonText.font = toolbar.statusText.font = font;
            toolbar.statusText.text = "PICO  ⇄  PC  未连接";
            typeof(UdpTransmissionButton).GetField("udpButton", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(toolbar, udpObj.GetComponent<Button>());
            typeof(UdpTransmissionButton).GetMethod("BuildInterface", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(toolbar, null);
            InterfaceHelpPanel help = panel.GetComponentInChildren<InterfaceHelpPanel>(true);
            Transform popup = help.transform.Find("HelpPopup");
            Transform trigger = help.transform.Find("HelpButton");
            Require(!popup.gameObject.activeSelf, "help initially closed");
            EventSystem events = Child(root.transform, "Events", typeof(EventSystem)).GetComponent<EventSystem>();
            PointerEventData pointer = new PointerEventData(events) { pointerId = 101 };
            ExecuteEvents.Execute(trigger.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            Require(popup.gameObject.activeSelf, "pointer enter opens help");
            popup.Find("Page1").GetComponent<Button>().onClick.Invoke();
            ExecuteEvents.Execute(trigger.gameObject, pointer, ExecuteEvents.pointerExitHandler);
            Require(!popup.gameObject.activeSelf, "leaving question closes immediately; page click does not pin");
            ExecuteEvents.Execute(popup.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            Require(!popup.gameObject.activeSelf, "panel hover cannot open help");
            ExecuteEvents.Execute(trigger.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.Execute(trigger.gameObject, pointer, ExecuteEvents.pointerClickHandler);
            ExecuteEvents.Execute(trigger.gameObject, pointer, ExecuteEvents.pointerExitHandler);
            Require(popup.gameObject.activeSelf, "clicked help stays open after leaving question");
            ExecuteEvents.Execute(popup.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.Execute(popup.gameObject, pointer, ExecuteEvents.pointerExitHandler);
            Require(popup.gameObject.activeSelf, "panel exit cannot close pinned help");
            ExecuteEvents.Execute(trigger.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            ExecuteEvents.Execute(trigger.gameObject, pointer, ExecuteEvents.pointerClickHandler);
            Require(!popup.gameObject.activeSelf, "second question click closes help");
            ExecuteEvents.Execute(trigger.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            Require(!popup.gameObject.activeSelf, "close does not immediately reopen under pointer");
            ExecuteEvents.Execute(trigger.gameObject, pointer, ExecuteEvents.pointerExitHandler);
            ExecuteEvents.Execute(trigger.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            Require(popup.gameObject.activeSelf, "re-enter opens help again");
            ExecuteEvents.Execute(trigger.gameObject, pointer, ExecuteEvents.pointerClickHandler);

            RectTransform triggerRect = (RectTransform)trigger;
            Require(triggerRect.anchorMin == Vector2.one && triggerRect.anchorMax == Vector2.one,
                "question is anchored to upper-right corner");
            Vector3[] triggerCorners = new Vector3[4];
            Vector3[] statusCorners = new Vector3[4];
            triggerRect.GetWorldCorners(triggerCorners);
            toolbar.statusText.rectTransform.GetWorldCorners(statusCorners);
            Require(triggerCorners[0].x > statusCorners[2].x, "question hit area does not overlap status text");

            Camera camera = Child(root.transform, "Camera", typeof(Camera)).GetComponent<Camera>();
            camera.transform.position = new Vector3(0f, 30f, -1000f);
            camera.orthographic = true;
            camera.orthographicSize = 370f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 2000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.065f, 0.085f, 0.12f);
            camera.cullingMask = 1 << 30;
            camera.scene = preview;
            canvas.worldCamera = camera;
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 30;
            target = new RenderTexture(1280, 950, 24);
            camera.targetTexture = target;
            pixels = new Texture2D(1280, 950, TextureFormat.RGB24, false);
            Directory.CreateDirectory(outputDirectory);
            List<string> problems = new List<string>();
            for (int page = 0; page < 5; page++)
            {
                popup.Find("Page" + page).GetComponent<Button>().onClick.Invoke();
                Canvas.ForceUpdateCanvases();
                foreach (TMP_Text text in canvasObj.GetComponentsInChildren<TMP_Text>()) text.ForceMeshUpdate();
                foreach (TMP_Text text in popup.GetComponentsInChildren<TMP_Text>())
                {
                    text.ForceMeshUpdate();
                    if (text.isTextOverflowing || text.preferredWidth > text.rectTransform.rect.width + 1f ||
                        text.preferredHeight > text.rectTransform.rect.height + 1f)
                        problems.Add("Page " + page + " overflow " + text.text.Replace("\n", " / ") +
                            " preferred=" + text.preferredWidth + "x" + text.preferredHeight + " rect=" + text.rectTransform.rect.size);
                }
                camera.Render();
                RenderTexture previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, 1280, 950), 0, 0);
                    pixels.Apply();
                    File.WriteAllBytes(Path.Combine(outputDirectory, "HC-Teleop-help-" + (page + 1) + ".png"), pixels.EncodeToPNG());
                }
                finally { RenderTexture.active = previous; }
            }

            // The help surface must be the top hit over the original UDP button.
            PointerEventData hit = new PointerEventData(events) { position = camera.WorldToScreenPoint(udpObj.transform.position) };
            List<RaycastResult> hits = new List<RaycastResult>();
            canvasObj.GetComponent<GraphicRaycaster>().Raycast(hit, hits);
            Require(hits.Count > 0 && hits[0].gameObject.transform.IsChildOf(help.transform), "help blocks click-through to UDP");
            CanvasGroup group = canvasObj.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = group.interactable = false;
            Canvas.ForceUpdateCanvases();
            hits.Clear();
            canvasObj.GetComponent<GraphicRaycaster>().Raycast(hit, hits);
            Require(hits.Count == 0, "root CanvasGroup blocks hidden help input");
            Require(Mathf.Approximately(popup.GetComponent<CanvasRenderer>().GetInheritedAlpha(), 0f), "help inherits global alpha");
            if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
            return "PASS: five pages without overflow; upper-right placement; question-only immediate hover and click toggle; no page auto-pin; click-through blocking and parent visibility.";
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(preview);
            if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            if (pixels != null) UnityEngine.Object.DestroyImmediate(pixels);
            if (font != null)
            {
                foreach (Texture2D atlas in font.atlasTextures) if (atlas != null) UnityEngine.Object.DestroyImmediate(atlas);
                UnityEngine.Object.DestroyImmediate(font.material);
                UnityEngine.Object.DestroyImmediate(font);
            }
        }
    }

    private static GameObject Child(Transform parent, string name, params Type[] components)
    {
        GameObject obj = new GameObject(name, components);
        obj.transform.SetParent(parent, false);
        return obj;
    }

    private static void Require(bool pass, string message)
    {
        if (!pass) throw new InvalidOperationException(message);
    }
}
#endif
