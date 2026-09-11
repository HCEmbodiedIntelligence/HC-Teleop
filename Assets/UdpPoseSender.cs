using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.XR;

[Serializable]
public class MiddlewareEventJson
{
    public string kind;
    public string type;
    public string source;
    public double timestamp;
    public MiddlewareEventPayload payload;

    public string EventKind => !string.IsNullOrEmpty(kind) ? kind : type;
}

[Serializable]
public class MiddlewareEventPayload
{
    public bool recording;
    public string filename;
    public string path;
    public string action;
    public string message;
    public string error;
    public string reason;
    public string state;
    public bool requires_reset;
    public bool marked;
    public string marked_at;
}

public sealed class MiddlewareSafetyState
{
    public bool IsEmergencyStopped { get; private set; }
    public string EmergencyStopReason { get; private set; }
    private double lastSafetyTimestamp = double.NegativeInfinity;

    public string EmergencyStopMessage
    {
        get
        {
            string reason = EmergencyStopReason ?? string.Empty;
            if (reason.StartsWith("no VR pose data for ", StringComparison.Ordinal))
                return "VR 数据超时：" + reason.Substring(20);
            if (reason.StartsWith("VR pose sample stale for ", StringComparison.Ordinal))
                return "VR 位姿采样停滞：" + reason.Substring(25);
            if (reason == "head tracking invalid")
                return "头显追踪丢失";
            return string.IsNullOrEmpty(reason) ? "中间件触发急停" : reason;
        }
    }

    public bool Apply(MiddlewareEventJson evt)
    {
        if (evt == null || evt.payload == null)
            return false;
        string kind = evt.EventKind;
        if (kind != "safety_stop" && kind != "safety_resume")
            return false;
        // Ignore delayed UDP events; only middleware confirmation clears the latch.
        if (evt.timestamp > 0 && evt.timestamp < lastSafetyTimestamp)
            return false;
        if (evt.timestamp > 0)
            lastSafetyTimestamp = evt.timestamp;
        IsEmergencyStopped = kind == "safety_stop";
        EmergencyStopReason = IsEmergencyStopped
            ? (!string.IsNullOrEmpty(evt.payload.reason) ? evt.payload.reason : evt.payload.message)
            : null;
        return true;
    }
}

public class UdpPoseSender : MonoBehaviour
{
    private const string DiscoveryRequest = "PICO_DISCOVER_V1";
    private const string DiscoveryResponsePrefix = "PICO_RECEIVER_V1|";
    private const byte ProtocolVersion = 2;

    [Flags]
    public enum ControllerButton : ushort
    {
        Primary = 1 << 0,
        Secondary = 1 << 1,
        Grip = 1 << 2,
        Trigger = 1 << 3,
        Menu = 1 << 4,
        PrimaryAxisClick = 1 << 5,
        PrimaryAxisTouch = 1 << 6,
        SecondaryAxisClick = 1 << 7,
        SecondaryAxisTouch = 1 << 8,
        PrimaryTouch = 1 << 9,
        SecondaryTouch = 1 << 10
    }

    private struct ControllerInputState
    {
        public ushort held;
        public ushort pressed;
        public ushort released;
        public float trigger;
        public float grip;
        public Vector2 primaryAxis;
        public Vector2 secondaryAxis;
    }

    private struct PoseState
    {
        public Vector3 position;
        public Quaternion rotation;
    }

    private struct PoseSnapshot
    {
        public long version;
        public long capturedTicks;
        public double timestamp;
        public byte flags;
        public PoseState head;
        public PoseState left;
        public PoseState right;
        public ControllerInputState leftInput;
        public ControllerInputState rightInput;
    }

    [Header("自动发现 PC 接收端")]
    public int discoveryPort = 5006;
    public float receiverTimeoutSeconds = 6f;
    public float discoveryIntervalSeconds = 1f;
    [Tooltip("广播被热点或防火墙拦截时，每轮额外探测当前 /24 子网中的主机数。")]
    [Range(1, 64)] public int discoveryUnicastBatchSize = 32;

    [Range(1f, 200f)]
    public float sendRateHz = 100f;

    private int sentPacketsCounter;
    private double rateMeasureTimer;
    private float currentSendRateHz;

    public float SendRateHz => sendRateHz;
    public float CurrentSendRateHz => currentSendRateHz;

    [Header("统一参考坐标系")]
    public Transform referenceFrame;

    [Header("追踪对象")]
    public Transform head;
    public Transform leftController;
    public Transform rightController;

        [Header("接收端事件监听")]
    public int inboundEventPort = 5007;

    public bool IsRecording { get; private set; }
    public string ActiveRecordingFile { get; private set; }
    public float RecordingStartTime { get; private set; }
    public string LastRecordingMessage { get; private set; }
    public string ReplayState { get; private set; } = "idle";
    public bool ReplayRequiresReset { get; private set; }
    public string LastReplayMessage { get; private set; }
    private readonly MiddlewareSafetyState safetyState = new MiddlewareSafetyState();
    public bool IsEmergencyStopped => safetyState.IsEmergencyStopped;
    public string EmergencyStopReason => safetyState.EmergencyStopReason;
    public string EmergencyStopMessage => safetyState.EmergencyStopMessage;
    public event Action<bool, string> RecordingStateChanged;
    public event Action<string, bool, string> ReplayStateChanged;

    private UdpClient eventClient;

    [Header("运行状态")]
    public bool headTracked;
    public bool leftTracked;
    public bool rightTracked;
    public uint sequence;

    [Header("传输开关")]
    [SerializeField] private bool transmissionEnabled;

    private UdpClient poseClient;
    private UdpClient discoveryClient;
    private volatile IPEndPoint receiverEndPoint;
    private double nextSendTime;
    private double nextDiscoveryTime;
    private double lastReceiverReplyTime = double.NegativeInfinity;
    private double nextIpRefreshTime;
    private int nextDiscoveryHost = 1;
    private string localIpAddress = "检测中";
    private string initializationError;
    private ControllerInputState leftInput;
    private ControllerInputState rightInput;
    private ushort previousLeftButtons;
    private ushort previousRightButtons;
    private bool leftInputInitialized;
    private bool rightInputInitialized;
    private readonly object poseSnapshotLock = new object();
    private readonly object poseSendLock = new object();
    private PoseSnapshot latestPoseSnapshot;
    private bool hasPoseSnapshot;
    private long latestPoseVersion;
    private Thread poseSendThread;
    private volatile bool poseSendThreadStopping;
    private volatile bool poseSenderActive;
    private volatile bool poseSenderSuspended;
    private const int PosePacketBytes = 162;
    private readonly byte[] posePacketBuffer = new byte[PosePacketBytes];
    private MemoryStream posePacketStream;
    private BinaryWriter posePacketWriter;
    private volatile IPAddress[] cachedLocalAddresses = new IPAddress[0];
    private int addressRefreshPending;
    private int addressRefreshReady;
    private readonly byte[] discoveryRequestBytes = Encoding.ASCII.GetBytes(DiscoveryRequest);
    private long lastCaptureTicks;
    private long lastSuccessfulSendTicks;
    private long maxCaptureGapTicks;
    private long maxSendGapTicks;
    private long maxSendCallTicks;
    private long maxSampleAgeTicks;
    private int sendBackpressure;
    private int sendErrors;
    private string lastSendError;
    private double nextDiagnosticsTime;


    public bool IsTransmissionEnabled => transmissionEnabled;
    public bool HasReceiver => receiverEndPoint != null;
    public string LocalIpAddress => localIpAddress;
    public string ReceiverIpAddress => receiverEndPoint == null
        ? string.Empty
        : receiverEndPoint.Address.ToString();
    public string ReceiverAddress => receiverEndPoint == null
        ? "未发现"
        : receiverEndPoint.Address + ":" + receiverEndPoint.Port;

    // Expose the same sampled button state that is written into protocol v2.
    // Consumers such as the interface-reset handler should use this instead
    // of opening a second, potentially different XR input path.
    public bool IsRightButtonHeld(ControllerButton button)
    {
        return (rightInput.held & (ushort)button) != 0;
    }

    public string CurrentStatus
    {
        get
        {
            if (!string.IsNullOrEmpty(initializationError))
                return "UDP 初始化失败: " + initializationError;
            if (IsEmergencyStopped)
                return "急停：" + EmergencyStopMessage;
            if (!HasReceiver)
                return transmissionEnabled ? "等待 PC，发现后自动发送" : "正在搜索 PC 接收端";
            return transmissionEnabled ? "正在传输位姿" : "已发现 PC，传输已关闭";
        }
    }

    public event Action<bool> TransmissionStateChanged;
    public event Action NetworkStatusChanged;

    private void Start()
    {
        try
        {
            poseClient = new UdpClient();
            // Drop a frame under socket backpressure; never wait on the network
            // while holding the send lock (also used by pause/stop packets).
            poseClient.Client.Blocking = false;
            poseClient.Client.SendBufferSize = 16 * 1024;
            posePacketStream = new MemoryStream(posePacketBuffer, 0, PosePacketBytes, true, true);
            posePacketWriter = new BinaryWriter(posePacketStream);
            discoveryClient = new UdpClient(0);
            discoveryClient.EnableBroadcast = true;
            discoveryClient.Client.Blocking = false;
            try {
                eventClient = new UdpClient(inboundEventPort);
                eventClient.Client.Blocking = false;
            } catch (Exception ex) { Debug.LogWarning("UDP event port bind failed: " + ex.Message); }

            double now = Time.realtimeSinceStartupAsDouble;
            nextSendTime = now;
            nextDiscoveryTime = now;
            nextIpRefreshTime = now;
            RefreshLocalIp();
            StartPoseSendThread();
            poseSenderActive = transmissionEnabled;
            Debug.Log("UDP initialized. Searching for a PC receiver...");
        }
        catch (Exception exception)
        {
            initializationError = exception.Message;
            Debug.LogError("UDP initialization failed: " + exception.Message);
            CloseUdp(false);
        }
    }

    private void Update()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        ApplyLocalIpRefresh();
        ReportTransportDiagnostics(now);

        SampleControllerInput(
            XRNode.LeftHand,
            ref leftInput,
            ref previousLeftButtons,
            ref leftInputInitialized);
        SampleControllerInput(
            XRNode.RightHand,
            ref rightInput,
            ref previousRightButtons,
            ref rightInputInitialized);

        // --- 核心快捷键：按 A 开启遥操作，按 B 关闭遥操作 ---
        bool aPressedThisFrame = (rightInput.pressed & (ushort)ControllerButton.Primary) != 0;
        bool bPressedThisFrame = (rightInput.pressed & (ushort)ControllerButton.Secondary) != 0;

        if (aPressedThisFrame)
        {
            if (!transmissionEnabled)
            {
                SetTransmissionEnabled(true);
                TriggerHapticImpulse(XRNode.RightHand, 0.75f, 0.15f);
                Debug.Log("[Teleop] 右手 A 键按下 -> 开启遥操作传输");
            }
        }
        else if (bPressedThisFrame)
        {
            if (transmissionEnabled)
            {
                SetTransmissionEnabled(false);
                StartCoroutine(TriggerDoubleHapticImpulse(XRNode.RightHand));
                Debug.Log("[Teleop] 右手 B 键按下 -> 关闭遥操作传输");
            }
        }

        if (now >= nextIpRefreshTime)
        {
            RefreshLocalIp();
            nextIpRefreshTime = now + 5.0;
        }

        rateMeasureTimer += Time.unscaledDeltaTime;
        if (rateMeasureTimer >= 0.5)
        {
            int packets = Interlocked.Exchange(ref sentPacketsCounter, 0);
            currentSendRateHz = (float)(packets / rateMeasureTimer);
            rateMeasureTimer = 0;
        }

        PollInboundEvents();
        PollDiscoveryReplies(now);

        if (now >= nextDiscoveryTime)
        {
            SendDiscoveryRequest();
            nextDiscoveryTime = now + Math.Max(0.25, discoveryIntervalSeconds);
        }

        if (receiverEndPoint != null &&
            now - lastReceiverReplyTime > Math.Max(1f, receiverTimeoutSeconds))
        {
            Debug.LogWarning("PC receiver timed out. Searching again.");
            receiverEndPoint = null;
            currentSendRateHz = 0f;
            NetworkStatusChanged?.Invoke();
        }
    }

    private void LateUpdate()
    {
        if (!transmissionEnabled || poseClient == null || receiverEndPoint == null || sendRateHz <= 0f)
            return;

        double now = Time.realtimeSinceStartupAsDouble;
        CaptureLatestPoseSnapshot(now);
    }

    private void SendDiscoveryRequest()
    {
        if (discoveryClient == null)
            return;

        try
        {
            byte[] request = discoveryRequestBytes;
            IPEndPoint receiver = receiverEndPoint;
            if (receiver != null)
            {
                // A discovered receiver only needs a directed heartbeat.
                discoveryClient.Send(request, request.Length,
                    new IPEndPoint(receiver.Address, discoveryPort));
                return;
            }
            // 1. 全局广播 255.255.255.255
            discoveryClient.Send(request, request.Length,
                new IPEndPoint(IPAddress.Broadcast, discoveryPort));

            // 2. 对所有有效 IPv4 网卡发送定向广播。开发机上常同时
            // 存在 Meta、Hyper-V、WLAN 和以太网，只使用默认出口会扫错网段。
            IPAddress[] localAddresses = cachedLocalAddresses;
            foreach (IPAddress ip in localAddresses)
            {
                byte[] ipBytes = ip.GetAddressBytes();
                ipBytes[3] = 255;
                IPAddress subnetBroadcast = new IPAddress(ipBytes);
                if (!subnetBroadcast.Equals(IPAddress.Broadcast))
                {
                    discoveryClient.Send(request, request.Length,
                        new IPEndPoint(subnetBroadcast, discoveryPort));
                }

            }

            // PICO 热点、Windows 多网卡以及部分路由器会丢弃广播回复。
            // 在尚未找到接收端时，对每个有效 /24 子网分批轮询。所有
            // 网段使用同一批主机号，所以 1-254 只需约八轮即可覆盖。
            if (receiverEndPoint == null && localAddresses.Length > 0)
            {
                int probes = Mathf.Clamp(discoveryUnicastBatchSize, 1, 64);
                int firstHost = nextDiscoveryHost;
                foreach (IPAddress ip in localAddresses)
                {
                    byte[] localBytes = ip.GetAddressBytes();
                    int host = firstHost;
                    for (int index = 0; index < probes; index++)
                    {
                        if (host != localBytes[3])
                        {
                            byte[] targetBytes = (byte[])localBytes.Clone();
                            targetBytes[3] = (byte)host;
                            discoveryClient.Send(
                                request,
                                request.Length,
                                new IPEndPoint(
                                    new IPAddress(targetBytes),
                                    discoveryPort));
                        }

                        host++;
                        if (host >= 255)
                            host = 1;
                    }
                }

                nextDiscoveryHost = firstHost + probes;
                while (nextDiscoveryHost >= 255)
                    nextDiscoveryHost -= 254;
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("UDP discovery send failed: " + exception.Message);
        }
    }

    private void PollDiscoveryReplies(double now)
    {
        if (discoveryClient == null)
            return;

        try
        {
            while (discoveryClient.Available > 0)
            {
                IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                string response = Encoding.ASCII.GetString(
                    discoveryClient.Receive(ref sender)).Trim();

                if (!response.StartsWith(DiscoveryResponsePrefix, StringComparison.Ordinal))
                    continue;

                string portText = response.Substring(DiscoveryResponsePrefix.Length);
                if (!int.TryParse(portText, out int posePort) || posePort < 1 || posePort > 65535)
                    continue;

                bool sameReceiver = receiverEndPoint != null &&
                    receiverEndPoint.Address.Equals(sender.Address);
                if (receiverEndPoint != null && !sameReceiver)
                    continue;

                bool changed = receiverEndPoint == null || receiverEndPoint.Port != posePort;
                receiverEndPoint = new IPEndPoint(sender.Address, posePort);
                lastReceiverReplyTime = now;

                if (changed)
                {
                    nextDiscoveryHost = 1;
                    nextSendTime = now;
                    ResetInputHistory();
                    Debug.Log("PC receiver discovered: " + ReceiverAddress);
                    NetworkStatusChanged?.Invoke();
                }
            }
        }
        catch (SocketException exception)
        {
            if (exception.SocketErrorCode != SocketError.WouldBlock)
                Debug.LogWarning("UDP discovery receive failed: " + exception.Message);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("UDP discovery receive failed: " + exception.Message);
        }
    }

    private void PollInboundEvents()
    {
        if (eventClient == null)
            return;

        try
        {
            while (eventClient.Available > 0)
            {
                IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = eventClient.Receive(ref sender);
                if (data == null || data.Length == 0)
                    continue;

                string jsonStr = Encoding.UTF8.GetString(data).Trim();
                if (string.IsNullOrEmpty(jsonStr))
                    continue;

                try
                {
                    MiddlewareEventJson evt = JsonUtility.FromJson<MiddlewareEventJson>(jsonStr);
                    if (evt != null)
                    {
                        HandleMiddlewareEvent(evt);
                    }
                }
                catch (Exception jsonEx)
                {
                    Debug.LogWarning("Failed to parse inbound event JSON: " + jsonEx.Message + "\nRaw: " + jsonStr);
                }
            }
        }
        catch (SocketException exception)
        {
            if (exception.SocketErrorCode != SocketError.WouldBlock)
                Debug.LogWarning("UDP inbound event receive failed: " + exception.Message);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("UDP inbound event receive failed: " + exception.Message);
        }
    }

    private void HandleMiddlewareEvent(MiddlewareEventJson evt)
    {
        if (evt == null || evt.payload == null)
            return;

        string eventKind = evt.EventKind ?? string.Empty;
        MiddlewareEventPayload payload = evt.payload;

        if (eventKind.StartsWith("replay_", StringComparison.Ordinal))
        {
            ReplayState = string.IsNullOrEmpty(payload.state) ? eventKind.Substring(7) : payload.state;
            ReplayRequiresReset = payload.requires_reset;
            LastReplayMessage = !string.IsNullOrEmpty(payload.error)
                ? "重放错误: " + payload.error
                : payload.message;
            if (eventKind == "replay_reset")
            {
                ReplayState = "idle";
                ReplayRequiresReset = false;
            }
            ReplayStateChanged?.Invoke(ReplayState, ReplayRequiresReset, LastReplayMessage);
            NetworkStatusChanged?.Invoke();
            return;
        }

        if (eventKind == "safety_stop" || eventKind == "safety_resume")
        {
            if (!safetyState.Apply(evt))
                return;
            // Keep sending input so A can reach the middleware's resume handler.
            // A local transmission toggle or recovered tracking must not clear this state.
            if (!IsEmergencyStopped && ReplayRequiresReset)
            {
                ReplayState = "idle";
                ReplayRequiresReset = false;
                LastReplayMessage = "已恢复实时遥操作";
                ReplayStateChanged?.Invoke(ReplayState, false, LastReplayMessage);
            }
            NetworkStatusChanged?.Invoke();
            return;
        }

        if (!eventKind.StartsWith("recording_", StringComparison.Ordinal))
            return;

        LastRecordingMessage = !string.IsNullOrEmpty(payload.error)
            ? "错误: " + payload.error
            : payload.message;

        if (eventKind == "recording_marked")
        {
            if (string.IsNullOrEmpty(LastRecordingMessage))
                LastRecordingMessage = "已标记: " + payload.filename;
            TriggerHapticImpulse(XRNode.LeftHand, 0.65f, 0.12f);
            TriggerHapticImpulse(XRNode.RightHand, 0.65f, 0.12f);
            RecordingStateChanged?.Invoke(IsRecording, LastRecordingMessage);
            NetworkStatusChanged?.Invoke();
            return;
        }

        bool wasRecording = IsRecording;
        bool targetRecording = IsRecording;
        bool hasRecordingState = false;
        if (eventKind == "recording_started")
        {
            targetRecording = true;
            hasRecordingState = true;
        }
        else if (eventKind == "recording_stopped")
        {
            targetRecording = false;
            hasRecordingState = true;
        }
        else if (eventKind == "recording_status")
        {
            targetRecording = payload.recording;
            hasRecordingState = true;
        }

        if (hasRecordingState)
        {
            IsRecording = targetRecording;
            if (IsRecording && !wasRecording)
            {
                RecordingStartTime = Time.realtimeSinceStartup;
                ActiveRecordingFile = payload.filename;
            }
            else if (!IsRecording)
            {
                ActiveRecordingFile = null;
            }
            RecordingStateChanged?.Invoke(IsRecording, LastRecordingMessage);
        }
        NetworkStatusChanged?.Invoke();
    }

    private void SendPosePacket(double timestamp, bool forceInvalidFlags = false)
    {
        IPEndPoint target = receiverEndPoint;
        if (poseClient == null || target == null)
            return;

        PoseSnapshot snapshot = BuildPoseSnapshot(timestamp, forceInvalidFlags);
        SendPoseSnapshot(snapshot, target, true);
        if (forceInvalidFlags)
            ResetInputHistory();
    }

    private void CaptureLatestPoseSnapshot(double timestamp)
    {
        PoseSnapshot snapshot = BuildPoseSnapshot(timestamp, false);
        snapshot.capturedTicks = Stopwatch.GetTimestamp();
        if (lastCaptureTicks != 0)
            RecordMaximum(ref maxCaptureGapTicks, snapshot.capturedTicks - lastCaptureTicks);
        lastCaptureTicks = snapshot.capturedTicks;
        lock (poseSnapshotLock)
        {
            snapshot.version = ++latestPoseVersion;
            latestPoseSnapshot = snapshot;
            hasPoseSnapshot = true;
        }

        // Edge bits belong to this sample.  The sender thread transmits them
        // once, while held/analog state remains present in every packet.
        leftInput.pressed = 0;
        leftInput.released = 0;
        rightInput.pressed = 0;
        rightInput.released = 0;
    }

    private PoseSnapshot BuildPoseSnapshot(double timestamp, bool forceInvalidFlags)
    {
        headTracked = head != null && IsTracked(XRNode.Head);
        leftTracked = leftController != null && IsTracked(XRNode.LeftHand);
        rightTracked = rightController != null && IsTracked(XRNode.RightHand);

        byte flags = 0;
        if (!forceInvalidFlags)
        {
            if (headTracked) flags |= 1;
            if (leftTracked) flags |= 2;
            if (rightTracked) flags |= 4;
        }

        return new PoseSnapshot
        {
            timestamp = timestamp,
            flags = flags,
            head = ReadPose(head),
            left = ReadPose(leftController),
            right = ReadPose(rightController),
            leftInput = forceInvalidFlags ? default(ControllerInputState) : leftInput,
            rightInput = forceInvalidFlags ? default(ControllerInputState) : rightInput
        };
    }

    private bool SendPoseSnapshot(
        PoseSnapshot snapshot,
        IPEndPoint target,
        bool includeInputEdges)
    {
        ControllerInputState packetLeftInput = snapshot.leftInput;
        ControllerInputState packetRightInput = snapshot.rightInput;
        if (!includeInputEdges)
        {
            packetLeftInput.pressed = packetLeftInput.released = 0;
            packetRightInput.pressed = packetRightInput.released = 0;
        }
        try
        {
            lock (poseSendLock)
            {
                if (poseClient == null || posePacketWriter == null)
                    return false;
                if (snapshot.flags != 0 && (!poseSenderActive || poseSenderSuspended))
                    return false;
                posePacketStream.Position = 0;
                BinaryWriter writer = posePacketWriter;
                writer.Write((byte)'P'); writer.Write((byte)'I');
                writer.Write((byte)'C'); writer.Write((byte)'O');
                writer.Write(ProtocolVersion);
                writer.Write(sequence);
                // Preserve Unity's sample timestamp, including repeated samples.
                // A stalled sampler must remain detectable by the receiver.
                writer.Write(snapshot.timestamp);
                writer.Write(snapshot.flags);
                WritePose(writer, snapshot.head);
                WritePose(writer, snapshot.left);
                WritePose(writer, snapshot.right);
                WriteControllerInput(writer, packetLeftInput);
                WriteControllerInput(writer, packetRightInput);
                writer.Flush();
                long started = Stopwatch.GetTimestamp();
                if (snapshot.capturedTicks != 0)
                    RecordMaximum(ref maxSampleAgeTicks, started - snapshot.capturedTicks);
                try
                {
                    poseClient.Send(posePacketBuffer, PosePacketBytes, target);
                }
                finally
                {
                    RecordMaximum(ref maxSendCallTicks, Stopwatch.GetTimestamp() - started);
                }
                long sent = Stopwatch.GetTimestamp();
                if (lastSuccessfulSendTicks != 0)
                    RecordMaximum(ref maxSendGapTicks, sent - lastSuccessfulSendTicks);
                lastSuccessfulSendTicks = sent;
                Interlocked.Increment(ref sentPacketsCounter);
                unchecked { sequence++; }
                return true;
            }
        }
        catch (SocketException exception)
        {
            if (exception.SocketErrorCode == SocketError.WouldBlock ||
                exception.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                Interlocked.Increment(ref sendBackpressure);
            else
            {
                Interlocked.Increment(ref sendErrors);
                Interlocked.Exchange(ref lastSendError, exception.SocketErrorCode.ToString());
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref sendErrors);
            Interlocked.Exchange(ref lastSendError, exception.Message);
        }
        return false;
    }

    private static void RecordMaximum(ref long maximum, long value)
    {
        long old;
        do
        {
            old = Interlocked.Read(ref maximum);
            if (value <= old) return;
        } while (Interlocked.CompareExchange(ref maximum, value, old) != old);
    }

    private void ReportTransportDiagnostics(double now)
    {
        if (now < nextDiagnosticsTime) return;
        nextDiagnosticsTime = now + 1.0;
        double milliseconds = 1000.0 / Stopwatch.Frequency;
        double captureGap = Interlocked.Exchange(ref maxCaptureGapTicks, 0) * milliseconds;
        double sendGap = Interlocked.Exchange(ref maxSendGapTicks, 0) * milliseconds;
        double sendCall = Interlocked.Exchange(ref maxSendCallTicks, 0) * milliseconds;
        double sampleAge = Interlocked.Exchange(ref maxSampleAgeTicks, 0) * milliseconds;
        int blocked = Interlocked.Exchange(ref sendBackpressure, 0);
        int errors = Interlocked.Exchange(ref sendErrors, 0);
        string error = Interlocked.Exchange(ref lastSendError, null);
        if (captureGap > 200 || sendGap > 200 || sendCall > 50 || sampleAge > 200 || blocked > 0 || errors > 0)
            Debug.LogWarning($"[TeleopTiming] sample_time={now:F3} capture_gap_ms={captureGap:F1} " +
                $"send_gap_ms={sendGap:F1} send_call_ms={sendCall:F1} sample_age_ms={sampleAge:F1} " +
                $"backpressure={blocked} errors={errors} last_error={error}");
    }

    private void StartPoseSendThread()
    {
        poseSendThreadStopping = false;
        poseSendThread = new Thread(PoseSendLoop)
        {
            IsBackground = true,
            Name = "PICO UDP pose sender"
        };
        poseSendThread.Start();
    }

    private void PoseSendLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double nextSendAt = clock.Elapsed.TotalSeconds;
        long lastSentSnapshotVersion = -1;

        while (!poseSendThreadStopping)
        {
            IPEndPoint target = receiverEndPoint;
            float rate = sendRateHz;
            if (!poseSenderActive || poseSenderSuspended || target == null ||
                poseClient == null || rate <= 0f)
            {
                nextSendAt = clock.Elapsed.TotalSeconds;
                Thread.Sleep(2);
                continue;
            }

            double now = clock.Elapsed.TotalSeconds;
            if (now < nextSendAt)
            {
                int sleepMilliseconds = Math.Max(
                    1,
                    (int)Math.Floor((nextSendAt - now) * 1000.0));
                Thread.Sleep(sleepMilliseconds);
                continue;
            }

            PoseSnapshot snapshot;
            bool available;
            lock (poseSnapshotLock)
            {
                snapshot = latestPoseSnapshot;
                available = hasPoseSnapshot;
            }

            if (available)
            {
                bool includeEdges = snapshot.version != lastSentSnapshotVersion;
                if (SendPoseSnapshot(snapshot, target, includeEdges))
                    lastSentSnapshotVersion = snapshot.version;
            }

            double interval = 1.0 / Math.Max(1.0, rate);
            nextSendAt += interval;
            now = clock.Elapsed.TotalSeconds;
            if (now - nextSendAt > 0.1)
                nextSendAt = now + interval;
        }
    }

    private static void WriteControllerInput(
        BinaryWriter writer,
        ControllerInputState input)
    {
        writer.Write(input.held);
        writer.Write(input.pressed);
        writer.Write(input.released);
        writer.Write(input.trigger);
        writer.Write(input.grip);
        writer.Write(input.primaryAxis.x);
        writer.Write(input.primaryAxis.y);
        writer.Write(input.secondaryAxis.x);
        writer.Write(input.secondaryAxis.y);
    }

    private static void SampleControllerInput(
        XRNode node,
        ref ControllerInputState state,
        ref ushort previousButtons,
        ref bool initialized)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (device.isValid &&
            device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) &&
            !tracked)
        {
            device = default(InputDevice);
        }
        ushort currentButtons = device.isValid
            ? ReadButtonMask(device)
            : (ushort)0;

        if (initialized)
        {
            state.pressed |= (ushort)(currentButtons & ~previousButtons);
            state.released |= (ushort)(previousButtons & ~currentButtons);
        }
        else
        {
            // Treat buttons already held when a stream starts as new presses.
            state.pressed |= currentButtons;
            initialized = true;
        }

        state.held = currentButtons;
        previousButtons = currentButtons;

        state.trigger = ReadFloat(device, CommonUsages.trigger);
        state.grip = ReadFloat(device, CommonUsages.grip);
        state.primaryAxis = ReadVector2(device, CommonUsages.primary2DAxis);
        state.secondaryAxis = ReadVector2(device, CommonUsages.secondary2DAxis);
    }

    private static ushort ReadButtonMask(InputDevice device)
    {
        ushort mask = 0;
        AddButton(device, CommonUsages.primaryButton,
            ControllerButton.Primary, ref mask);
        AddButton(device, CommonUsages.secondaryButton,
            ControllerButton.Secondary, ref mask);
        AddButton(device, CommonUsages.gripButton,
            ControllerButton.Grip, ref mask);
        AddButton(device, CommonUsages.triggerButton,
            ControllerButton.Trigger, ref mask);
        AddButton(device, CommonUsages.menuButton,
            ControllerButton.Menu, ref mask);
        AddButton(device, CommonUsages.primary2DAxisClick,
            ControllerButton.PrimaryAxisClick, ref mask);
        AddButton(device, CommonUsages.primary2DAxisTouch,
            ControllerButton.PrimaryAxisTouch, ref mask);
        AddButton(device, CommonUsages.secondary2DAxisClick,
            ControllerButton.SecondaryAxisClick, ref mask);
        AddButton(device, CommonUsages.secondary2DAxisTouch,
            ControllerButton.SecondaryAxisTouch, ref mask);
        AddButton(device, CommonUsages.primaryTouch,
            ControllerButton.PrimaryTouch, ref mask);
        AddButton(device, CommonUsages.secondaryTouch,
            ControllerButton.SecondaryTouch, ref mask);
        return mask;
    }

    private static void AddButton(
        InputDevice device,
        InputFeatureUsage<bool> usage,
        ControllerButton button,
        ref ushort mask)
    {
        if (device.isValid &&
            device.TryGetFeatureValue(usage, out bool value) &&
            value)
        {
            mask |= (ushort)button;
        }
    }

    private static float ReadFloat(
        InputDevice device,
        InputFeatureUsage<float> usage)
    {
        return device.isValid &&
               device.TryGetFeatureValue(usage, out float value)
            ? value
            : 0f;
    }

    private static Vector2 ReadVector2(
        InputDevice device,
        InputFeatureUsage<Vector2> usage)
    {
        return device.isValid &&
               device.TryGetFeatureValue(usage, out Vector2 value)
            ? value
            : Vector2.zero;
    }

    private void ResetInputHistory()
    {
        leftInput = default(ControllerInputState);
        rightInput = default(ControllerInputState);
        previousLeftButtons = 0;
        previousRightButtons = 0;
        leftInputInitialized = false;
        rightInputInitialized = false;
    }

    private PoseState ReadPose(Transform target)
    {
        Vector3 position = Vector3.zero;
        Quaternion rotation = Quaternion.identity;

        if (target != null)
        {
            if (referenceFrame != null)
            {
                position = referenceFrame.InverseTransformPoint(target.position);
                rotation = Quaternion.Inverse(referenceFrame.rotation) * target.rotation;
            }
            else
            {
                position = target.position;
                rotation = target.rotation;
            }
        }

        return new PoseState
        {
            position = position,
            rotation = rotation
        };
    }

    private static void WritePose(BinaryWriter writer, PoseState pose)
    {
        writer.Write(pose.position.x);
        writer.Write(pose.position.y);
        writer.Write(pose.position.z);
        writer.Write(pose.rotation.x);
        writer.Write(pose.rotation.y);
        writer.Write(pose.rotation.z);
        writer.Write(pose.rotation.w);
    }

    private bool IsTracked(XRNode node)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (!device.isValid)
            return false;
        return !device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) || tracked;
    }

    private void RefreshLocalIp()
    {
        if (Interlocked.CompareExchange(ref addressRefreshPending, 1, 0) != 0)
            return;
        // Interface enumeration may invoke platform services. Never run it on
        // the Unity sampling thread or the pose sender thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                IPAddress[] addresses = FindAllLocalIpv4Addresses().ToArray();
                if (!poseSendThreadStopping)
                {
                    cachedLocalAddresses = addresses;
                    Interlocked.Exchange(ref addressRefreshReady, 1);
                }
            }
            finally { Interlocked.Exchange(ref addressRefreshPending, 0); }
        });
    }

    private void ApplyLocalIpRefresh()
    {
        if (Interlocked.Exchange(ref addressRefreshReady, 0) == 0) return;
        IPAddress[] addresses = cachedLocalAddresses;
        string next = addresses.Length > 0 ? addresses[0].ToString() : "不可用";
        if (next == localIpAddress) return;
        localIpAddress = next;
        NetworkStatusChanged?.Invoke();
    }

    private static List<IPAddress> FindAllLocalIpv4Addresses()
    {
        var addresses = new List<IPAddress>();

        // Android/PICO 上主机名解析有时只返回 127.0.0.1。通过一个不发送
        // 数据的 UDP connect 先取得系统实际选用的 Wi-Fi 地址。
        try
        {
            using (Socket socket = new Socket(
                       AddressFamily.InterNetwork,
                       SocketType.Dgram,
                       ProtocolType.Udp))
            {
                socket.Connect("8.8.8.8", 65530);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                if (endPoint != null &&
                    !IsBenchmarkAdapterAddress(endPoint.Address))
                {
                    addresses.Add(endPoint.Address);
                }
            }
        }
        catch { }

        try
        {
            foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                foreach (UnicastIPAddressInformation entry in network.GetIPProperties().UnicastAddresses)
                {
                    IPAddress address = entry.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address) &&
                        !address.ToString().StartsWith("169.254.", StringComparison.Ordinal) &&
                        !IsBenchmarkAdapterAddress(address) && !addresses.Contains(address))
                        addresses.Add(address);
                }
            }
        }
        catch { }

        addresses.Sort((left, right) =>
            ScoreLocalAddress(right).CompareTo(ScoreLocalAddress(left)));
        return addresses;
    }

    private static bool IsBenchmarkAdapterAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        // 198.18.0.0/15 是基准测试/虚拟代理网段，Meta 软件会创建该网卡，
        // 不应将它当作机器人局域网进行扫描。
        return bytes.Length == 4 && bytes[0] == 198 &&
               (bytes[1] == 18 || bytes[1] == 19);
    }

    private static int ScoreLocalAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
            return 0;
        if (bytes[0] == 192 && bytes[1] == 168)
            return 100;
        if (bytes[0] == 10)
            return 90;
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            return 70;
        return 60;
    }

    private void OnApplicationPause(bool paused)
    {
        Debug.Log($"[TeleopTiming] application_paused={paused} sample_time={Time.realtimeSinceStartupAsDouble:F3}");
        if (paused)
        {
            poseSenderSuspended = true;
            if (transmissionEnabled)
                SendPosePacket(Time.realtimeSinceStartupAsDouble, true);
            return;
        }

        poseSenderSuspended = false;
        double now = Time.realtimeSinceStartupAsDouble;
        nextSendTime = now;
        nextDiscoveryTime = now;
    }

    private void OnDestroy() => CloseUdp(true);
    private void OnApplicationQuit() => CloseUdp(true);

    private void CloseUdp(bool sendStopPacket)
    {
        poseSenderActive = false;
        if (sendStopPacket && transmissionEnabled)
            SendPosePacket(Time.realtimeSinceStartupAsDouble, true);

        poseSendThreadStopping = true;
        if (poseSendThread != null && poseSendThread.IsAlive)
            poseSendThread.Join(500);
        poseSendThread = null;

        try { poseClient?.Close(); } catch { }
        try { eventClient?.Close(); } catch { }
        eventClient = null;
        try { discoveryClient?.Close(); } catch { }
        poseClient = null;
        discoveryClient = null;
        receiverEndPoint = null;
    }

    public static void TriggerHapticImpulse(XRNode node, float amplitude = 0.7f, float duration = 0.12f)
    {
        InputDevice device = InputDevices.GetDeviceAtXRNode(node);
        if (device.isValid && device.TryGetHapticCapabilities(out HapticCapabilities cap) && cap.supportsImpulse)
        {
            device.SendHapticImpulse(0u, amplitude, duration);
        }
    }

    public static System.Collections.IEnumerator TriggerDoubleHapticImpulse(XRNode node)
    {
        TriggerHapticImpulse(node, 0.65f, 0.08f);
        yield return new WaitForSecondsRealtime(0.12f);
        TriggerHapticImpulse(node, 0.65f, 0.08f);
    }

    public void ToggleTransmission() => SetTransmissionEnabled(!transmissionEnabled);

    public void SetTransmissionEnabled(bool enabled)
    {
        if (transmissionEnabled == enabled)
            return;

        if (!enabled)
        {
            poseSenderActive = false;
            SendPosePacket(Time.realtimeSinceStartupAsDouble, true);
        }

        transmissionEnabled = enabled;
        if (transmissionEnabled)
        {
            ResetInputHistory();
            nextSendTime = Time.realtimeSinceStartupAsDouble;
            CaptureLatestPoseSnapshot(nextSendTime);
            poseSenderActive = true;
            GripDraggablePanel.EndAllDrags();
        }
        else
        {
            currentSendRateHz = 0f;
            sentPacketsCounter = 0;
        }

        Debug.Log(transmissionEnabled ? "UDP transmission enabled" : "UDP transmission disabled");
        TransmissionStateChanged?.Invoke(transmissionEnabled);
        NetworkStatusChanged?.Invoke();
    }
}
