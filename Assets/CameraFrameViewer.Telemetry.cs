using System;
using System.Collections;
using System.Globalization;
using System.Text;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.Networking;

// This file uses the installed com.unity.webrtc 3.0.0-pre.8 RTCStats.Dict API.
// Its undefined members are null; typed property getters instead return zero.
public partial class CameraFrameViewer
{
    [Header("接收端遥测")]
    public bool reportReceiverMetrics = true;

    private string receiverTelemetrySession;
    private string receiverTelemetryUrl;
    private string receiverTelemetryPendingJson;
    private RTCPeerConnection receiverTelemetryPeer;
    private Coroutine receiverTelemetryCoroutine;
    private UnityWebRequest receiverTelemetryRequest;
    private System.Diagnostics.Stopwatch receiverTelemetryClock;
    private long receiverTelemetrySequence;
    private double receiverTelemetryLastSampleMs;
    private bool receiverTelemetryWarningLogged;
    private string receiverTelemetryLastNativeStatsId;
    private long receiverTelemetryLastNativeTimestamp = long.MinValue;

    private void BeginReceiverTelemetry(RTCPeerConnection peer, string serverIp, string cameraId, string sessionId)
    {
        ResetReceiverTelemetry();
        // Older servers can stream video without implementing telemetry.
        if (!reportReceiverMetrics || peer == null || string.IsNullOrEmpty(sessionId) || sessionId.Length > 128)
            return;
        receiverTelemetryPeer = peer;
        receiverTelemetrySession = sessionId;
        int targetPort = videoPort > 0 ? videoPort : 7876;
        receiverTelemetryUrl = "http://" + serverIp + ":" + targetPort +
            "/api/webrtc/receiver/" + Uri.EscapeDataString(string.IsNullOrEmpty(cameraId) ? "main" : cameraId);
        receiverTelemetryClock = System.Diagnostics.Stopwatch.StartNew();
        receiverTelemetryCoroutine = StartCoroutine(ReceiverTelemetryLoop(peer, sessionId));
    }

    private void QueueReceiverTelemetry(RTCInboundRTPStreamStats inbound, RTCPeerConnection statsPeer)
    {
        if (!reportReceiverMetrics || statsPeer != receiverTelemetryPeer || statsPeer != peerConnection ||
            receiverTelemetryClock == null || string.IsNullOrEmpty(receiverTelemetrySession))
            return;
        // This dictionary is copied into JSON before report.Dispose(). Do not retain native RTCStats.
        var members = inbound.Dict;
        if (!members.TryGetValue("kind", out object kind) ||
            !string.Equals(kind as string, "video", StringComparison.OrdinalIgnoreCase))
            return;
        string statsId = inbound.Id;
        if (string.IsNullOrEmpty(statsId) || statsId.Length > 128)
            return;
        // Cached native reports are not fresh measurements, even if the polling loop runs.
        if (receiverTelemetryLastNativeStatsId == statsId &&
            inbound.Timestamp <= receiverTelemetryLastNativeTimestamp)
            return;
        receiverTelemetryLastNativeStatsId = statsId;
        receiverTelemetryLastNativeTimestamp = inbound.Timestamp;
        double sampleMs = Math.Max(receiverTelemetryClock.Elapsed.TotalMilliseconds,
            receiverTelemetryLastSampleMs + 0.001);
        receiverTelemetryLastSampleMs = sampleMs;
        var json = new StringBuilder(768);
        json.Append("{\"session_id\":").Append(JsonQuoted(receiverTelemetrySession));
        json.Append(",\"sequence\":").Append((receiverTelemetrySequence++).ToString(CultureInfo.InvariantCulture));
        json.Append(",\"sample_ms\":").Append(sampleMs.ToString("R", CultureInfo.InvariantCulture));
        json.Append(",\"stats_id\":").Append(JsonQuoted(statsId));
        AppendReceiverCounter(json, members, "packetsReceived", "packets_received");
        AppendReceiverCounter(json, members, "packetsLost", "packets_lost", 1, true);
        AppendReceiverCounter(json, members, "fecPacketsReceived", "fec_packets_received");
        AppendReceiverCounter(json, members, "fecPacketsDiscarded", "fec_packets_discarded");
        AppendReceiverCounter(json, members, "framesReceived", "frames_received");
        AppendReceiverCounter(json, members, "framesDecoded", "frames_decoded");
        AppendReceiverCounter(json, members, "framesDropped", "frames_dropped");
        AppendReceiverCounter(json, members, "freezeCount", "freeze_count");
        AppendReceiverCounter(json, members, "totalFreezesDuration", "freeze_duration_ms", 1000);
        AppendReceiverCounter(json, members, "jitterBufferDelay", "jitter_buffer_delay_ms", 1000);
        AppendReceiverCounter(json, members, "jitterBufferEmittedCount", "jitter_buffer_emitted_count");
        AppendReceiverCounter(json, members, "totalDecodeTime", "decode_time_ms", 1000);
        // No frames_rendered/render_gap_ms: OnVideoReceived fires on first frame/resize,
        // while Camera.onPostRender can repeatedly draw the same decoded video frame.
        json.Append('}');
        // One bounded latest-value mailbox: HTTP congestion never queues stale reports.
        receiverTelemetryPendingJson = json.ToString();
    }

    private static void AppendReceiverCounter(StringBuilder json,
        System.Collections.Generic.IDictionary<string, object> members,
        string source, string target, double scale = 1, bool allowNegative = false)
    {
        if (!members.TryGetValue(source, out object value) || value == null || value is bool || value is string)
            return;
        double number;
        try { number = Convert.ToDouble(value, CultureInfo.InvariantCulture) * scale; }
        catch (Exception) { return; }
        if (double.IsNaN(number) || double.IsInfinity(number) || number > 1e15 ||
            number < (allowNegative ? -1e9 : 0))
            return;
        json.Append(",\"").Append(target).Append("\":");
        json.Append(number.ToString("R", CultureInfo.InvariantCulture));
    }

    private static string JsonQuoted(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
        {
            if (c == '"' || c == '\\') result.Append('\\').Append(c);
            else if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else result.Append(c);
        }
        return result.Append('"').ToString();
    }

    private IEnumerator ReceiverTelemetryLoop(RTCPeerConnection expectedPeer, string expectedSession)
    {
        var wait = new WaitForSecondsRealtime(1f);
        while (receiverTelemetryPeer == expectedPeer && receiverTelemetrySession == expectedSession)
        {
            yield return wait;
            if (!reportReceiverMetrics || expectedPeer != peerConnection ||
                receiverTelemetrySession != expectedSession)
                yield break;
            string payload = receiverTelemetryPendingJson;
            receiverTelemetryPendingJson = null;
            if (string.IsNullOrEmpty(payload)) continue;
            using (var request = new UnityWebRequest(receiverTelemetryUrl, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 3;
                receiverTelemetryRequest = request;
                yield return request.SendWebRequest();
                if (receiverTelemetryRequest == request) receiverTelemetryRequest = null;
                if (receiverTelemetrySession != expectedSession || expectedPeer != peerConnection)
                    yield break;
                if (request.result != UnityWebRequest.Result.Success && !receiverTelemetryWarningLogged)
                {
                    receiverTelemetryWarningLogged = true;
                    Debug.LogWarning("[Receiver telemetry] " + streamId + ": " + request.error +
                        "; video/control remain active, retrying at limited rate");
                }
                else if (request.result == UnityWebRequest.Result.Success)
                    receiverTelemetryWarningLogged = false;
            }
        }
    }

    private void ResetReceiverTelemetry()
    {
        receiverTelemetrySession = null;
        receiverTelemetryPeer = null;
        receiverTelemetryUrl = null;
        receiverTelemetryPendingJson = null;
        receiverTelemetryClock = null;
        receiverTelemetrySequence = 0;
        receiverTelemetryLastSampleMs = -1;
        receiverTelemetryWarningLogged = false;
        receiverTelemetryLastNativeStatsId = null;
        receiverTelemetryLastNativeTimestamp = long.MinValue;
        if (receiverTelemetryCoroutine != null) StopCoroutine(receiverTelemetryCoroutine);
        receiverTelemetryCoroutine = null;
        if (receiverTelemetryRequest != null)
        {
            try { receiverTelemetryRequest.Abort(); }
            catch (ObjectDisposedException) { }
            receiverTelemetryRequest.Dispose();
            receiverTelemetryRequest = null;
        }
    }
}
