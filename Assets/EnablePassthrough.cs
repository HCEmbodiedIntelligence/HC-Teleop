using UnityEngine;
using ByteDance.PICO.XR;

public class EnablePassthrough : MonoBehaviour
{
    private void Start()
    {
        PXR_Manager.EnableVideoSeeThrough = true;
    }

    private void OnDestroy()
    {
        PXR_Manager.EnableVideoSeeThrough = false;
    }
}