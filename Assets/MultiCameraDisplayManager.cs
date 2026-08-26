using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

public class MultiCameraDisplayManager : MonoBehaviour
{
    [Serializable]
    private class CameraStreamInfo
    {
        public string id;
        public string name;
        public string topic;
        public string state;
        public float capture_fps;
        public float webrtc_send_fps;
    }

    [Serializable]
    private class CameraStatusResponse
    {
        public CameraStreamInfo[] streams;
    }

    [Header("自动发现")]
    public UdpPoseSender poseSender;
    public CameraFrameViewer templateViewer;
    public int serverPort = 7876;
    [Range(1f, 15f)] public float discoveryIntervalSeconds = 3f;

    [Header("多窗口布局")]
    [Range(1, 3)] public int maximumColumns = 2;
    [Min(180f)] public float multiCameraPanelHeight = 260f;
    [Min(0f)] public float horizontalGap = 36f;
    [Min(0f)] public float verticalGap = 36f;

    private readonly Dictionary<string, CameraFrameViewer> viewers =
        new Dictionary<string, CameraFrameViewer>();
    private readonly Dictionary<string, bool> requestedVisibility =
        new Dictionary<string, bool>();
    private readonly List<string> streamOrder = new List<string>();
    private Coroutine discoveryCoroutine;
    private string configuredServerIp = string.Empty;
    private bool allVisible = true;
    private bool hasLayoutCenter;
    private Vector2 layoutCenter;

    public int CameraCount => viewers.Count;
    public int ConnectedCount => viewers.Values.Count(viewer =>
        viewer != null && viewer.IsVideoConnected);
    public bool AllVisible => viewers.Count > 0 && viewers.Values.All(viewer =>
        viewer != null && viewer.IsWindowVisible);
    public string[] StreamIds => streamOrder
        .Where(id => viewers.ContainsKey(id) && viewers[id] != null)
        .ToArray();
    public float AverageVideoFps
    {
        get
        {
            float[] active = viewers.Values
                .Where(viewer => viewer != null && viewer.DisplayedVideoFps > 0f)
                .Select(viewer => viewer.DisplayedVideoFps)
                .ToArray();
            return active.Length > 0 ? active.Average() : 0f;
        }
    }

    public string FpsSummary => string.Join(" / ", viewers.Values
        .Where(viewer => viewer != null)
        .Select(viewer => viewer.DisplayedVideoFps > 0f
            ? viewer.DisplayedVideoFps.ToString("F0")
            : "--"));

    public void Configure(CameraFrameViewer template, UdpPoseSender sender)
    {
        templateViewer = template;
        poseSender = sender;
        if (templateViewer != null && viewers.Count == 0)
            viewers[templateViewer.StreamId] = templateViewer;
    }

    private void Awake()
    {
        if (poseSender == null)
            poseSender = FindObjectOfType<UdpPoseSender>(true);
        if (templateViewer == null)
            templateViewer = FindObjectOfType<CameraFrameViewer>(true);
        if (templateViewer != null)
            viewers[templateViewer.StreamId] = templateViewer;
    }

    private void OnEnable()
    {
        InterfaceLayoutResetService.ResetCompleted += OnInterfaceReset;
        discoveryCoroutine = StartCoroutine(DiscoveryLoop());
    }

    private void OnDisable()
    {
        InterfaceLayoutResetService.ResetCompleted -= OnInterfaceReset;
        if (discoveryCoroutine != null)
            StopCoroutine(discoveryCoroutine);
        discoveryCoroutine = null;
    }

    public void SetAllVisible(bool visible)
    {
        allVisible = visible;
        foreach (KeyValuePair<string, CameraFrameViewer> pair in viewers)
        {
            requestedVisibility[pair.Key] = visible;
            if (pair.Value != null)
                pair.Value.SetWindowVisible(visible);
        }
    }

    public void ToggleAllVisible()
    {
        SetAllVisible(!AllVisible);
    }

    public void ToggleStream(string streamId)
    {
        SetStreamVisible(streamId, !IsStreamVisible(streamId));
    }

    public void SetStreamVisible(string streamId, bool visible)
    {
        if (string.IsNullOrEmpty(streamId))
            return;

        requestedVisibility[streamId] = visible;
        if (viewers.TryGetValue(streamId, out CameraFrameViewer viewer) &&
            viewer != null)
        {
            viewer.SetWindowVisible(visible);
        }

        allVisible = AllVisible;
    }

    public bool IsStreamVisible(string streamId)
    {
        return viewers.TryGetValue(streamId, out CameraFrameViewer viewer) &&
               viewer != null && viewer.IsWindowVisible;
    }

    public bool IsStreamConnected(string streamId)
    {
        return viewers.TryGetValue(streamId, out CameraFrameViewer viewer) &&
               viewer != null && viewer.IsVideoConnected;
    }

    public float GetStreamFps(string streamId)
    {
        return viewers.TryGetValue(streamId, out CameraFrameViewer viewer) &&
               viewer != null ? viewer.DisplayedVideoFps : 0f;
    }

    public string GetStreamDisplayName(string streamId)
    {
        return viewers.TryGetValue(streamId, out CameraFrameViewer viewer) &&
               viewer != null && !string.IsNullOrEmpty(viewer.StreamDisplayName)
            ? viewer.StreamDisplayName
            : streamId;
    }

    private IEnumerator DiscoveryLoop()
    {
        while (true)
        {
            if (poseSender == null)
                poseSender = FindObjectOfType<UdpPoseSender>(true);

            string serverIp = poseSender != null
                ? poseSender.ReceiverIpAddress
                : string.Empty;
            if (string.IsNullOrEmpty(serverIp))
            {
                yield return new WaitForSecondsRealtime(0.5f);
                continue;
            }

            string url = "http://" + serverIp + ":" + serverPort +
                         "/api/camera/status";
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 5;
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    CameraStatusResponse response = null;
                    try
                    {
                        response = JsonUtility.FromJson<CameraStatusResponse>(
                            request.downloadHandler.text);
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning("多相机列表解析失败: " + exception.Message);
                    }

                    if (response != null && response.streams != null &&
                        response.streams.Length > 0)
                    {
                        Debug.Log("[Multi Camera] discovered: " + string.Join(", ",
                            response.streams.Select(stream => stream != null ? stream.id : "null")));
                        bool serverChanged = configuredServerIp != serverIp;
                        configuredServerIp = serverIp;
                        ApplyStreams(response.streams, serverChanged);
                    }
                }
            }

            yield return new WaitForSecondsRealtime(discoveryIntervalSeconds);
        }
    }

    private void ApplyStreams(CameraStreamInfo[] streams, bool forceRestart)
    {
        if (templateViewer == null)
            return;

        CameraStreamInfo[] validStreams = streams
            .Where(stream => stream != null && !string.IsNullOrEmpty(stream.id))
            .GroupBy(stream => stream.id)
            .Select(group => group.First())
            .ToArray();
        if (validStreams.Length == 0)
            return;

        RectTransform templatePanel = ResolvePanel(templateViewer);
        if (templatePanel == null)
            return;

        if (!hasLayoutCenter)
        {
            layoutCenter = templatePanel.anchoredPosition;
            hasLayoutCenter = true;
        }

        string[] incomingIds = validStreams.Select(stream => stream.id).ToArray();
        bool streamSetChanged = streamOrder.Count != incomingIds.Length ||
            !streamOrder.SequenceEqual(incomingIds);

        // Never deactivate the template while rebuilding the grid.
        // CameraFrameViewer.OnDisable closes its peer and can stop the shared
        // WebRTC.Update coroutine that all camera windows depend on.

        var next = new Dictionary<string, CameraFrameViewer>();
        for (int index = 0; index < validStreams.Length; index++)
        {
            CameraStreamInfo stream = validStreams[index];
            CameraFrameViewer viewer;
            if (index == 0)
            {
                viewer = templateViewer;
            }
            else if (!viewers.TryGetValue(stream.id, out viewer) || viewer == null ||
                     viewer == templateViewer)
            {
                GameObject clone = Instantiate(
                    templatePanel.gameObject,
                    templatePanel.parent,
                    false);
                clone.name = "VideoPanel_" + stream.id;
                viewer = clone.GetComponentInChildren<CameraFrameViewer>(true);
            }

            if (viewer == null)
                continue;

            viewer.useNativeResolution = validStreams.Length == 1;
            if (validStreams.Length > 1)
                viewer.videoPanelHeight = multiCameraPanelHeight;
            viewer.ConfigureStream(
                stream.id,
                string.IsNullOrEmpty(stream.name) ? stream.id : stream.name,
                "/offer/" + stream.id);
            next[stream.id] = viewer;
        }

        foreach (KeyValuePair<string, CameraFrameViewer> pair in viewers)
        {
            if (pair.Value != null && pair.Value != templateViewer &&
                !next.ContainsValue(pair.Value))
            {
                RectTransform oldPanel = ResolvePanel(pair.Value);
                if (oldPanel != null)
                    Destroy(oldPanel.gameObject);
            }
        }

        viewers.Clear();
        foreach (KeyValuePair<string, CameraFrameViewer> pair in next)
            viewers[pair.Key] = pair.Value;

        streamOrder.Clear();
        streamOrder.AddRange(incomingIds.Where(id => viewers.ContainsKey(id)));

        if (streamSetChanged)
            LayoutPanels(layoutCenter);

        foreach (KeyValuePair<string, CameraFrameViewer> pair in viewers)
        {
            CameraFrameViewer viewer = pair.Value;
            if (!requestedVisibility.TryGetValue(pair.Key, out bool visible))
            {
                visible = allVisible;
                requestedVisibility[pair.Key] = visible;
            }
            viewer.SetWindowVisible(visible);
            if (forceRestart && visible)
                viewer.RestartConnection();
        }

        allVisible = AllVisible;
    }

    private void OnInterfaceReset(string source, int resetCount)
    {
        if (viewers.Count == 0)
            return;

        // GripDraggablePanel has already restored the dashboard and camera
        // group to the visible area. Preserve that larger reset scale and only
        // re-apply the stable 2x2 camera grid around its current centre.
        Vector2 sum = Vector2.zero;
        int count = 0;
        foreach (CameraFrameViewer viewer in viewers.Values)
        {
            RectTransform panel = ResolvePanel(viewer);
            if (panel == null)
                continue;
            sum += panel.anchoredPosition;
            count++;
        }

        if (count == 0)
            return;

        layoutCenter = sum / count;
        hasLayoutCenter = true;
        // ResetAllPanels already placed the cameras in a fitted 2x2 grid. Do
        // not immediately overwrite those scaled positions with the normal
        // unscaled discovery layout.
    }

    private void LayoutPanels(Vector2 center)
    {
        CameraFrameViewer[] ordered = streamOrder
            .Where(id => viewers.ContainsKey(id) && viewers[id] != null)
            .Select(id => viewers[id])
            .ToArray();
        if (ordered.Length == 0)
            return;

        int columns = Mathf.Min(maximumColumns, ordered.Length);
        int rows = Mathf.CeilToInt((float)ordered.Length / columns);
        float panelWidth = multiCameraPanelHeight * (16f / 9f) + 24f;
        float panelHeight = multiCameraPanelHeight + 88f;

        for (int index = 0; index < ordered.Length; index++)
        {
            int row = index / columns;
            int column = index % columns;
            int itemsInRow = Mathf.Min(columns, ordered.Length - row * columns);
            float x = center.x +
                (column - (itemsInRow - 1) * 0.5f) * (panelWidth + horizontalGap);
            float y = center.y +
                ((rows - 1) * 0.5f - row) * (panelHeight + verticalGap);

            CameraFrameViewer viewer = ordered[index];
            viewer.ApplyLayoutNow();
            RectTransform panel = ResolvePanel(viewer);
            if (panel != null)
                panel.anchoredPosition = new Vector2(x, y);
        }
    }

    private static RectTransform ResolvePanel(CameraFrameViewer viewer)
    {
        if (viewer == null)
            return null;
        if (viewer.videoPanel != null)
            return viewer.videoPanel;
        if (viewer.targetImage != null)
            return viewer.targetImage.rectTransform.parent as RectTransform;
        return null;
    }
}
