using UnityEngine;
using UnityEngine.XR;

public class ControllerPoseReader : MonoBehaviour
{
    [Header("追踪对象")]
    public Transform head;
    public Transform leftController;
    public Transform rightController;

    [Header("头部位姿")]
    public Vector3 headPosition;
    public Vector3 headRotation;
    public Quaternion headQuaternion;

    [Header("左手柄位姿")]
    public Vector3 leftPosition;
    public Vector3 leftRotation;
    public Quaternion leftQuaternion;

    [Header("右手柄位姿")]
    public Vector3 rightPosition;
    public Vector3 rightRotation;
    public Quaternion rightQuaternion;

    private InputDevice leftDevice;
    private InputDevice rightDevice;

    void Update()
    {
        // Main Camera 已经由 XR 系统自动追踪，
        //这里只读取，不修改它的位置
        if (head != null)
        {
            headPosition = head.localPosition;
            headRotation = head.localEulerAngles;
            headQuaternion = head.localRotation;
        }

        if (!leftDevice.isValid)
        {
            leftDevice =
                InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        }

        if (!rightDevice.isValid)
        {
            rightDevice =
                InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        }

        UpdateController(
            leftDevice,
            leftController,
            ref leftPosition,
            ref leftRotation,
            ref leftQuaternion
        );

        UpdateController(
            rightDevice,
            rightController,
            ref rightPosition,
            ref rightRotation,
            ref rightQuaternion
        );
    }

    private void UpdateController(
        InputDevice device,
        Transform controller,
        ref Vector3 positionValue,
        ref Vector3 rotationValue,
        ref Quaternion quaternionValue)
    {
        if (!device.isValid || controller == null)
            return;

        if (device.TryGetFeatureValue(
                CommonUsages.devicePosition,
                out Vector3 position))
        {
            controller.localPosition = position;
            positionValue = position;
        }

        if (device.TryGetFeatureValue(
                CommonUsages.deviceRotation,
                out Quaternion rotation))
        {
            controller.localRotation = rotation;
            rotationValue = rotation.eulerAngles;
            quaternionValue = rotation;
        }
    }
}