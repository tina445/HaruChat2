#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Callbacks;
using UnityEngine;

namespace HaruChat.Editor
{
    /// <summary>Repository-owned UBA hook. Dashboard invokes ValidateIosBuildInputs before export.</summary>
    public static class HaruChatBuildAutomation
    {
        public static void ValidateIosBuildInputs()
        {
            if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS) ValidateNativePlugin();
        }

        [PostProcessBuild(900)]
        private static void VerifyIosExport(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS) return;
            ValidateNativePlugin();
            if (string.IsNullOrWhiteSpace(pathToBuiltProject)) throw new BuildFailedException("Unity iOS export path is empty.");
        }

        private static void ValidateNativePlugin()
        {
            var root = Path.Combine(Application.dataPath, "Plugins", "iOS", "LlmCore.xcframework");
            if (!Directory.Exists(root)) throw new BuildFailedException("LlmCore.xcframework is not staged. Run the UBA pre-build hook first.");
            if (!Directory.Exists(Path.Combine(root, "ios-arm64"))) throw new BuildFailedException("LlmCore.xcframework has no ios-arm64 slice.");
        }
    }
}
#endif
