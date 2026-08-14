using UnityEngine;

public class RuntimeAxesMarker : MonoBehaviour
{
    public float axisLength = 0.15f;
    public float lineWidth = 0.006f;
    public float arrowSize = 0.025f;

    void Start()
    {
        // Unity标准坐标颜色
        CreateAxis(
            "X Axis",
            Vector3.right,
            Vector3.up,
            Color.red
        );

        CreateAxis(
            "Y Axis",
            Vector3.up,
            Vector3.right,
            Color.green
        );

        CreateAxis(
            "Z Axis",
            Vector3.forward,
            Vector3.up,
            Color.blue
        );
    }

    private void CreateAxis(
        string axisName,
        Vector3 direction,
        Vector3 perpendicular,
        Color color)
    {
        Vector3 tip = direction * axisLength;

        // 坐标轴主体
        LineRenderer shaft = CreateLine(
            axisName + " Shaft",
            color
        );

        shaft.positionCount = 2;
        shaft.SetPosition(0, Vector3.zero);
        shaft.SetPosition(1, tip);

        // 箭头的V形部分
        LineRenderer arrow = CreateLine(
            axisName + " Arrow",
            color
        );

        Vector3 arrowBase =
            tip - direction * arrowSize;

        Vector3 side =
            perpendicular.normalized * arrowSize * 0.5f;

        arrow.positionCount = 3;
        arrow.SetPosition(0, arrowBase + side);
        arrow.SetPosition(1, tip);
        arrow.SetPosition(2, arrowBase - side);
    }

    private LineRenderer CreateLine(
        string objectName,
        Color color)
    {
        GameObject lineObject =
            new GameObject(objectName);

        lineObject.transform.SetParent(
            transform,
            false
        );

        LineRenderer line =
            lineObject.AddComponent<LineRenderer>();

        line.useWorldSpace = false;
        line.startWidth = lineWidth;
        line.endWidth = lineWidth;
        line.numCapVertices = 4;
        line.numCornerVertices = 2;

        Shader shader =
            Shader.Find("Sprites/Default");

        line.material = new Material(shader);
        line.startColor = color;
        line.endColor = color;

        return line;
    }
}