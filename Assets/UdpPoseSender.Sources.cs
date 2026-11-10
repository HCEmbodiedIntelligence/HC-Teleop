using System;
using System.Collections;
using System.Linq;
using System.Net;
using UnityEngine;
using UnityEngine.Networking;

public partial class UdpPoseSender
{
    [Serializable] private class SessionStatus { public VrSession vr; }
    [Serializable] private class VrSession { public string active_peer; public bool is_locked; }
    public int deviceStatusPort = 7876;
    private readonly DeviceSourceRegistry sourceRegistry = new DeviceSourceRegistry();
    private Coroutine ownershipCoroutine;
    private string verifiedServerIp;
    private string cameraOwnerIp;
    private bool cameraOwnerLocked;
    private double lastOwnershipReply = double.NegativeInfinity;
    private int sourceEpoch;
    public DeviceSourceRegistry.Source[] DeviceSources => sourceRegistry.Sources;
    public bool HasCameraControl => CameraControlAccess.IsAllowed(transmissionEnabled, IsOccupiedByOther,
        ReceiverIpAddress, verifiedServerIp, cameraOwnerIp, cameraOwnerLocked,
        Time.realtimeSinceStartupAsDouble - lastOwnershipReply, cachedLocalAddresses.Select(ip => ip.ToString()));

    private void InvalidateCameraControl()
    {
        lastOwnershipReply = double.NegativeInfinity;
        cameraOwnerIp = null;
        cameraOwnerLocked = false;
        verifiedServerIp = null;
    }

    public void SelectDeviceSource(string ip)
    {
        if (!sourceRegistry.Select(ip)) return;
        ApplySelectedSource(Time.realtimeSinceStartupAsDouble);
    }

    private void ApplySelectedSource(double now)
    {
        sourceRegistry.Tick(now, Math.Max(1f, receiverTimeoutSeconds));
        var source = sourceRegistry.Selected;
        bool changed = (receiverEndPoint == null ? null : receiverEndPoint.Address.ToString()) != source?.Ip ||
            (receiverEndPoint != null && receiverEndPoint.Port != source?.Port);
        if (changed)
        {
            // Serialize with the worker: no old-target packet may follow release.
            lock (poseSendLock)
            {
                if (receiverEndPoint != null) SetTransmissionEnabled(false);
                InvalidateCameraControl();
                sourceEpoch++;
                receiverEndPoint = source == null ? null : new IPEndPoint(IPAddress.Parse(source.Ip), source.Port);
                lock (poseSnapshotLock) hasPoseSnapshot = false;
                ResetInputHistory();
                safetyState.ResetForSourceChange();
                IsRecording = false;
                ActiveRecordingFile = null;
                LastRecordingMessage = null;
                ReplayState = "idle";
                ReplayRequiresReset = false;
                LastReplayMessage = null;
            }
            nextDiscoveryTime = now;
        }
        bool busyChanged = IsOccupiedByOther != (source != null && source.Busy) || OccupiedPeerIp != source?.Owner;
        IsOccupiedByOther = source != null && source.Busy;
        OccupiedPeerIp = source?.Owner;
        if (source != null) lastReceiverReplyTime = source.LastSeen;
        if (IsOccupiedByOther)
        {
            InvalidateCameraControl();
            if (transmissionEnabled) SetTransmissionEnabled(false);
        }
        if (changed || busyChanged) NetworkStatusChanged?.Invoke();
    }

    private IEnumerator CameraOwnershipLoop()
    {
        while (true)
        {
            string ip = ReceiverIpAddress;
            int epoch = sourceEpoch;
            double requestedAt = Time.realtimeSinceStartupAsDouble;
            if (!transmissionEnabled || IsOccupiedByOther || string.IsNullOrEmpty(ip))
            {
                InvalidateCameraControl();
                yield return new WaitForSecondsRealtime(0.25f);
                continue;
            }
            using (var request = UnityWebRequest.Get("http://" + ip + ":" + deviceStatusPort + "/api/status"))
            {
                request.timeout = 2;
                yield return request.SendWebRequest();
                if (epoch != sourceEpoch || ReceiverIpAddress != ip || !transmissionEnabled || IsOccupiedByOther) continue;
                SessionStatus status = null;
                if (request.result == UnityWebRequest.Result.Success)
                {
                    try { status = JsonUtility.FromJson<SessionStatus>(request.downloadHandler.text); }
                    catch (Exception) { }
                }
                InvalidateCameraControl();
                if (status?.vr != null)
                {
                    verifiedServerIp = ip;
                    cameraOwnerIp = status.vr.active_peer;
                    cameraOwnerLocked = status.vr.is_locked;
                    lastOwnershipReply = requestedAt;
                }
            }
            yield return new WaitForSecondsRealtime(0.5f);
        }
    }
}
