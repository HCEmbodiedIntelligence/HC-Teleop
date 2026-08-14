using System;
using System.Collections;
using System.Linq;
using System.Text;
using TMPro;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class D435FrameViewer : MonoBehaviour
{
    [Serializable]
    private class SessionDescriptionJson
    {
        public string sdp;
        public string type;
    }

    [Header("自动发现与信令")]
    public UdpPoseSender poseSender;
    public int videoPort = 8080;
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
    private float previousStatsRealtime;
    private bool connectionNeedsRestart;
    private CanvasGroup videoWindowCanvasGroup;
    private bool isWindowVisible = true;

    public float DisplayedVideoFps => displayedVideoFps;
    public bool IsVideoConnected =>
        receivedVideoTrack != null &&
        Time.realtimeSinceStartup - lastFrameRealtime < 3f;
    public bool IsWindowVisible => isWindowVisible;

    public string CompactStatus
    {
        get
        {
            if (!IsVideoConnected)
                return "视频: " + connectionState;

            return "视频: WebRTC H.264  " + frameWidth + "×" + frameHeight + "\n" +
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
            aspectRatioFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;

        if (videoStatusText != null)
        {
            videoStatusText.raycastTarget = false;
            videoStatusText.fontSize = 28f;
            videoStatusText.enableWordWrapping = false;
            videoStatusText.margin = new Vector4(8f, 4f, 8f, 4f);

            RectTransform statusRect = videoStatusText.rectTransform;
            statusRect.anchorMin = new Vector2(0.5f, 0.5f);
            statusRect.anchorMax = new Vector2(0.5f, 0.5f);
            statusRect.pivot = new Vector2(0.5f, 0.5f);
            statusRect.anchoredPosition = new Vector2(0f, -330f);
            statusRect.sizeDelta = new Vector2(650f, 90f);
        }
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
        ClosePeerConnection();

        if (targetImage != null)
            targetImage.texture = null;
    }

    private void Update()
    {
        statusRefreshTimer += Time.unscaledDeltaTime;

        if (receivedVideoTrack != null &&
            previousStatsRealtime > 0f &&
            lastFrameRealtime > 0f &&
            Time.realtimeSinceStartup - lastFrameRealtime > 5f)
        {
            connectionState = "5 秒没有收到视频帧，正在重连";
            connectionNeedsRestart = true;
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
            if (string.IsNullOrEmpty(discoveredIp))
            {
                if (peerConnection != null)
                    ClosePeerConnection();
                currentServerIp = string.Empty;
                connectionState = "等待发现 PC";
                yield return new WaitForSecondsRealtime(0.5f);
                continue;
            }

            if (currentServerIp != discoveredIp || connectionNeedsRestart)
            {
                ClosePeerConnection();
                currentServerIp = discoveredIp;
                connectionNeedsRestart = false;
            }

            if (peerConnection == null)
            {
                yield return StartCoroutine(ConnectToServer(discoveredIp));
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

        if (h264Codecs.Length == 0)
        {
            FailConnection("PICO端没有可用的 H.264 解码器");
            yield break;
        }

        RTCErrorType codecError = transceiver.SetCodecPreferences(h264Codecs);
        if (codecError != RTCErrorType.None)
        {
            FailConnection("设置 H.264 失败: " + codecError);
            yield break;
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

        // 当前只在同一局域网使用，不做Trickle ICE；等待候选地址写入SDP后一次性交换。
        float iceDeadline = Time.realtimeSinceStartup + 5f;
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

        string url = "http://" + serverIp + ":" + videoPort + offerPath;
        byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(offerJson));

        using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
        {
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 8;
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                FailConnection("信令失败: " + request.error);
                yield break;
            }

            SessionDescriptionJson answerJson;
            try
            {
                answerJson = JsonUtility.FromJson<SessionDescriptionJson>(request.downloadHandler.text);
            }
            catch (Exception exception)
            {
                FailConnection("Answer JSON错误: " + exception.Message);
                yield break;
            }

            if (answerJson == null || string.IsNullOrEmpty(answerJson.sdp))
            {
                FailConnection("电脑端没有返回有效 SDP");
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
        }

        consecutiveFailures = 0;
        connectionState = "等待 H.264 视频帧";
    }

    private void OnTrack(RTCTrackEvent trackEvent)
    {
        if (!(trackEvent.Track is VideoStreamTrack videoTrack))
            return;

        receivedVideoTrack = videoTrack;
        videoReceiver = trackEvent.Transceiver.Receiver;
        receivedVideoTrack.OnVideoReceived += OnVideoReceived;
        connectionState = "已连接 H.264";
    }

    private void OnVideoReceived(Texture texture)
    {
        if (texture == null)
            return;

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

            if (videoReceiver == null)
                continue;

            RTCStatsReportAsyncOperation operation = videoReceiver.GetStats();
            yield return operation;

            if (operation.IsError || operation.Value == null)
                continue;

            RTCStatsReport report = operation.Value;
            RTCInboundRTPStreamStats inbound = report.Stats.Values
                .OfType<RTCInboundRTPStreamStats>()
                .FirstOrDefault(stats => stats.framesDecoded > 0);

            if (inbound != null)
            {
                float now = Time.realtimeSinceStartup;
                uint decodedFrames = inbound.framesDecoded;

                if (previousStatsRealtime > 0f && decodedFrames >= previousDecodedFrames)
                {
                    float elapsed = now - previousStatsRealtime;
                    uint decodedDelta = decodedFrames - previousDecodedFrames;

                    if (elapsed > 0f)
                        displayedVideoFps = decodedDelta / elapsed;

                    if (decodedDelta > 0)
                        lastFrameRealtime = now;
                }
                else
                {
                    lastFrameRealtime = now;
                    if (inbound.framesPerSecond > 0)
                        displayedVideoFps = (float)inbound.framesPerSecond;
                }

                previousDecodedFrames = decodedFrames;
                previousStatsRealtime = now;

                if (inbound.frameWidth > 0 && inbound.frameHeight > 0)
                {
                    frameWidth = (int)inbound.frameWidth;
                    frameHeight = (int)inbound.frameHeight;
                    UpdateVideoPanelLayout();
                }
            }

            report.Dispose();
        }
    }

    private void UpdateVideoPanelLayout()
    {
        if (frameWidth <= 0 || frameHeight <= 0 || videoPanel == null)
            return;

        float aspect = Mathf.Clamp((float)frameWidth / frameHeight, 0.5f, 3f);
        float panelHeight = useNativeResolution ? frameHeight : videoPanelHeight;
        float panelWidth = useNativeResolution ? frameWidth : panelHeight * aspect;

        videoPanel.SetSizeWithCurrentAnchors(
            RectTransform.Axis.Vertical,
            panelHeight);
        videoPanel.SetSizeWithCurrentAnchors(
            RectTransform.Axis.Horizontal,
            panelWidth);

        if (videoStatusText != null)
        {
            videoStatusText.rectTransform.anchoredPosition =
                new Vector2(0f, -panelHeight * 0.5f + 42f);
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
        Debug.LogWarning("D435 WebRTC: " + message);
    }

    private void ClosePeerConnection()
    {
        if (receivedVideoTrack != null)
            receivedVideoTrack.OnVideoReceived -= OnVideoReceived;
        receivedVideoTrack = null;
        videoReceiver = null;
        previousDecodedFrames = 0;
        previousStatsRealtime = 0f;
        displayedVideoFps = 0f;

        if (peerConnection != null)
        {
            peerConnection.OnTrack = null;
            peerConnection.OnIceConnectionChange = null;
            peerConnection.OnConnectionStateChange = null;
            peerConnection.Close();
            peerConnection.Dispose();
            peerConnection = null;
        }

        if (targetImage != null)
            targetImage.texture = null;
    }

    private void RefreshStatusText()
    {
        if (videoStatusText == null)
            return;

        if (IsVideoConnected)
        {
            videoStatusText.text =
                "H.264 | VIDEO FPS: " + displayedVideoFps.ToString("F1") +
                " | " + frameWidth + " x " + frameHeight + "\n" +
                "PC: " + currentServerIp;
            return;
        }

        if (!IsVideoConnected)
        {
            videoStatusText.text = "D435 WebRTC: " + connectionState;
            if (!string.IsNullOrEmpty(lastError))
                videoStatusText.text += "\n" + lastError;
            return;
        }

        videoStatusText.text =
            "D435 WebRTC  H.264  " + frameWidth + "×" + frameHeight + "\n" +
            "解码 FPS: " + displayedVideoFps.ToString("F1") +
            "  PC: " + currentServerIp;
    }
}
