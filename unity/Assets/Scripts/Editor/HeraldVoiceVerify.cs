using System;
using System.Collections.Generic;
using System.IO;
using Kingdom.Presentation;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;

namespace Kingdom.Editor
{
    public static class HeraldVoiceVerify
    {
        [MenuItem("Kingdom/Verify/Herald Voice")]
        public static void Run()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var addresses = new HashSet<string>();
            int recordings = 0;
            foreach (string file in Directory.GetFiles("Assets/Sound/Voice/Herald", "*.mp3"))
            {
                string path = file.Replace('\\', '/');
                string address = "vo/" + Path.GetFileNameWithoutExtension(path);
                string guid = AssetDatabase.AssetPathToGUID(path);
                var entry = settings.FindAssetEntry(guid);
                Require(entry != null && entry.address == address && entry.parentGroup.Name == "Voice",
                    $"Herald recording must ship in Voice at {address}.");
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                Require(clip != null && clip.samples > 0 && clip.length > 0f,
                    $"Herald recording must import as playable audio: {path}.");
                addresses.Add(address);
                recordings++;
            }

            var tutorialAddresses = new HashSet<string>();
            foreach (TidbitTrigger trigger in Enum.GetValues(typeof(TidbitTrigger)))
                foreach (Tidbit tidbit in TidbitCatalog.For(trigger))
                {
                    Require(addresses.Contains(tidbit.VoiceAddress),
                        $"Tutorial {tidbit.Id} has no registered Herald recording.");
                    Require(tutorialAddresses.Add(tidbit.VoiceAddress),
                        $"Tutorial {tidbit.Id} shares another lesson's voice recording.");
                }
            Debug.Log($"[HeraldVoiceVerify] PASS: {recordings} addressable recordings; " +
                $"{tutorialAddresses.Count} tutorials with distinct playable narration.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
