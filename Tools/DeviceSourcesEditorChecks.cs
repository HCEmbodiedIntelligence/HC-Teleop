#if UNITY_EDITOR
// Copy into Assets/Editor and run DeviceSourcesEditorChecks.Run in batch Edit Mode.
// Uses an isolated preview scene; does not enter Play Mode or start networking.
using System;
using System.IO;
using System.Net;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class DeviceSourcesEditorChecks
{
    private static int assertions;
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL: " + message);
        assertions++;
    }
    private static void Field(object value, string name, object data) => value.GetType().GetField(name, Flags).SetValue(value, data);
    private static object Field(object value, string name) => value.GetType().GetField(name, Flags).GetValue(value);
    private static void Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Flags).Invoke(value, args);

    public static void Run()
    {
        string resultPath = Environment.GetEnvironmentVariable("HC_DEVICE_CHECK_RESULT");
        var preview = EditorSceneManager.NewPreviewScene();
        try
        {
            var root = new GameObject("SourceCheck", typeof(RectTransform), typeof(Canvas));
            SceneManager.MoveGameObjectToScene(root, preview);
            var panel = root.GetComponent<RectTransform>();
            panel.sizeDelta = new Vector2(900f, 560f);
            var senderObject = new GameObject("Sender");
            senderObject.transform.SetParent(root.transform);
            var sender = senderObject.AddComponent<UdpPoseSender>();
            sender.enabled = false;
            var registry = (DeviceSourceRegistry)Field(sender, "sourceRegistry");
            double now = Time.realtimeSinceStartupAsDouble;
            registry.Observe("10.0.0.1", "PICO_RECEIVER_V1|5005", now);
            registry.Observe("10.0.0.2", "PICO_RECEIVER_V1|5005", now);
            Call(sender, "ApplySelectedSource", now);
            Check(!sender.HasReceiver, "two sources do not auto-select");
            var labelObject = new GameObject("Status", typeof(RectTransform), typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(panel, false);
            var caption = labelObject.GetComponent<TMP_Text>();
            var selector = labelObject.AddComponent<DeviceSourceSelector>();
            selector.Configure(sender, panel);
            Check(caption.raycastTarget && labelObject.GetComponent<Button>().interactable, "multiple sources enable clickable dropdown");
            labelObject.GetComponent<Button>().onClick.Invoke();
            var overlay = panel.Find("SourceSelectionOverlay");
            Check(overlay != null, "click opens dropdown overlay");
            var content = overlay.Find("DeviceSourceDropdown/Viewport/Content");
            Check(content.childCount == 2, "dropdown contains both source IPs");
            Check(content.GetChild(0).GetComponentInChildren<TMP_Text>().text.Contains("10.0.0.1"), "dropdown labels show source IP");
            content.GetChild(0).GetComponent<Button>().onClick.Invoke();
            Check(sender.ReceiverIpAddress == "10.0.0.1", "clicking row selects source");
            Field(sender, "transmissionEnabled", true);
            Field(sender, "poseSenderActive", true);
            Field(sender, "cachedLocalAddresses", new[] { IPAddress.Parse("10.0.0.90") });
            Field(sender, "verifiedServerIp", "10.0.0.1");
            Field(sender, "cameraOwnerIp", "10.0.0.90");
            Field(sender, "cameraOwnerLocked", true);
            Field(sender, "lastOwnershipReply", Time.realtimeSinceStartupAsDouble);
            Check(sender.HasCameraControl, "fresh middleware owner confirmation grants camera");
            Field(sender, "cameraOwnerIp", "10.0.0.91");
            Check(!sender.HasCameraControl, "other headset owner denies camera despite UDP enabled");
            Field(sender, "cameraOwnerIp", "10.0.0.90");
            ((MiddlewareSafetyState)Field(sender, "safetyState")).Apply(new MiddlewareEventJson {
                kind = "safety_stop", timestamp = 10, payload = new MiddlewareEventPayload { reason = "old source" } });
            sender.SelectDeviceSource("10.0.0.2");
            Check(sender.ReceiverIpAddress == "10.0.0.2", "explicit switch updates receiver");
            Check(!sender.IsTransmissionEnabled && !(bool)Field(sender, "poseSenderActive"), "switch stops transmission and worker before new target");
            Check(!sender.HasCameraControl, "previous owner grant is invalidated");
            Check(!sender.IsEmergencyStopped, "previous source safety latch is cleared");
            var cameraObject = new GameObject("Camera", typeof(RectTransform), typeof(RawImage));
            cameraObject.SetActive(false);
            cameraObject.transform.SetParent(root.transform, false);
            var viewer = cameraObject.AddComponent<CameraFrameViewer>();
            viewer.poseSender = sender;
            viewer.targetImage = cameraObject.GetComponent<RawImage>();
            viewer.targetImage.texture = Texture2D.whiteTexture;
            var negotiation = (System.Collections.IEnumerator)typeof(CameraFrameViewer)
                .GetMethod("ConnectToServer", Flags).Invoke(viewer, new object[] { "10.0.0.2" });
            Check(!negotiation.MoveNext(), "unauthorized negotiation exits before WebRTC peer creation");
            Call(viewer, "Update");
            Check(viewer.targetImage.texture == null, "revoked access clears last frame");
            Check(Field(viewer, "peerConnection") == null, "revoked access leaves no peer");
            registry.Observe("10.0.0.2", "PICO_RECEIVER_BUSY|5005|10.0.0.91", Time.realtimeSinceStartupAsDouble);
            Call(sender, "ApplySelectedSource", Time.realtimeSinceStartupAsDouble);
            Check(sender.IsOccupiedByOther && !sender.HasCameraControl, "busy reply blocks camera");
            string result = "PASS: " + assertions + " source-selector and camera-gate Unity assertions";
            Debug.Log(result);
            if (!string.IsNullOrEmpty(resultPath)) File.WriteAllText(resultPath, result);
        }
        catch (Exception error)
        {
            if (!string.IsNullOrEmpty(resultPath)) File.WriteAllText(resultPath, "FAIL: " + error);
            throw;
        }
        finally { EditorSceneManager.ClosePreviewScene(preview); }
    }
}
#endif
