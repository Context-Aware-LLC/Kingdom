using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace Emberfall.Editor
{
    /// <summary>
    /// Authors <c>Assets/Resources/Audio/MasterMixer.mixer</c> — the AudioMixer
    /// <see cref="Emberfall.Presentation.AudioSettings"/> loads via
    /// <c>Resources.Load&lt;AudioMixer&gt;("Audio/MasterMixer")</c>. Master (the default root) gets
    /// two child groups, Music and SFX, each exposing its Volume attenuation as a float parameter
    /// named MusicVolume / SfxVolume — the exact names AudioSettings calls SetFloat/GetFloat with.
    ///
    /// <para><b>Why <c>Assets/Resources/Audio/</c>, not just <c>Assets/Audio/</c>.</b>
    /// <c>Resources.Load</c> only ever looks inside a folder literally named <c>Resources</c>
    /// anywhere on the asset's path — <c>AssetDatabase.LoadAssetAtPath</c> does not have that
    /// restriction, so a mixer placed at a flat <c>Assets/Audio/MasterMixer.mixer</c> would still
    /// satisfy an editor-time existence check while returning null at runtime, the exact failure
    /// <see cref="Emberfall.Presentation.AudioSettings.EnsureMixer"/> is built to degrade
    /// silently from. This repo's existing convention (<c>Assets/Resources/SupabaseAuthSettings.asset</c>,
    /// <c>Assets/UI/MatchSettings/Resources/MatchSettingsScreen.uxml</c>) is a <c>Resources</c>
    /// folder holding the exact relative path the loader asks for.</para>
    ///
    /// <para><b>Why reflection.</b> There is no public UnityEditor API for creating AudioMixer
    /// groups or exposing a parameter — only the Editor GUI ("Expose to script" on a group's Volume
    /// slider) does it directly. The type that does the real work,
    /// <c>UnityEditor.Audio.AudioMixerController</c>, is <c>internal sealed</c>, so every call
    /// below goes through <see cref="Type.GetMethod(string, BindingFlags)"/> rather than a
    /// <c>using UnityEditor.Audio;</c> — that namespace's controller types cannot appear in this
    /// file's signatures at all, only behind <see cref="object"/>. Verified against Unity 6000.5
    /// by reflecting the type's member list before writing this; if a future Unity version renames
    /// one of these members, this script fails loudly (NullReferenceException on the missing
    /// MethodInfo) rather than producing a half-built mixer.</para>
    ///
    /// <para>Idempotent by refusal, not by merge: if the asset already exists this is a no-op,
    /// matching <see cref="MaterialSetup"/>'s house style of never clobbering hand-tuned output.
    /// Delete the asset first to rebuild it.</para>
    /// </summary>
    public static class CreateMasterMixer
    {
        private const string Folder = "Assets/Resources/Audio";
        private const string AssetPath = Folder + "/MasterMixer.mixer";
        private const string MusicGroupName = "Music";
        private const string SfxGroupName = "SFX";
        private const string MusicParam = "MusicVolume";
        private const string SfxParam = "SfxVolume";

        private const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static;

        [MenuItem("Emberfall/Audio/Create Master Mixer")]
        public static void Run()
        {
            if (AssetDatabase.LoadAssetAtPath<AudioMixer>(AssetPath) != null)
            {
                Debug.Log($"[CreateMasterMixer] {AssetPath} already exists — leaving it alone.");
                return;
            }

            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets/Resources", "Audio");

            Assembly editorAsm = typeof(AssetDatabase).Assembly;
            Type controllerType = editorAsm.GetType("UnityEditor.Audio.AudioMixerController");
            Type groupType = editorAsm.GetType("UnityEditor.Audio.AudioMixerGroupController");
            Type exposedParamType = editorAsm.GetType("UnityEditor.Audio.ExposedAudioParameter");
            if (controllerType == null || groupType == null || exposedParamType == null)
                throw new InvalidOperationException(
                    "[CreateMasterMixer] UnityEditor.Audio internals not found — Unity version mismatch?");

            // Static factory: writes the .mixer asset to disk with a single Master group already
            // in place (mirrors Assets > Create > Audio Mixer).
            object controller = controllerType
                .GetMethod("CreateMixerControllerAtPath", AnyMember)
                .Invoke(null, new object[] { AssetPath });

            object masterGroup = controllerType.GetProperty("masterGroup", AnyMember).GetValue(controller);

            object musicGroup = AddChildGroup(controllerType, controller, masterGroup, MusicGroupName);
            object sfxGroup = AddChildGroup(controllerType, controller, masterGroup, SfxGroupName);

            MethodInfo getGuidForVolume = groupType.GetMethod("GetGUIDForVolume", AnyMember);
            FieldInfo guidField = exposedParamType.GetField("guid", AnyMember);
            FieldInfo nameField = exposedParamType.GetField("name", AnyMember);

            Array exposed = Array.CreateInstance(exposedParamType, 2);
            exposed.SetValue(MakeExposedParameter(exposedParamType, guidField, nameField,
                getGuidForVolume.Invoke(musicGroup, null), MusicParam), 0);
            exposed.SetValue(MakeExposedParameter(exposedParamType, guidField, nameField,
                getGuidForVolume.Invoke(sfxGroup, null), SfxParam), 1);

            controllerType.GetProperty("exposedParameters", AnyMember).SetValue(controller, exposed);

            EditorUtility.SetDirty((UnityEngine.Object)controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[CreateMasterMixer] created {AssetPath} — Master > (Music, SfxVolume exposed).");
        }

        private static object AddChildGroup(Type controllerType, object controller, object masterGroup, string name)
        {
            object group = controllerType.GetMethod("CreateNewGroup", AnyMember)
                .Invoke(controller, new object[] { name, true });
            controllerType.GetMethod("AddChildToParent", AnyMember)
                .Invoke(controller, new object[] { group, masterGroup });
            return group;
        }

        private static object MakeExposedParameter(Type exposedParamType, FieldInfo guidField, FieldInfo nameField,
            object guid, string paramName)
        {
            object param = Activator.CreateInstance(exposedParamType);
            guidField.SetValue(param, guid);
            nameField.SetValue(param, paramName);
            return param;
        }
    }
}
