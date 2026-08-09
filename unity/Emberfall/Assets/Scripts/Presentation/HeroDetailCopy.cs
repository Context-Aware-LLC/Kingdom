using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Presentation-layer traits copy for the character-creation detail overlay (NOR-281).
    /// Loaded once from Resources on first access; each class has three trait bullets.
    /// </summary>
    internal static class HeroDetailCopy
    {
        private static Dictionary<CardClass, string[]> _traits;

        internal static string[] TraitsFor(CardClass heroClass)
        {
            EnsureLoaded();
            return _traits.TryGetValue(heroClass, out string[] t) ? t : Array.Empty<string>();
        }

        private static void EnsureLoaded()
        {
            if (_traits != null) return;
            _traits = new Dictionary<CardClass, string[]>();

            var asset = Resources.Load<TextAsset>("hero-detail");
            if (asset == null)
            {
                Debug.LogWarning("[HeroDetailCopy] hero-detail.json not found in Resources — trait bullets will be empty.");
                return;
            }

            var payload = JsonUtility.FromJson<Payload>(asset.text);
            if (payload?.entries == null) return;

            foreach (Entry e in payload.entries)
            {
                if (Enum.TryParse(e.heroClass, out CardClass c))
                    _traits[c] = e.traits ?? Array.Empty<string>();
            }
        }

        [Serializable]
        private class Payload
        {
            public Entry[] entries;
        }

        [Serializable]
        private class Entry
        {
            public string heroClass;
            public string[] traits;
        }
    }
}
