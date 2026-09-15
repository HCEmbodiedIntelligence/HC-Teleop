using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class InterfaceVisibilityController : MonoBehaviour
{
    private struct CanvasState
    {
        public float alpha;
        public bool interactable;
        public bool blocksRaycasts;
    }

    private readonly Dictionary<CanvasGroup, CanvasState> hiddenGroups =
        new Dictionary<CanvasGroup, CanvasState>();
    private float nextCanvasDiscoveryTime;

    public static bool IsInterfaceVisible { get; private set; } = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticState()
    {
        IsInterfaceVisible = true;
    }

    public void ToggleVisibility()
    {
        SetVisible(!IsInterfaceVisible);
    }

    public void SetVisible(bool visible)
    {
        bool changed = IsInterfaceVisible != visible;
        IsInterfaceVisible = visible;
        if (changed)
            Debug.Log("[Interface] visible=" + visible);
        if (visible)
        {
            RestoreCanvasGroups();
            return;
        }

        GripDraggablePanel.EndAllDrags();
        HideApplicationCanvases();
        nextCanvasDiscoveryTime = Time.unscaledTime + 0.5f;
    }

    private void Update()
    {
        if (IsInterfaceVisible || Time.unscaledTime < nextCanvasDiscoveryTime)
            return;

        // Existing and newly cloned camera panels inherit their root group.
        // Discover again only to include newly created application canvases.
        HideApplicationCanvases();
        nextCanvasDiscoveryTime = Time.unscaledTime + 0.5f;
    }

    private void HideApplicationCanvases()
    {
        foreach (GripDraggablePanel panel in FindObjectsOfType<GripDraggablePanel>(true))
        {
            Canvas canvas = panel.GetComponentInParent<Canvas>(true);
            if (canvas == null)
                continue;

            Canvas rootCanvas = canvas.rootCanvas;
            CanvasGroup group = rootCanvas.GetComponent<CanvasGroup>();
            if (group == null)
                group = rootCanvas.gameObject.AddComponent<CanvasGroup>();

            if (!hiddenGroups.ContainsKey(group))
            {
                hiddenGroups.Add(group, new CanvasState
                {
                    alpha = group.alpha,
                    interactable = group.interactable,
                    blocksRaycasts = group.blocksRaycasts
                });
            }

            // Keep scripts and per-camera visibility active so hiding the
            // interface never stops WebRTC or changes which streams are open.
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
        }
    }

    private void RestoreCanvasGroups()
    {
        foreach (KeyValuePair<CanvasGroup, CanvasState> entry in hiddenGroups)
        {
            if (entry.Key == null)
                continue;

            entry.Key.alpha = entry.Value.alpha;
            entry.Key.interactable = entry.Value.interactable;
            entry.Key.blocksRaycasts = entry.Value.blocksRaycasts;
        }
        hiddenGroups.Clear();
    }

    private void OnDisable()
    {
        SetVisible(true);
    }

    private void OnDestroy()
    {
        RestoreCanvasGroups();
    }
}
