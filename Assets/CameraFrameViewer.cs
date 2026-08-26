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

    [Header("相机流")]
    public string streamId = "main";
    public string streamDisplayName = "主相机";

    [Header("显示")]
    public RawImage targetImage;
    public TMP_Text videoStatusText;
    public AspectRatioFitter aspectRatioFitter;
    public RectTransform videoPanel;
    public bool useNativeResolution = true;
    [Min(200f)] public float videoPanelHeight = 540f;

    private RTCPeerConnection peerConnection;
    private VideoStreamTrack receivedVideoTrack;
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
    private bool statsWarningLogged;
    private static Coroutine sharedWebRtcUpdateCoroutine;
    private static CameraFrameViewer sharedWebRtcUpdateOwner;

    private const float PanelPadding = 12f;
    private const float StatusBarHeight = 44f;
    private const float StatusBarSpacing = 8f;
    private static Sprite panelRoundedCardSprite;
    private static Sprite statusPillSprite;
    private GameObject statusBarBgObj;

    public float DisplayedVideoFps => displayedVideoFps;
    public string StreamId => streamId;
    public string StreamDisplayName => streamDisplayName;
    public bool IsVideoConnected =>
        (receivedVideoTrack != null || (targetImage != null && targetImage.texture != null)) &&
        (Time.realtimeSinceStartup - lastFrameRealtime < 6f);
    public bool IsWindowVisible => isWindowVisible;

    public string CompactStatus
    {
        get
        {
            if (!IsVideoConnected)
                return streamDisplayName + ": " + connectionState;

            return streamDisplayName + ": WebRTC H.264  " + frameWidth + "×" + frameHeight + "\n" +
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
        bool visibilityChanged = isWindowVisible != visible;
        isWindowVisible = visible;
        EnsureVideoWindowCanvasGroup();

        if (videoWindowCanvasGroup != null)
        {
            videoWindowCanvasGroup.alpha = visible ? 1f : 0f;
            videoWindowCanvasGroup.interactable = visible;
            videoWindowCanvasGroup.blocksRaycasts = visible;
        }

        // “关闭相机”同时停止该路 WebRTC，而不只是隐藏画面。
        // 多相机模式下这可以立即释放头显解码和服务端编码资源。
        if (!visible && visibilityChanged)
        {
            ClosePeerConnection(true);
            connectionNeedsRestart = false;
            connectionState = "已关闭";
        }
        else if (visible && visibilityChanged && isActiveAndEnabled)
        {
            connectionNeedsRestart = true;
            connectionState = "准备连接";
            EnsureRuntimeCoroutines();
        }
    }

    public void ConfigureStream(string id, string displayName, string path)
    {
        string nextId = string.IsNullOrWhiteSpace(id) ? "main" : id.Trim();
        string nextName = string.IsNullOrWhiteSpace(displayName)
            ? nextId
            : displayName.Trim();
        string nextPath = string.IsNullOrWhiteSpace(path)
            ? "/offer/" + nextId
            : path;
        bool connectionChanged = streamId != nextId || offerPath != nextPath;

        streamId = nextId;
        streamDisplayName = nextName;
        offerPath = nextPath;
        gameObject.name = "CameraStream_" + streamId;
        if (connectionChanged)
            RestartConnection();
        EnsureRuntimeCoroutines();
        UpdateVideoPanelLayout();
        RefreshStatusText();
    }

    public void RestartConnection()
    {
        // A cloned active panel starts OnEnable immediately and may still be
        // negotiating the template stream. Stop that inherited coroutine
        // before applying the clone's own stream id/path.
        if (connectionCoroutine != null)
        {
            StopCoroutine(connectionCoroutine);
            connectionCoroutine = null;
        }

        ClosePeerConnection(true);
        connectionNeedsRestart = isWindowVisible;
        connectionState = isWindowVisible ? "准备连接" : "已关闭";
        EnsureRuntimeCoroutines();
    }

    public void ApplyLayoutNow()
    {
        UpdateVideoPanelLayout();
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
        EnsureRuntimeCoroutines();
    }

    private void EnsureRuntimeCoroutines()
    {
        if (!isActiveAndEnabled)
            return;

        StartSharedWebRtcUpdate();
        if (connectionCoroutine == null)
            connectionCoroutine = StartCoroutine(ConnectionLoop());
        if (statsCoroutine == null)
            statsCoroutine = StartCoroutine(VideoStatsLoop());
    }

    private void OnDisable()
    {
        if (connectionCoroutine != null)
            StopCoroutine(connectionCoroutine);
        if (statsCoroutine != null)
            StopCoroutine(statsCoroutine);

        connectionCoroutine = null;
        statsCoroutine = null;

        // 先关闭 PeerConnection，再停止/移交全局 WebRTC 更新循环。
        // 反过来会让 Unity WebRTC 的 NegotiationNeeded 回调访问已停止的上下文。
        ClosePeerConnection(true);
        StopSharedWebRtcUpdateIfOwner();
        webRtcUpdateCoroutine = null;
    }

    private void StartSharedWebRtcUpdate()
    {
        if (sharedWebRtcUpdateCoroutine != null)
            return;
        sharedWebRtcUpdateOwner = this;
        sharedWebRtcUpdateCoroutine = StartCoroutine(WebRTC.Update());
        webRtcUpdateCoroutine = sharedWebRtcUpdateCoroutine;
    }

    private void StopSharedWebRtcUpdateIfOwner()
    {
        if (sharedWebRtcUpdateOwner != this || sharedWebRtcUpdateCoroutine == null)
            return;

        StopCoroutine(sharedWebRtcUpdateCoroutine);
        sharedWebRtcUpdateCoroutine = null;
        sharedWebRtcUpdateOwner = null;

        CameraFrameViewer replacement = FindObjectsOfType<CameraFrameViewer>(true)
            .FirstOrDefault(viewer => viewer != this && viewer.isActiveAndEnabled);
        if (replacement != null)
            replacement.StartSharedWebRtcUpdate();
    }

    private void Update()
    {
        statusRefreshTimer += Time.unscaledDeltaTime;
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
            if (!isWindowVisible)
            {
                yield return new WaitForSecondsRealtime(0.25f);
                continue;
            }

            if (poseSender == null)
            {
                poseSender = FindObjectOfType<UdpPoseSender>(true);
                connectionState = poseSender == null
                    ? "未设置 UdpPoseSender"
                    : "等待发现 PC";
                yield return new WaitForSecondsRealtime(1f);
                continue;
            }

            string discoveredIp = poseSender.ReceiverIpAddress;
            if (!string.IsNullOrEmpty(discoveredIp))
            {
                if (string.IsNullOrEmpty(currentServerIp))
                {
                    currentServerIp = discoveredIp;
                }
                else if (currentServerIp != discoveredIp && peerConnection == null)
                {
                    currentServerIp = discoveredIp;
                }
            }

            if (string.IsNullOrEmpty(currentServerIp))
            {
                connectionState = "等待发现 PC";
                yield return new WaitForSecondsRealtime(0.5f);
                continue;
            }

            // 如果当前连接正常（或正在稳定接收画面），绝对不要主动断开！
            if (peerConnection != null)
            {
                if (!connectionNeedsRestart)
                {
                    yield return new WaitForSecondsRealtime(1.0f);
                    continue;
                }

                // 仅在明确出错需要重试时清理旧连接
                ClosePeerConnection(false);
                connectionNeedsRestart = false;
            }

            connectionNeedsRestart = false;
            Debug.Log($"[Camera WebRTC] {streamId} -> http://{currentServerIp}:{videoPort}{offerPath}");
            yield return StartCoroutine(ConnectToServer(currentServerIp));

            if (peerConnection == null || connectionNeedsRestart)
            {
                yield return new WaitForSecondsRealtime(reconnectDelaySeconds);
            }
            else
            {
                yield return new WaitForSecondsRealtime(1.0f);
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

        // 优先支持 H.264，同时保留 VP8 作为安全后备
        RTCRtpCodecCapability[] preferredCodecs =
            RTCRtpSender.GetCapabilities(TrackKind.Video).codecs
                .Where(codec =>
                    string.Equals(codec.mimeType, "video/H264", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(codec.mimeType, "video/VP8", StringComparison.OrdinalIgnoreCase))
                .OrderBy(codec => string.Equals(codec.mimeType, "video/H264", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToArray();

        if (preferredCodecs.Length > 0)
        {
            RTCErrorType codecError = transceiver.SetCodecPreferences(preferredCodecs);
            if (codecError != RTCErrorType.None)
            {
                Debug.LogWarning("设置视频首选编解码器提示: " + codecError);
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

        // 局域网直连等待 ICE 候选收集（最多 3.5 秒）
        // 需要给足时间让所有 host candidate 收集完毕，否则 Offer SDP 不完整导致对端 ICE 永远 checking
        float iceDeadline = Time.realtimeSinceStartup + 3.5f;
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
        int targetPort = (videoPort > 0) ? videoPort : 7876;
        string normPath = string.IsNullOrEmpty(offerPath) ? "/offer" : (offerPath.StartsWith("/") ? offerPath : "/" + offerPath);
        string url = "http://" + serverIp + ":" + targetPort + normPath;

        string lastHttpError = string.Empty;
        SessionDescriptionJson answerJson = null;

        using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            // 给服务端 ICE gathering（最多 2s）+ 网络往返留足余量
            // 必须 > 服务端 ICE gather 上限（2s）+ Answer 序列化/传输时间
            request.timeout = 12;
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                string bodyText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                lastHttpError = !string.IsNullOrEmpty(bodyText)
                    ? request.error + " (" + bodyText.Trim() + ")"
                    : request.error + " (URL: " + url + ")";
                FailConnection("信令请求失败: " + lastHttpError);
                yield break;
            }

            try
            {
                answerJson = JsonUtility.FromJson<SessionDescriptionJson>(request.downloadHandler.text);
            }
            catch (Exception exception)
            {
                FailConnection("JSON解析错误: " + exception.Message);
                yield break;
            }
        }

        if (answerJson == null || string.IsNullOrEmpty(answerJson.sdp))
        {
            FailConnection("机器人未返回有效 SDP Answer");
            yield break;
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

        consecutiveFailures = 0;
        connectionState = "等待视频帧";
    }

    private void OnTrack(RTCTrackEvent trackEvent)
    {
        if (!(trackEvent.Track is VideoStreamTrack videoTrack))
            return;

        receivedVideoTrack = videoTrack;
        receivedVideoTrack.OnVideoReceived += OnVideoReceived;
        lastFrameRealtime = Time.realtimeSinceStartup;
        connectionState = "已连接 H.264";
    }

    private void OnVideoReceived(Texture texture)
    {
        if (texture == null)
            return;

        // Unity WebRTC 只在首帧/尺寸变化时触发 OnVideoReceived，不能用它统计视频 FPS。
        // 实时帧率和后续活跃时间由 VideoStatsLoop 的 RTP 统计更新。
        lastFrameRealtime = Time.realtimeSinceStartup;

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

            RTCPeerConnection statsConnection = peerConnection;
            if (statsConnection == null || receivedVideoTrack == null)
            {
                displayedVideoFps = 0f;
                continue;
            }

            RTCStatsReportAsyncOperation operation;
            try
            {
                // 使用完整 PeerConnection 的单次统计，避免 Android 上 Receiver GetStats 重入。
                operation = statsConnection.GetStats();
            }
            catch (Exception exception)
            {
                WarnStatsOnce(exception.Message);
                continue;
            }

            yield return operation;

            // 统计等待期间如果连接已被替换，丢弃旧连接结果。
            if (statsConnection != peerConnection)
            {
                if (!operation.IsError && operation.Value != null)
                    operation.Value.Dispose();
                continue;
            }

            if (operation.IsError || operation.Value == null)
            {
                WarnStatsOnce(operation.IsError ? operation.Error.message : "empty report");
                continue;
            }

            RTCStatsReport report = operation.Value;
            try
            {
                RTCInboundRTPStreamStats[] inboundStreams = report.Stats.Values
                    .OfType<RTCInboundRTPStreamStats>()
                    .ToArray();
                RTCInboundRTPStreamStats inbound = inboundStreams
                    .FirstOrDefault(item => string.Equals(item.kind, "video", StringComparison.OrdinalIgnoreCase))
                    ?? inboundStreams.FirstOrDefault();

                if (inbound == null)
                {
                    WarnStatsOnce("video inbound-rtp not ready");
                    continue;
                }

                statsWarningLogged = false;
                float now = Time.realtimeSinceStartup;
                ulong currentBytes = inbound.bytesReceived;
                ulong currentPackets = inbound.packetsReceived;
                uint currentFrames = inbound.framesDecoded > 0 ? inbound.framesDecoded : inbound.framesReceived;
                bool frameAdvanced = currentFrames > previousDecodedFrames;
                bool transportAdvanced = currentBytes > previousBytesReceived || currentPackets > previousPacketsReceived;

                if (frameAdvanced || transportAdvanced)
                    lastFrameRealtime = now;

                if (previousStatsRealtime > 0f && currentFrames >= previousDecodedFrames)
                {
                    float elapsed = now - previousStatsRealtime;
                    uint decodedDelta = currentFrames - previousDecodedFrames;
                    if (elapsed > 0f && decodedDelta > 0)
                        displayedVideoFps = decodedDelta / elapsed;
                    else
                        displayedVideoFps = inbound.framesPerSecond > 0
                            ? (float)inbound.framesPerSecond
                            : 0f;
                }
                else
                {
                    displayedVideoFps = inbound.framesPerSecond > 0
                        ? (float)inbound.framesPerSecond
                        : 0f;
                }

                previousDecodedFrames = currentFrames;
                previousBytesReceived = currentBytes;
                previousPacketsReceived = currentPackets;
                previousStatsRealtime = now;

                if (inbound.frameWidth > 0 && inbound.frameHeight > 0 &&
                    (frameWidth != (int)inbound.frameWidth || frameHeight != (int)inbound.frameHeight))
                {
                    frameWidth = (int)inbound.frameWidth;
                    frameHeight = (int)inbound.frameHeight;
                    UpdateVideoPanelLayout();
                }
            }
            catch (Exception exception)
            {
                WarnStatsOnce(exception.Message);
            }
            finally
            {
                report.Dispose();
            }
        }
    }

    private void WarnStatsOnce(string message)
    {
        if (statsWarningLogged)
            return;

        statsWarningLogged = true;
        Debug.LogWarning("WebRTC 视频统计暂不可用: " + message);
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
        if (state == RTCIceConnectionState.Connected || state == RTCIceConnectionState.Completed)
        {
            connectionState = "已连接 H.264";
            lastError = string.Empty;
        }
        else
        {
            connectionState = "ICE: " + state;
        }

        if (state == RTCIceConnectionState.Failed)
        {
            connectionNeedsRestart = true;
        }
    }

    private void OnConnectionStateChange(RTCPeerConnectionState state)
    {
        if (state == RTCPeerConnectionState.Connected)
        {
            connectionState = "已连接 H.264";
            lastError = string.Empty;
        }
        else if (state == RTCPeerConnectionState.Failed)
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
        previousDecodedFrames = 0;
        previousBytesReceived = 0;
        previousPacketsReceived = 0;
        previousStatsRealtime = 0f;
        displayedVideoFps = 0f;
        statsWarningLogged = false;

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
                "<b>" + streamDisplayName + "</b>  <color=#00E676>● LIVE</color>  " + frameWidth + "×" + frameHeight +
                "  <color=#00C7FF>" + displayedVideoFps.ToString("F1") + " FPS</color>  (PC: " + currentServerIp + ")";
            return;
        }

        videoStatusText.text = "<b>" + streamDisplayName + "</b>  <color=#FFB826>● " + connectionState + "</color>";
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
