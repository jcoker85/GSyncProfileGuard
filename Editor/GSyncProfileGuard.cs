using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GSyncProfileGuard.Editor
{
    /// <summary>
    /// Keeps G-Sync disallowed in the NVIDIA driver profile that applies to the running
    /// editor's Unity.exe.
    ///
    /// Why: NVIDIA's predefined "Unity 3D" profile (which claims every "unity.exe") sets
    /// G-Sync to "full screen and windowed", overriding the global setting. Focusing the
    /// D3D12 editor then switches the displays into variable refresh. Some monitors blank
    /// while re-locking, on every display, and some don't come back. Disallowing G-Sync for
    /// the editor fixes it while leaving G-Sync on for games. Driver updates can reset
    /// profiles, so this checks once per editor session and puts the settings back.
    ///
    /// Driver profile settings only take effect when a process starts, so after a fix the
    /// editor has to be restarted.
    /// </summary>
    [InitializeOnLoad]
    internal static class GSyncProfileGuard
    {
        const string Title = "G-Sync Profile Guard";
        const string MenuRoot = "Tools/G-Sync Profile Guard/";
        const string PrefCheckOnStartup = "GSyncProfileGuard.CheckOnStartup";
        const string SessionCheckedKey = "GSyncProfileGuard.CheckedThisSession";
        const string OwnProfileName = "Unity Editor (G-Sync Profile Guard)";
        const string UnityExe = "unity.exe";

        // NVAPI DRS setting IDs (NvApiDriverSettings.h) and the values that fix the flashes.
        // Profile Inspector shows them under "02 - Sync and Refresh".
        const uint VRR_MODE_ID = 0x1194F158;                       // 0 off, 1 full screen, 2 full screen + windowed
        const uint VRR_APP_OVERRIDE_ID = 0x10A879CF;               // 0 allow, 1 force off, 2 disallow, 4 fixed refresh
        const uint VRR_APP_OVERRIDE_REQUEST_STATE_ID = 0x10A879AC; // same values as above

        static readonly (uint id, uint value, string label)[] Desired =
        {
            (VRR_MODE_ID, 0, "G-SYNC mode = Off"),
            (VRR_APP_OVERRIDE_ID, 2, "G-SYNC Application State = Disallow"),
            (VRR_APP_OVERRIDE_REQUEST_STATE_ID, 2, "G-SYNC Requested State = Disallow"),
        };

        static GSyncProfileGuard()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) return;
            if (!EditorPrefs.GetBool(PrefCheckOnStartup, true)) return;
            if (SessionState.GetBool(SessionCheckedKey, false)) return; // once per editor session, not per domain reload
            SessionState.SetBool(SessionCheckedKey, true);
            EditorApplication.delayCall += () => Run(interactive: false);
        }

        // ---------------- Menu ----------------

        [MenuItem(MenuRoot + "Check Now", priority = 0)]
        static void CheckNow() => Run(interactive: true);

        [MenuItem(MenuRoot + "Check Now", validate = true)]
        static bool CheckNowValidate() => Application.platform == RuntimePlatform.WindowsEditor;

        [MenuItem(MenuRoot + "Check On Startup", priority = 20)]
        static void ToggleCheckOnStartup()
            => EditorPrefs.SetBool(PrefCheckOnStartup, !EditorPrefs.GetBool(PrefCheckOnStartup, true));

        [MenuItem(MenuRoot + "Check On Startup", validate = true)]
        static bool ToggleCheckOnStartupValidate()
        {
            Menu.SetChecked(MenuRoot + "Check On Startup", EditorPrefs.GetBool(PrefCheckOnStartup, true));
            return Application.platform == RuntimePlatform.WindowsEditor;
        }

        [MenuItem(MenuRoot + "Restore NVIDIA Defaults For This Editor", priority = 40)]
        static void RestoreDefaults()
        {
            if (!EditorUtility.DisplayDialog(Title,
                    "Remove this tool's G-Sync settings from the NVIDIA profile used by this editor?\n\n" +
                    "Also turn off \"Check On Startup\", or they'll be put back next time the editor starts.",
                    "Restore Defaults", "Cancel"))
                return;
            try
            {
                string appPath = Path.GetFullPath(EditorApplication.applicationPath);
                using (var drs = new NvApiDrs())
                {
                    if (!drs.TryFindApplication(appPath, out IntPtr profile, out _))
                    {
                        EditorUtility.DisplayDialog(Title, "No NVIDIA profile applies to this editor. Nothing to restore.", "OK");
                        return;
                    }
                    drs.GetProfileInfo(profile, out string name, out _);
                    foreach (var d in Desired) drs.RestoreDefault(profile, d.id);
                    drs.Save();
                    EditorUtility.DisplayDialog(Title, "Restored NVIDIA's defaults in profile '" + name + "'. Restart the editor for it to take effect.", "OK");
                }
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog(Title, "Couldn't restore the defaults:\n\n" + e.Message, "OK");
            }
        }

        [MenuItem(MenuRoot + "Restore NVIDIA Defaults For This Editor", validate = true)]
        static bool RestoreDefaultsValidate() => Application.platform == RuntimePlatform.WindowsEditor;

        // ---------------- Check / fix ----------------

        static void Run(bool interactive)
        {
            Result result;
            try
            {
                result = Apply(EditorApplication.applicationPath);
            }
            catch (DllNotFoundException)
            {
                // No NVIDIA driver on this machine: nothing to do.
                if (interactive)
                    EditorUtility.DisplayDialog(Title, "No NVIDIA driver found (nvapi64.dll is missing). Nothing to do on this machine.", "OK");
                return;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[" + Title + "] Couldn't check the NVIDIA profile: " + e.Message);
                if (interactive)
                    EditorUtility.DisplayDialog(Title, "Couldn't check the NVIDIA profile:\n\n" + e.Message, "OK");
                return;
            }

            if (result.Refused != null)
            {
                Debug.LogWarning("[" + Title + "] " + result.Refused);
                if (interactive) EditorUtility.DisplayDialog(Title, result.Refused, "OK");
                return;
            }

            if (result.Changes.Count == 0)
            {
                if (interactive)
                    EditorUtility.DisplayDialog(Title, "All good. G-Sync is disallowed for this editor.\n\n" + result.Describe(), "OK");
                return;
            }

            string message = "Fixed the NVIDIA profile for this editor.\n\n" + result.Describe() +
                             "\n\nThe driver only applies profile changes when the editor starts. Restart the editor now?";
            Debug.LogWarning("[" + Title + "] " + message.Replace("\n\n", "\n"));

            if (EditorUtility.DisplayDialog(Title, message, "Restart Editor", "Later"))
                RestartEditor();
        }

        sealed class Result
        {
            public string AppPath;
            public string ProfileName;
            public bool ProfileIsPredefined;
            public bool CreatedProfile;
            public string Refused;
            public readonly List<string> Changes = new List<string>();

            public string Describe()
            {
                string s = "Editor: " + AppPath +
                           "\nNVIDIA profile: '" + ProfileName + "'" +
                           (CreatedProfile ? " (created)" : ProfileIsPredefined ? " (NVIDIA built-in, applies to every unity.exe)" : "");
                foreach (var d in Desired) s += "\n  • " + d.label;
                if (Changes.Count > 0) s += "\n\nChanged:\n  • " + string.Join("\n  • ", Changes);
                return s;
            }
        }

        static Result Apply(string editorPath)
        {
            string appPath = Path.GetFullPath(editorPath);
            var result = new Result { AppPath = appPath };

            using (var drs = new NvApiDrs())
            {
                // Fix the profile the driver actually applies to this exe: a user profile that
                // names it by full path if there is one, otherwise NVIDIA's predefined "Unity 3D"
                // profile, which claims every "unity.exe". The driver refuses to add another
                // "unity.exe" entry while that profile exists, so creating our own profile is
                // only possible when nothing matches at all.
                IntPtr profile;
                if (drs.TryFindApplication(appPath, out profile, out _))
                {
                    // Only touch a profile that covers nothing but Unity editors.
                    string[] apps = drs.GetApplicationNames(profile);
                    string[] others = apps.Where(a => !IsUnityExe(a)).ToArray();
                    drs.GetProfileInfo(profile, out result.ProfileName, out result.ProfileIsPredefined);
                    if (others.Length > 0)
                    {
                        result.Refused =
                            "The NVIDIA profile '" + result.ProfileName + "' that applies to this editor also covers other programs (" +
                            string.Join(", ", others.Take(5)) + (others.Length > 5 ? ", ..." : "") +
                            "), so it was left alone. Move Unity.exe to its own profile in NVIDIA Profile Inspector, then run Check Now.";
                        return result;
                    }
                }
                else
                {
                    if (!drs.TryFindProfile(OwnProfileName, out profile))
                    {
                        profile = drs.CreateProfile(OwnProfileName);
                        result.CreatedProfile = true;
                    }
                    drs.AddApplication(profile, appPath);
                    result.Changes.Add("Added this editor's exe to the profile");
                    drs.GetProfileInfo(profile, out result.ProfileName, out result.ProfileIsPredefined);
                }

                foreach (var d in Desired)
                {
                    bool has = drs.TryGetDword(profile, d.id, out uint current);
                    if (has && current == d.value) continue;
                    drs.SetDword(profile, d.id, d.value);
                    result.Changes.Add(d.label + (has ? " (was " + current + ")" : " (was not set)"));
                }

                if (result.Changes.Count > 0) drs.Save();
            }
            return result;
        }

        static bool IsUnityExe(string appName)
        {
            if (string.IsNullOrEmpty(appName)) return false;
            string file = appName.Replace('\\', '/');
            int slash = file.LastIndexOf('/');
            if (slash >= 0) file = file.Substring(slash + 1);
            return string.Equals(file, UnityExe, StringComparison.OrdinalIgnoreCase);
        }

        static void RestartEditor()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorApplication.OpenProject(Directory.GetCurrentDirectory());
        }
    }
}
