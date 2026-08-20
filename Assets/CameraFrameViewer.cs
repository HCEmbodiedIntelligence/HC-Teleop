using System;
using System.Collections;
using System.Linq;
using System.Text;
using TMPro;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class CameraFrameViewer : MonoBehaviour
{
    [Serializable]
    private class SessionDescriptionJson
    {
        public string sdp;
        public string type;
    }

    [Header("自动发现与信令")]
    public UdpPoseSender poseSender;
    public int videoPort = 7876;
    public string offerPath = "/offer";
    [Range(1f, 10f)] public float reconnectDelaySeconds = 2f;

    [Header("显示")]
    public RawImage targetImage;
    public TMP_Text videoStatusText;
    public AspectRatioFitter aspectRatioFitter;
    public RectTransform videoPanel;
    public bool useNativeResolution = true;
    [Min(200f)] public float videoPanelHeight = 540f;

    private RTCPeerConnection peerConnection;
    private VideoStreamTrack receivedVideoTrack;
    private RTCRtpReceiver videoReceiver;
    private Coroutine webRtcUpdateCoroutine;
    private Coroutine connectionCoroutine;
    private Coroutine statsCoroutine;
    private string currentServerIp = string.Empty;
    private string connectionState = "等待发现 PC";
    private string lastError = string.Empty;
    private int consecutiveFailures;
    private int frameWidth;
    private int frameHeight;
    private float displayedVideoFps;
    private float statusRefreshTimer;
    private float lastFrameRealtime = -100f;
    private uint previousDecodedFrames;
    private ulong previousBytesReceived;
    private ulong previousPacketsReceived;
    private float previousStatsRealtime;
    private bool connectionNeedsRestart;
    private CanvasGroup videoWindowCanvasGroup;
    private bool isWindowVisible = true;
    private int receivedFramesCount;

    private const float PanelPadding = 12f;
    private const float StatusBarHeight = 44f;
    private const float StatusBarSpacing = 8f;
    private static Sprite panelRoundedCardSprite;
    private static Sprite statusPillSprite;
    private GameObject statusBarBgObj;

    public float DisplayedVideoFps => displayedVideoFps;
    public bool IsVideoConnected =>
        receivedVideoTrack != null &&
        (Time.realtimeSinceStartup - lastFrameRealtime < 10f ||
         (peerConnection != null && (peerConnection.IceConnectionState == RTCIceConnectionState.Connected || peerConnection.IceConnectionState == RTCIceConnectionState.Completed)));
    public bool IsWindowVisible => isWindowVisible;

    public string CompactStatus
    {
        get
        {
            if (!IsVideoConnected)
                return "相机: " + connectionState;

            return "相机: WebRTC H.264  " + frameWidth + "×" + frameHeight + "\n" +
                   "视频 FPS: " + displayedVideoFps.ToString("F1");
        }
    }

    private void Awake()
    {
        if (targetImage != null)
        {
            targetImage.raycastTarget = false;
            if (aspectRatioFitter == null)
                aspectRatioFitter = targetImage.GetComponent<AspectRatioFitter>();
            if (videoPanel == null)
                videoPanel = targetImage.rectTransform.parent as RectTransform;
        }

        if (aspectRatioFitter != null)
            aspectRatioFitter.enabled = false;

        if (videoStatusText == null)
        {
            if (videoPanel != null)
                videoStatusText = videoPanel.GetComponentInChildren<TMP_Text>(true);
            if (videoStatusText == null && targetImage != null)
                videoStatusText = targetImage.GetComponentInChildren<TMP_Text>(true);
        }

        UpdateVideoPanelLayout();
    }

    public void SetWindowVisible(bool visible)
    {
        isWindowVisible = visible;
        EnsureVideoWindowCanvasGroup();

        if (videoWindowCanvasGroup == null)
            return;

        videoWindowCanvasGroup.alpha = visible ? 1f : 0f;
        videoWindowCanvasGroup.interactable = visible;
        videoWindowCanvasGroup.blocksRaycasts = visible;
    }

    private void EnsureVideoWindowCanvasGroup()
    {
        if (videoPanel == null && targetImage != null)
            videoPanel = targetImage.rectTransform.parent as RectTransform;

        if (videoPanel == null)
            return;

        if (videoWindowCanvasGroup == null)
        {
            videoWindowCanvasGroup = videoPanel.GetComponent<CanvasGroup>();
            if (videoWindowCanvasGroup == null)
                videoWindowCanvasGroup = videoPanel.gameObject.AddComponent<CanvasGroup>();
        }
    }

    private void OnEnable()
    {
        webRtcUpdateCoroutine = StartCoroutine(WebRTC.Update());
        connectionCoroutine = StartCoroutine(ConnectionLoop());
        statsCoroutine = StartCoroutine(VideoStatsLoop());
    }

    private void OnDisable()
    {
        if (connectionCoroutine != null)
            StopCoroutine(connectionCoroutine);
        if (webRtcUpdateCoroutine != null)
            StopCoroutine(webRtcUpdateCoroutine);
        if (statsCoroutine != null)
            StopCoroutine(statsCoroutine);

        connectionCoroutine = null;
        webRtcUpdateCoroutine = null;
        statsCoroutine = null;
        ClosePeerConnection(true);
    }

    private void Update()
    {
        statusRefreshTimer += Time.unscaledDeltaTime;

        // 仅在已建立视频轨道且完全无任何数据 15 秒以上，且 ICE 已失败时才触发重连
        if (receivedVideoTrack != null &&
            lastFrameRealtime > 0f &&
            Time.realtimeSinceStartup - lastFrameRealtime > 15f)
        {
            if (peerConnection == null ||
                peerConnection.IceConnectionState == RTCIceConnectionState.Failed ||
                peerConnection.IceConnectionState == RTCIceConnectionState.Closed)
            {
                connectionState = "视频流中断，正在重连";
                connectionNeedsRestart = true;
            }
        }

        if (statusRefreshTimer >= 0.25f)
        {
            statusRefreshTimer = 0f;
            RefreshStatusText();
        }
    }

    private IEnumerator ConnectionLoop()
    {
        while (true)
        {
            if (poseSender == null)
            {
                connectionState = "未设置 UdpPoseSender";
                yield return new WaitForSecondsRealtime(1f);
                continue;
            }

            string discoveredIp = poseSender.ReceiverIpAddress;

            // 当发现有效 IP 时，记录并更新
            if (!string.IsNullOrEmpty(discoveredIp))
            {
                if (currentServerIp != discoveredIp)
                {
                    ClosePeerConnection(false);
                    currentServerIp = discoveredIp;
                    connectionNeedsRestart = false;
                }
            }

            // 若从未发现过任何 PC IP，进入等待发现状态
            if (string.IsNullOrEmpty(currentServerIp))
            {
                connectionState = "等待发现 PC";
                yield return new WaitForSecondsRealtime(0.5f);
                continue;
            }

            // 如果当前已有连接且不需要重启，保持常驻检测
            if (peerConnection != null && !connectionNeedsRestart)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                continue;
            }

            // 重启连接前清理（保留当前最后一帧，避免白屏闪烁）
            if (connectionNeedsRestart && peerConnection != null)
            {
                ClosePeerConnection(false);
                connectionNeedsRestart = false;
            }

            if (peerConnection == null)
            {
                yield return StartCoroutine(ConnectToServer(currentServerIp));
                if (peerConnection == null || connectionNeedsRestart)
                    yield return new WaitForSecondsRealtime(reconnectDelaySeconds);
            }
            else
            {
                yield return new WaitForSecondsRealtime(0.25f);
            }
        }
    }

    private IEnumerator ConnectToServer(string serverIp)
    {
        connectionState = "正在协商 WebRTC";
        lastError = string.Empty;

        RTCConfiguration configuration = default;
        peerConnection = new RTCPeerConnection(ref configuration);
        peerConnection.OnIceConnectionChange = OnIceConnectionChange;
        peerConnection.OnConnectionStateChange = OnConnectionStateChange;
        peerConnection.OnTrack = OnTrack;

        var init = new RTCRtpTransceiverInit
        {
            direction = RTCRtpTransceiverDirection.RecvOnly
        };
        RTCRtpTransceiver transceiver =
            peerConnection.AddTransceiver(TrackKind.Video, init);

        RTCRtpCodecCapability[] h264Codecs =
            RTCRtpSender.GetCapabilities(TrackKind.Video).codecs
                .Where(codec =>
                    string.Equals(codec.mimeType, "video/H264", StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (h264Codecs.Length > 0)
        {
            RTCErrorType codecError = transceiver.SetCodecPreferences(h264Codecs);
            if (codecError != RTCErrorType.None)
            {
                Debug.LogWarning("设置 H.264 首选项提示: " + codecError);
            }
        }

        RTCSessionDescriptionAsyncOperation offerOperation = peerConnection.CreateOffer();
        yield return offerOperation;
        if (offerOperation.IsError)
        {
            FailConnection("创建 Offer 失败: " + offerOperation.Error.message);
            yield break;
        }

        RTCSessionDescription offer = offerOperation.Desc;
        RTCSetSessionDescriptionAsyncOperation localOperation =
            peerConnection.SetLocalDescription(ref offer);
        yield return localOperation;
        if (localOperation.IsError)
        {
            FailConnection("设置本地 SDP 失败: " + localOperation.Error.message);
            yield break;
        }

        // 局域网无 Trickle ICE，等待收集完成或超时 3 秒
        float iceDeadline = Time.realtimeSinceStartup + 3f;
        while (peerConnection != null &&
               peerConnection.GatheringState != RTCIceGatheringState.Complete &&
               Time.realtimeSinceStartup < iceDeadline)
        {
            yield return null;
        }

        if (peerConnection == null)
            yield break;

        RTCSessionDescription localDescription = peerConnection.LocalDescription;
        var offerJson = new SessionDescriptionJson
        {
            sdp = localDescription.sdp,
            type = "offer"
        };

        byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(offerJson));
        int[] portsToTry = (videoPort > 0) ? new int[] { videoPort, 7876 } : new int[] { 7876 };
        string[] pathsToTry = new string[] { offerPath, "/offer", "/api/webrtc/offer" };

        bool success = false;
        string lastHttpError = string.Empty;

        foreach (int port in portsToTry.Distinct())
        {
            foreach (string path in pathsToTry.Distinct())
            {
                string normPath = path.StartsWith("/") ? path : "/" + path;
                string url = "http://" + serverIp + ":" + port + normPath;
                using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
                {
                    request.uploadHandler = new UploadHandlerRaw(body);
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = 6;
                    yield return request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        string bodyText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                        lastHttpError = !string.IsNullOrEmpty(bodyText)
                            ? request.error + " (" + bodyText.Trim() + ")"
                            : request.error + " (端口: " + port + ")";
                        continue;
                    }

                    SessionDescriptionJson answerJson = null;
                    try
                    {
                        answerJson = JsonUtility.FromJson<SessionDescriptionJson>(request.downloadHandler.text);
                    }
                    catch (Exception exception)
                    {
                        lastHttpError = "JSON解析错误: " + exception.Message;
                        continue;
                    }

                    if (answerJson == null || string.IsNullOrEmpty(answerJson.sdp))
                    {
                        lastHttpError = "电脑端未返回有效 SDP";
                        continue;
                    }

                    var answer = new RTCSessionDescription
                    {
                        type = RTCSdpType.Answer,
                        sdp = answerJson.sdp
                    };

                    RTCSetSessionDescriptionAsyncOperation remoteOperation =
                        peerConnection.SetRemoteDescription(ref answer);
                    yield return remoteOperation;
                    if (remoteOperation.IsError)
                    {
                        FailConnection("设置远端 SDP 失败: " + remoteOperation.Error.message);
                        yield break;
                    }

                    success = true;
                    break;
                }
            }
            if (success) break;
        }

        if (!success)
        {
            FailConnection("信令失败: " + lastHttpError);
            yield break;
        }

        consecutiveFailures = 0;
        connectionState = "等待视频帧";
    }

    private void OnTrack(RTCTrackEvent trackEvent)
    {
        if (!(trackEvent.Track is VideoStreamTrack videoTrack))
            return;

        receivedVideoTrack = videoTrack;
        videoReceiver = trackEvent.Transceiver.Receiver;
        receivedVideoTrack.OnVideoReceived += OnVideoReceived;
        lastFrameRealtime = Time.realtimeSinceStartup;
        connectionState = "已连接 H.264";
    }

    private void OnVideoReceived(Texture texture)
    {
        if (texture == null)
            return;

        // 收到画面纹理即时更新活跃时间戳
        lastFrameRealtime = Time.realtimeSinceStartup;
        receivedFramesCount++;

        if (targetImage != null && targetImage.texture != texture)
            targetImage.texture = texture;

        frameWidth = texture.width;
        frameHeight = texture.height;
        if (aspectRatioFitter != null && frameHeight > 0)
            aspectRatioFitter.aspectRatio = (float)frameWidth / frameHeight;
        UpdateVideoPanelLayout();

        connectionState = "已连接 H.264";
    }

    private IEnumerator VideoStatsLoop()
    {
        var wait = new WaitForSecondsRealtime(1f);

        while (true)
        {
            yield return wait;

            float now = Time.realtimeSinceStartup;

            // 1. 提取底层解码与 RTP 接收统计
            if (videoReceiver != null)
            {
                RTCStatsReportAsyncOperation operation = videoReceiver.GetStats();
                yield return operation;

                if (!operation.IsError && operation.Value != null)
                {
                    RTCStatsReport report = operation.Value;
                    RTCInboundRTPStreamStats inbound = report.Stats.Values
                        .OfType<RTCInboundRTPStreamStats>()
                        .FirstOrDefault();

                    if (inbound != null)
                    {
                        ulong currentBytes = inbound.bytesReceived;
                        ulong currentPackets = inbound.packetsReceived;
                        uint currentFrames = inbound.framesDecoded > 0 ? inbound.framesDecoded : inbound.framesReceived;

                        // 只要有字节、数据包或解码帧增加，说明视频正在正常传输
                        if (currentBytes > previousBytesReceived || currentPackets > previousPacketsReceived || currentFrames > previousDecodedFrames)
                        {
                            lastFrameRealtime = now;
                        }

                        if (previousStatsRealtime > 0f && currentFrames >= previousDecodedFrames)
                        {
                            float elapsed = now - previousStatsRealtime;
                            uint decodedDelta = currentFrames - previousDecodedFrames;

                            if (elapsed > 0f && decodedDelta > 0)
                                displayedVideoFps = decodedDelta / elapsed;
                        }
                        else if (inbound.framesPerSecond > 0)
                        {
                            displayedVideoFps = (float)inbound.framesPerSecond;
                        }

                        previousDecodedFrames = currentFrames;
                        previousBytesReceived = currentBytes;
                        previousPacketsReceived = currentPackets;
                        previousStatsRealtime = now;

                        if (inbound.frameWidth > 0 && inbound.frameHeight > 0)
                        {
                            frameWidth = (int)inbound.frameWidth;
                            frameHeight = (int)inbound.frameHeight;
                            UpdateVideoPanelLayout();
                        }
                    }
                    else
                    {
                        // 统计对象未就绪但 ICE 连接正常时，保底维持活跃
                        if (peerConnection != null &&
                            (peerConnection.IceConnectionState == RTCIceConnectionState.Connected ||
                             peerConnection.IceConnectionState == RTCIceConnectionState.Completed))
                        {
                            lastFrameRealtime = now;
                        }
                    }

                    report.Dispose();
                    continue;
                }
            }

            // 2. 状态报告不可用时的保底维持
            if (peerConnection != null &&
                (peerConnection.IceConnectionState == RTCIceConnectionState.Connected ||
                 peerConnection.IceConnectionState == RTCIceConnectionState.Completed))
            {
                lastFrameRealtime = now;
            }

            if (receivedFramesCount > 0 && previousStatsRealtime > 0f)
            {
                float elapsed = now - previousStatsRealtime;
                if (elapsed > 0f)
                {
                    displayedVideoFps = receivedFramesCount / elapsed;
                    receivedFramesCount = 0;
                }
            }
            previousStatsRealtime = now;
        }
    }

    private void UpdateVideoPanelLayout()
    {
        float videoHeight = videoPanelHeight;
        float videoWidth = videoPanelHeight * (16f / 9f);

        if (frameWidth > 0 && frameHeight > 0)
        {
            float aspect = Mathf.Clamp((float)frameWidth / frameHeight, 0.5f, 3f);
            videoHeight = useNativeResolution ? frameHeight : videoPanelHeight;
            videoWidth = useNativeResolution ? frameWidth : videoHeight * aspect;
        }

        if (aspectRatioFitter != null)
        {
            aspectRatioFitter.enabled = false;
        }

        float totalPanelWidth = Mathf.Max(videoWidth + PanelPadding * 2f, 480f);
        float totalPanelHeight = videoHeight + StatusBarHeight + StatusBarSpacing + PanelPadding * 2f;

        // 1. 设置外层主面板尺寸与卡片样式 (现代磨砂悬浮卡片)
        if (videoPanel != null)
        {
            videoPanel.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, totalPanelWidth);
            videoPanel.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, totalPanelHeight);

            Image panelImg = videoPanel.GetComponent<Image>();
            if (panelImg != null)
            {
                panelImg.sprite = GetRoundedCardSprite(24f);
                panelImg.type = Image.Type.Sliced;
                panelImg.color = new Color(0.045f, 0.06f, 0.09f, 0.94f);
            }
        }

        // 2. 视频画面放置在面板上半部分（紧贴顶部 padding，与底部状态栏完全分离）
        if (targetImage != null)
        {
            if (videoPanel != null && targetImage.transform.parent != videoPanel.transform)
            {
                targetImage.transform.SetParent(videoPanel.transform, false);
            }

            RectTransform imgRect = targetImage.rectTransform;
            imgRect.anchorMin = new Vector2(0.5f, 0.5f);
            imgRect.anchorMax = new Vector2(0.5f, 0.5f);
            imgRect.pivot = new Vector2(0.5f, 0.5f);
            imgRect.localScale = Vector3.one;
            imgRect.localRotation = Quaternion.identity;
            imgRect.sizeDelta = new Vector2(videoWidth, videoHeight);

            float videoCenterY = (totalPanelHeight * 0.5f) - PanelPadding - (videoHeight * 0.5f);
            imgRect.anchoredPosition = new Vector2(0f, videoCenterY);
            targetImage.raycastTarget = false;
        }

        // 3. 底部空白处状态栏背景 (Sleek Dark Pill)
        if (videoPanel != null)
        {
            if (statusBarBgObj == null)
            {
                Transform found = videoPanel.Find("StatusBarBackground");
                if (found != null)
                    statusBarBgObj = found.gameObject;
                else
                {
                    statusBarBgObj = new GameObject("StatusBarBackground", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                    statusBarBgObj.transform.SetParent(videoPanel.transform, false);
                }
            }

            if (statusBarBgObj != null)
            {
                RectTransform bgRect = statusBarBgObj.GetComponent<RectTransform>();
                bgRect.anchorMin = new Vector2(0.5f, 0.5f);
                bgRect.anchorMax = new Vector2(0.5f, 0.5f);
                bgRect.pivot = new Vector2(0.5f, 0.5f);
                bgRect.localScale = Vector3.one;
                bgRect.localRotation = Quaternion.identity;
                bgRect.sizeDelta = new Vector2(videoWidth, StatusBarHeight);

                float statusCenterY = (-totalPanelHeight * 0.5f) + PanelPadding + (StatusBarHeight * 0.5f);
                bgRect.anchoredPosition = new Vector2(0f, statusCenterY);

                Image bgImg = statusBarBgObj.GetComponent<Image>();
                if (bgImg != null)
                {
                    bgImg.sprite = GetStatusPillSprite(14f);
                    bgImg.type = Image.Type.Sliced;
                    bgImg.color = new Color(0.08f, 0.105f, 0.145f, 0.92f);
                    bgImg.raycastTarget = false;
                }
            }
        }

        // 4. 状态文字放置在底部空白处状态栏中（完全移出视频画面）
        if (videoStatusText != null)
        {
            if (videoPanel != null && videoStatusText.transform.parent != videoPanel.transform)
            {
                videoStatusText.transform.SetParent(videoPanel.transform, false);
            }

            if (videoStatusText.font == null && TMP_Settings.defaultFontAsset != null)
                videoStatusText.font = TMP_Settings.defaultFontAsset;

            videoStatusText.gameObject.SetActive(true);
            videoStatusText.raycastTarget = false;
            videoStatusText.enableAutoSizing = true;
            videoStatusText.fontSizeMin = 14f;
            videoStatusText.fontSizeMax = 20f;
            videoStatusText.fontSize = 20f;
            videoStatusText.horizontalAlignment = HorizontalAlignmentOptions.Center;
            videoStatusText.verticalAlignment = VerticalAlignmentOptions.Middle;
            videoStatusText.enableWordWrapping = false;
            videoStatusText.overflowMode = TextOverflowModes.Ellipsis;
            videoStatusText.margin = new Vector4(12f, 0f, 12f, 0f);
            videoStatusText.transform.SetAsLastSibling();

            RectTransform statusRect = videoStatusText.rectTransform;
            statusRect.anchorMin = new Vector2(0.5f, 0.5f);
            statusRect.anchorMax = new Vector2(0.5f, 0.5f);
            statusRect.pivot = new Vector2(0.5f, 0.5f);
            statusRect.localScale = Vector3.one;
            statusRect.localRotation = Quaternion.identity;
            statusRect.sizeDelta = new Vector2(videoWidth, StatusBarHeight);

            float statusCenterY = (-totalPanelHeight * 0.5f) + PanelPadding + (StatusBarHeight * 0.5f);
            statusRect.anchoredPosition = new Vector2(0f, statusCenterY);
        }
    }

    private void OnIceConnectionChange(RTCIceConnectionState state)
    {
        connectionState = "ICE: " + state;
        if (state == RTCIceConnectionState.Failed ||
            state == RTCIceConnectionState.Closed)
        {
            connectionNeedsRestart = true;
        }
    }

    private void OnConnectionStateChange(RTCPeerConnectionState state)
    {
        if (state == RTCPeerConnectionState.Failed ||
            state == RTCPeerConnectionState.Closed)
        {
            connectionNeedsRestart = true;
        }
    }

    private void FailConnection(string message)
    {
        consecutiveFailures++;
        lastError = message;
        connectionState = "连接失败，准备重试";
        connectionNeedsRestart = true;
        Debug.LogWarning("相机 WebRTC: " + message);
    }

    private void ClosePeerConnection(bool clearTexture = false)
    {
        if (receivedVideoTrack != null)
            receivedVideoTrack.OnVideoReceived -= OnVideoReceived;
        receivedVideoTrack = null;
        videoReceiver = null;
        previousDecodedFrames = 0;
        previousBytesReceived = 0;
        previousPacketsReceived = 0;
        previousStatsRealtime = 0f;
        displayedVideoFps = 0f;
        receivedFramesCount = 0;

        if (peerConnection != null)
        {
            peerConnection.OnTrack = null;
            peerConnection.OnIceConnectionChange = null;
            peerConnection.OnConnectionStateChange = null;
            peerConnection.Close();
            peerConnection.Dispose();
            peerConnection = null;
        }

        if (clearTexture && targetImage != null)
            targetImage.texture = null;
    }

    private void RefreshStatusText()
    {
        if (videoStatusText == null)
            return;

        if (IsVideoConnected)
        {
            videoStatusText.text =
                "<color=#00E676>● LIVE</color>  " + frameWidth + "×" + frameHeight +
                "  <color=#00C7FF>" + displayedVideoFps.ToString("F1") + " FPS</color>  (PC: " + currentServerIp + ")";
            return;
        }

        videoStatusText.text = "<color=#FFB826>● 相机视频: " + connectionState + "</color>";
        if (!string.IsNullOrEmpty(lastError))
            videoStatusText.text += "  <size=80%><color=#FF4D4D>(" + lastError + ")</color></size>";
    }

    #region 高清程序化圆角纹理生成器
    private static Sprite GetRoundedCardSprite(float radius = 20f)
    {
        if (panelRoundedCardSprite == null)
            panelRoundedCardSprite = GenerateSdfRoundedRectSprite("CameraPanelCard", radius);
        return panelRoundedCardSprite;
    }

    private static Sprite GetStatusPillSprite(float radius = 12f)
    {
        if (statusPillSprite == null)
            statusPillSprite = GenerateSdfRoundedRectSprite("CameraStatusPill", radius);
        return statusPillSprite;
    }

    private static Sprite GenerateSdfRoundedRectSprite(string name, float cornerRadius)
    {
        const int size = 128;
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.name = name;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;

        Vector2 halfSize = new Vector2(size * 0.5f, size * 0.5f);
        float r = Mathf.Clamp(cornerRadius, 4f, 60f);
        Vector2 innerBox = halfSize - new Vector2(r, r);
        float aa = 2f;

        Color[] colors = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2(x - halfSize.x + 0.5f, y - halfSize.y + 0.5f);
                Vector2 d = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - innerBox;
                float dist = Mathf.Max(0f, Mathf.Max(d.x, d.y)) +
                             Mathf.Min(0f, Mathf.Max(d.x, d.y));
                if (d.x > 0f && d.y > 0f)
                    dist = d.magnitude;

                float alpha = Mathf.Clamp01((r - dist) / aa);
                colors[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }
        texture.SetPixels(colors);
        texture.Apply();

        int border = Mathf.RoundToInt(r + 2f);
        return Sprite.Create(texture, new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect, new Vector4(border, border, border, border));
    }
    #endregion
}
