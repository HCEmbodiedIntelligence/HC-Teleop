using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

public class UdpPoseSender : MonoBehaviour
{
    private const string DiscoveryRequest = "PICO_DISCOVER_V1";
    private const string DiscoveryResponsePrefix = "PICO_RECEIVER_V1|";

    [Header("自动发现 PC 接收端")]
    public int discoveryPort = 5006;
    public float receiverTimeoutSeconds = 3f;
    public float discoveryIntervalSeconds = 1f;

    [Range(1f, 120f)]
    public float sendRateHz = 60f;

    [Header("统一参考坐标系")]
    public Transform referenceFrame;

    [Header("追踪对象")]
    public Transform head;
    public Transform leftController;
    public Transform rightController;

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

    public bool IsTransmissionEnabled => transmissionEnabled;
    public bool HasReceiver => receiverEndPoint != null;
    public string LocalIpAddress => localIpAddress;
    public string ReceiverIpAddress => receiverEndPoint == null
        ? string.Empty
        : receiverEndPoint.Address.ToString();
    public string ReceiverAddress => receiverEndPoint == null
        ? "未发现"
        : receiverEndPoint.Address + ":" + receiverEndPoint.Port;

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

        if (now >= nextIpRefreshTime)
        {
            RefreshLocalIp();
            nextIpRefreshTime = now + 5.0;
        }

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

        SendPosePacket(now);
        nextSendTime += interval;
        if (now - nextSendTime > 0.25)
            nextSendTime = now + interval;
    }

    private void SendDiscoveryRequest()
    {
        if (discoveryClient == null)
            return;

        try
        {
            byte[] request = Encoding.ASCII.GetBytes(DiscoveryRequest);
            discoveryClient.Send(request, request.Length,
                new IPEndPoint(IPAddress.Broadcast, discoveryPort));
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
            using (MemoryStream stream = new MemoryStream(128))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((byte)'P');
                writer.Write((byte)'I');
                writer.Write((byte)'C');
                writer.Write((byte)'O');
                writer.Write((byte)1);
                writer.Write(sequence);
                writer.Write(timestamp);
                writer.Write(flags);
                WritePose(writer, head);
                WritePose(writer, leftController);
                WritePose(writer, rightController);

                byte[] packet = stream.ToArray();
                poseClient.Send(packet, packet.Length, target);
                unchecked { sequence++; }
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("UDP send failed: " + exception.Message);
        }
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
        try { discoveryClient?.Close(); } catch { }
        poseClient = null;
        discoveryClient = null;
        receiverEndPoint = null;
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
            nextSendTime = Time.realtimeSinceStartupAsDouble;

        Debug.Log(transmissionEnabled ? "UDP transmission enabled" : "UDP transmission disabled");
        TransmissionStateChanged?.Invoke(transmissionEnabled);
        NetworkStatusChanged?.Invoke();
    }
}
