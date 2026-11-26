using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections;
using ByteDance.PICO.XR;
using UnityEngine.Android;
using UnityEngine.XR;
using UnityEngine.XR.Management;
#endif

[DisallowMultipleComponent]
public sealed class MrSafeguardSpatialMesh : MonoBehaviour
{
#if UNITY_ANDROID && !UNITY_EDITOR
    private const string SpatialDataPermission = "com.picovr.permission.SPATIAL_DATA";
    private bool configured;
    private bool permissionRequested;
    private Coroutine startMesh;
    private XRMeshSubsystem ownedMesh;

    private void OnEnable()
    {
        var settings = PXR_ProjectSetting.GetProjectConfig();
        configured = settings != null && settings.mrSafeguard && settings.spatialMesh;
        if (!configured)
            return;

        if (!Permission.HasUserAuthorizedPermission(SpatialDataPermission) && !permissionRequested)
        {
            permissionRequested = true;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => EnsureMeshStarted();
            callbacks.PermissionDenied += _ =>
                Debug.LogWarning("[MR Safeguard] Spatial data permission denied; mesh was not started.");
            Permission.RequestUserPermission(SpatialDataPermission, callbacks);
        }

        EnsureMeshStarted();
    }

    private void OnApplicationPause(bool paused)
    {
        if (!paused)
            EnsureMeshStarted();
    }

    private void OnApplicationFocus(bool focused)
    {
        if (focused)
            EnsureMeshStarted();
    }

    private void EnsureMeshStarted()
    {
        if (configured && isActiveAndEnabled && startMesh == null &&
            Permission.HasUserAuthorizedPermission(SpatialDataPermission))
            startMesh = StartCoroutine(StartSpatialMesh());
    }

    // Start the SDK's obstacle mesh without instantiating or displaying Unity meshes.
    private IEnumerator StartSpatialMesh()
    {
        float timeout = Time.realtimeSinceStartup + 5f;
        do
        {
            yield return null;
            var loader = XRGeneralSettings.Instance?.Manager?.activeLoader as PXR_Loader;
            var mesh = loader?.meshSubsystem;
            if (mesh == null)
                continue;

            if (!mesh.running)
            {
                mesh.Start();
                if (mesh.running)
                    ownedMesh = mesh;
                else
                    Debug.LogWarning("[MR Safeguard] PICO spatial mesh did not start.");
            }

            startMesh = null;
            yield break;
        }
        while (Time.realtimeSinceStartup < timeout);

        startMesh = null;
        Debug.LogWarning("[MR Safeguard] PICO spatial mesh subsystem is unavailable.");
    }

    private void OnDisable()
    {
        if (startMesh != null)
        {
            StopCoroutine(startMesh);
            startMesh = null;
        }

        if (ownedMesh != null && ownedMesh.running)
            ownedMesh.Stop();
        ownedMesh = null;
    }
#endif
}
