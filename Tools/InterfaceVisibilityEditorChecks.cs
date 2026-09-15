#if UNITY_EDITOR
// Optional integration check: copy this file into Assets/Editor, then use
// HC-Teleop/Checks/Run Isolated Interface Visibility Regression. The isolated
// Edit Mode route never starts application networking or enters Play Mode.
// This file stays outside Assets so it is not part of the application build.
using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public static class InterfaceVisibilityEditorChecks
{
    private static int assertions;

    [MenuItem("HC-Teleop/Checks/Run Isolated Interface Visibility Regression")]
    public static void RunIsolatedEditMode()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before running isolated visibility checks.");

        SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
        for (int index = 0; index < SceneManager.sceneCount; index++)
        {
            Scene scene = SceneManager.GetSceneAt(index);
            if (scene.isDirty || string.IsNullOrEmpty(scene.path))
                throw new InvalidOperationException("Save open scenes before isolated checks so their setup can be restored.");
        }

        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Run();
        }
        finally
        {
            EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
        }
    }

    [MenuItem("HC-Teleop/Checks/Run Interface Visibility Regression")]
    public static void Run()
    {
        if (!EditorApplication.isPlaying &&
            (UnityEngine.Object.FindObjectsOfType<GripDraggablePanel>(true).Length > 0 ||
             UnityEngine.Object.FindObjectsOfType<UdpPoseSender>(true).Length > 0))
        {
            throw new InvalidOperationException("Use RunIsolatedEditMode to keep Edit Mode checks separate from application objects.");
        }
        if (!InterfaceVisibilityController.IsInterfaceVisible)
        {
            Debug.LogError("Show the application interface before running visibility checks.");
            return;
        }

        GameObject testRig = null;
        GameObject firstRoot = null;
        GameObject secondRoot = null;
        InterfaceVisibilityController visibility = null;
        assertions = 0;
        try
        {
            testRig = new GameObject("InterfaceVisibilityCheck_Rig");
            visibility = testRig.AddComponent<InterfaceVisibilityController>();
            firstRoot = CreateCanvas("InterfaceVisibilityCheck_Canvas1", 0.67f, true, true);
            secondRoot = CreateCanvas("InterfaceVisibilityCheck_Canvas2", 0.25f, false, false);
            CanvasGroup firstGroup = firstRoot.GetComponent<CanvasGroup>();
            CanvasGroup secondGroup = secondRoot.GetComponent<CanvasGroup>();
            GripDraggablePanel openPanel = CreatePanel(firstRoot.transform, "OpenCamera", 0.42f, false, true);
            GripDraggablePanel closedPanel = CreatePanel(firstRoot.transform, "ClosedCamera", 0f, false, false);
            GripDraggablePanel secondPanel = CreatePanel(secondRoot.transform, "OtherDashboard", 1f, true, true);
            CanvasGroup childGroup = openPanel.GetComponent<CanvasGroup>();

            Check(CanInteract(openPanel), "visible panel accepts custom drag hit tests");
            visibility.SetVisible(false);
            Check(!InterfaceVisibilityController.IsInterfaceVisible, "global hidden state is set");
            AssertGroup(firstGroup, 0f, false, false, "first canvas hidden");
            AssertGroup(secondGroup, 0f, false, false, "second canvas hidden");
            AssertGroup(childGroup, 0.42f, false, true, "open camera settings survive hiding");
            AssertGroup(closedPanel.GetComponent<CanvasGroup>(), 0f, false, false, "closed camera remains closed");
            Check(openPanel.isActiveAndEnabled && secondPanel.isActiveAndEnabled &&
                  openPanel.gameObject.activeInHierarchy, "panel objects and scripts stay active");
            Check(!CanInteract(openPanel), "hidden panels reject custom drag hit tests");
            Canvas.ForceUpdateCanvases();
            Check(Mathf.Approximately(openPanel.GetComponent<CanvasRenderer>().GetInheritedAlpha(), 0f),
                "open camera graphic actually inherits hidden alpha");

            GameObject clone = UnityEngine.Object.Instantiate(openPanel.gameObject, firstRoot.transform, false);
            clone.name = "DiscoveredWhileHidden";
            Canvas.ForceUpdateCanvases();
            Check(clone.activeInHierarchy && clone.GetComponent<GripDraggablePanel>().isActiveAndEnabled,
                "camera cloned while hidden stays active");
            Check(Mathf.Approximately(clone.GetComponent<CanvasRenderer>().GetInheritedAlpha(), 0f),
                "camera cloned while hidden is immediately invisible");
            Check(!CanInteract(clone.GetComponent<GripDraggablePanel>()), "new hidden camera cannot be dragged");

            visibility.SetVisible(false);
            visibility.SetVisible(true);
            Check(InterfaceVisibilityController.IsInterfaceVisible, "show restores global state");
            AssertGroup(firstGroup, 0.67f, true, true, "repeated hide preserves first root snapshot");
            AssertGroup(secondGroup, 0.25f, false, false, "root settings with disabled interaction are preserved");
            AssertGroup(childGroup, 0.42f, false, true, "show preserves open camera settings");
            AssertGroup(closedPanel.GetComponent<CanvasGroup>(), 0f, false, false, "show does not open closed camera");
            AssertGroup(clone.GetComponent<CanvasGroup>(), 0.42f, false, true, "new camera retains its own settings");
            Check(CanInteract(openPanel), "show restores custom drag hit eligibility");
            Canvas.ForceUpdateCanvases();
            Check(clone.GetComponent<CanvasRenderer>().GetInheritedAlpha() > 0f,
                "new camera becomes visible with its parent");

            visibility.ToggleVisibility();
            Check(!InterfaceVisibilityController.IsInterfaceVisible, "toggle hides visible UI");
            if (EditorApplication.isPlaying)
            {
                visibility.enabled = false;
                Check(InterfaceVisibilityController.IsInterfaceVisible, "real OnDisable restores global visibility");
                AssertGroup(firstGroup, 0.67f, true, true, "disable restores first root values");
                AssertGroup(secondGroup, 0.25f, false, false, "disable restores second root values");
                Check(openPanel.isActiveAndEnabled, "disabling visibility controller does not disable panel scripts");
            }
            else
            {
                // Non-ExecuteAlways scripts do not receive reliable automatic
                // lifecycle callbacks in Edit Mode. Exercise the public toggle;
                // the optional Play Mode run additionally checks real OnDisable.
                visibility.ToggleVisibility();
                Check(InterfaceVisibilityController.IsInterfaceVisible, "second toggle shows hidden UI");
                AssertGroup(firstGroup, 0.67f, true, true, "second toggle restores first root values");
                AssertGroup(secondGroup, 0.25f, false, false, "second toggle restores second root values");
            }
            Debug.Log("PASS: " + assertions + " interface-visibility integration assertions (" +
                (EditorApplication.isPlaying ? "Play Mode" : "isolated Edit Mode; automatic OnDisable not exercised") + ")");
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            throw;
        }
        finally
        {
            if (visibility != null)
                visibility.SetVisible(true);
            if (testRig != null)
                UnityEngine.Object.DestroyImmediate(testRig);
            if (firstRoot != null)
                UnityEngine.Object.DestroyImmediate(firstRoot);
            if (secondRoot != null)
                UnityEngine.Object.DestroyImmediate(secondRoot);
        }
    }

    private static GameObject CreateCanvas(string name, float alpha, bool interactable, bool blocksRaycasts)
    {
        GameObject root = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup));
        root.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        CanvasGroup group = root.GetComponent<CanvasGroup>();
        group.alpha = alpha;
        group.interactable = interactable;
        group.blocksRaycasts = blocksRaycasts;
        return root;
    }

    private static GripDraggablePanel CreatePanel(Transform parent, string name, float alpha,
        bool interactable, bool blocksRaycasts)
    {
        GameObject panel = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer),
            typeof(Image), typeof(CanvasGroup));
        panel.transform.SetParent(parent, false);
        CanvasGroup group = panel.GetComponent<CanvasGroup>();
        group.alpha = alpha;
        group.interactable = interactable;
        group.blocksRaycasts = blocksRaycasts;
        return panel.AddComponent<GripDraggablePanel>();
    }

    private static bool CanInteract(GripDraggablePanel panel)
    {
        MethodInfo method = typeof(GripDraggablePanel).GetMethod("IsPanelInteractionEnabled",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return (bool)method.Invoke(panel, null);
    }

    private static void AssertGroup(CanvasGroup group, float alpha, bool interactable,
        bool blocksRaycasts, string description)
    {
        Check(Mathf.Approximately(group.alpha, alpha) && group.interactable == interactable &&
              group.blocksRaycasts == blocksRaycasts, description);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new Exception("FAIL: " + description);
        assertions++;
    }
}
#endif
