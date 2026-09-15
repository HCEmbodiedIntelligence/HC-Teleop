using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class HCTeleopReleaseBuild
{
    [MenuItem("HC-Teleop/Build Release APK")]
    public static void BuildRelease()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before building a release.");

        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string outputDirectory = Path.Combine(projectRoot, "Builds");
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory,
            "HC-Teleop-v" + PlayerSettings.bundleVersion + ".apk");

        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" },
            locationPathName = outputPath,
            target = BuildTarget.Android,
            options = BuildOptions.None
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("APK build failed: " + report.summary.result);
        Debug.Log("[Release] APK ready: " + outputPath);
    }
}
