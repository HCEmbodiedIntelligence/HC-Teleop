using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

[Serializable]
public class MiddlewareEventJson
{
    public string type;
    public string source;
    public double timestamp;
    public MiddlewareEventPayload payload;
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

    [Header("自动发现 PC 接收端")]
    public int discoveryPort = 5006;
    public float receiverTimeoutSeconds = 6f;
    public float discoveryIntervalSeconds = 1f;

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

    public event Action<bool, string> RecordingStateChanged;

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
    private IPEndPoint receiverEndPoint;
    private double nextSendTime;
    private double nextDiscoveryTime;
    private double lastReceiverReplyTime = double.NegativeInfinity;
    private double nextIpRefreshTime;
    private string localIpAddress = "检测中";
    private string initializationError;
    private ControllerInputState leftInput;
    private ControllerInputState rightInput;
    private ushort previousLeftButtons;
    private ushort previousRightButtons;
    private bool leftInputInitialized;
    private bool rightInputInitialized;

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
            currentSendRateHz = (float)(sentPacketsCounter / rateMeasureTimer);
            sentPacketsCounter = 0;
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
        double interval = 1.0 / sendRateHz;
        if (now < nextSendTime)
            return;

        while (now >= nextSendTime)
        {
            SendPosePacket(now);
            nextSendTime += interval;
            if (now - nextSendTime > 0.25)
            {
                nextSendTime = now + interval;
                break;
            }
        }
    }

    private void SendDiscoveryRequest()
    {
        if (discoveryClient == null)
            return;

        try
        {
            byte[] request = Encoding.ASCII.GetBytes(DiscoveryRequest);
            // 1. 全局广播 255.255.255.255
            discoveryClient.Send(request, request.Length,
                new IPEndPoint(IPAddress.Broadcast, discoveryPort));

            // 2. 本地子网定向广播（如 10.42.0.255，增强 Android/Pico Wi-Fi 热点穿透）
            if (IPAddress.TryParse(localIpAddress, out IPAddress ip) &&
                ip.AddressFamily == AddressFamily.InterNetwork)
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
        if (evt == null)
            return;

        if (evt.payload != null)
        {
            if (!string.IsNullOrEmpty(evt.payload.error))
            {
                LastRecordingMessage = "错误: " + evt.payload.error;
            }
            else if (!string.IsNullOrEmpty(evt.payload.message))
            {
                LastRecordingMessage = evt.payload.message;
            }

            bool wasRecording = IsRecording;
            bool targetRecording = evt.payload.recording;

            if (evt.type == "record_started")
                targetRecording = true;
            else if (evt.type == "record_stopped")
                targetRecording = false;

            if (targetRecording != wasRecording || !string.IsNullOrEmpty(evt.payload.filename))
            {
                IsRecording = targetRecording;
                if (IsRecording && !wasRecording)
                {
                    RecordingStartTime = Time.realtimeSinceStartup;
                    ActiveRecordingFile = evt.payload.filename;
                }
                else if (!IsRecording)
                {
                    ActiveRecordingFile = null;
                }

                RecordingStateChanged?.Invoke(IsRecording, LastRecordingMessage);
                NetworkStatusChanged?.Invoke();
            }
            else if (!string.IsNullOrEmpty(LastRecordingMessage))
            {
                NetworkStatusChanged?.Invoke();
            }
        }
    }

    private void SendPosePacket(double timestamp, bool forceInvalidFlags = false)
    {
        IPEndPoint target = receiverEndPoint;
        if (poseClient == null || target == null)
            return;

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

        try
        {
            using (MemoryStream stream = new MemoryStream(192))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((byte)'P');
                writer.Write((byte)'I');
                writer.Write((byte)'C');
                writer.Write((byte)'O');
                writer.Write(ProtocolVersion);
                writer.Write(sequence);
                writer.Write(timestamp);
                writer.Write(flags);
                WritePose(writer, head);
                WritePose(writer, leftController);
                WritePose(writer, rightController);

                if (forceInvalidFlags)
                {
                    WriteControllerInput(writer, default(ControllerInputState));
                    WriteControllerInput(writer, default(ControllerInputState));
                }
                else
                {
                    WriteControllerInput(writer, leftInput);
                    WriteControllerInput(writer, rightInput);
                }

                byte[] packet = stream.ToArray();
                poseClient.Send(packet, packet.Length, target);
                sentPacketsCounter++;
                leftInput.pressed = 0;
                leftInput.released = 0;
                rightInput.pressed = 0;
                rightInput.released = 0;
                if (forceInvalidFlags)
                    ResetInputHistory();
                unchecked { sequence++; }
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("UDP send failed: " + exception.Message);
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

    private void WritePose(BinaryWriter writer, Transform target)
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

        writer.Write(position.x);
        writer.Write(position.y);
        writer.Write(position.z);
        writer.Write(rotation.x);
        writer.Write(rotation.y);
        writer.Write(rotation.z);
        writer.Write(rotation.w);
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
        string previous = localIpAddress;
        localIpAddress = FindLocalIpv4Address();
        if (previous != localIpAddress)
            NetworkStatusChanged?.Invoke();
    }

    private static string FindLocalIpv4Address()
    {
        try
        {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Connect("8.8.8.8", 65530);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                if (endPoint != null)
                    return endPoint.Address.ToString();
            }
        }
        catch { }

        try
        {
            foreach (IPAddress address in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address) &&
                    !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    return address.ToString();
            }
        }
        catch { }

        return "不可用";
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused)
        {
            if (transmissionEnabled)
                SendPosePacket(Time.realtimeSinceStartupAsDouble, true);
            return;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        nextSendTime = now;
        nextDiscoveryTime = now;
    }

    private void OnDestroy() => CloseUdp(true);
    private void OnApplicationQuit() => CloseUdp(true);

    private void CloseUdp(bool sendStopPacket)
    {
        if (sendStopPacket && transmissionEnabled)
            SendPosePacket(Time.realtimeSinceStartupAsDouble, true);

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
            SendPosePacket(Time.realtimeSinceStartupAsDouble, true);

        transmissionEnabled = enabled;
        if (transmissionEnabled)
        {
            ResetInputHistory();
            nextSendTime = Time.realtimeSinceStartupAsDouble;
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
