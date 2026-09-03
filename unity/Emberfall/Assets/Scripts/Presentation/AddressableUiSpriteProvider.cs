using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace Emberfall.Presentation
{
    // Shipping IUiSpriteProvider (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/pipeline.md §2): resolves frame/gem/icon keys from the
    // UI_Core Addressables group at address "ui/<key>". UI chrome is small and always
    // resident, and IUiSpriteProvider.Get is a synchronous contract, so resolution uses
    // WaitForCompletion — a boot-time cost, not a per-frame one, because results
    // (including misses) are cached exactly like EditorUiSpriteProvider.
    //
    // WHY IT IS PREWARMED, AND WHY THE CACHE IS STATIC (#230, 2026-08-21).
    // "Boot-time cost" was the intent but not the behaviour: the cost was paid on the FIRST
    // request per key, whenever that landed. On the collection wall that is inside an
    // Addressables completion callback — CardFaceBaker.OnArtReady pumps its queue, the next
    // card binds, CardPresenter.ApplyFrame asks for a gem or keyword icon nothing has asked
    // for yet — and WaitForCompletion inside a ResourceManager callback re-enters
    // ResourceManager.Update, which Addressables refuses with
    // "Reentering the Update method is not allowed". So the key set matters: a card wall only
    // trips it on the first card carrying a rarity or keyword no earlier card had.
    //
    // Prewarm moves the cost back to where the comment always said it was, and it enumerates
    // the CATALOG rather than a hand-written key list, so a new frame/gem/icon needs no edit
    // here and cannot re-arm the trap by being forgotten. The cache is static because the
    // instance that mattered was the private one CardFaceBaker's own BoardController builds —
    // a warm provider elsewhere in the scene did nothing for it.
    //
    // The handles are deliberately never released: UI_Core is always-resident by design (that
    // is what makes a synchronous Get legal at all), so a cached Sprite cannot be destroyed
    // underneath the cache.
    public sealed class AddressableUiSpriteProvider : IUiSpriteProvider
    {
        private const string AddressPrefix = "ui/";

        private static readonly Dictionary<string, Sprite> _cache = new();

        /// <summary>Keys already reported as a cached miss, so the warning is once per key rather
        /// than once per card on a wall of ninety.</summary>
        private static readonly HashSet<string> _reportedMisses = new();

        private static bool _prewarmed;

        public AddressableUiSpriteProvider() => Prewarm();

        public Sprite Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_cache.TryGetValue(key, out Sprite cached))
            {
                // A CACHED NULL IS A FAILURE THAT ALREADY HAPPENED, AND IT USED TO RETURN IN
                // SILENCE. Prewarm caches its misses (deliberately — see below), so a sprite that
                // failed to load at boot answers every later Get with null and no word said. The
                // consumer then fails silently too: CardPresenter.PlaceSocket hides the renderer,
                // the gem simply does not draw, and the card front comes back missing a piece with
                // nothing anywhere naming it. That shape was written up in #295 and predicted the
                // gem would go the same way; it did, as #341.
                //
                // Once per key, so a wall of ninety cards asking for gem_common ninety times says
                // it once.
                if (cached == null && _reportedMisses.Add(key))
                    Debug.LogWarning($"[AddressableUiSpriteProvider] '{AddressPrefix}{key}' is cached " +
                                     "as MISSING and every request for it will return null. Whatever " +
                                     "draws it will be absent from the card front with no further " +
                                     "error. Check the prewarm line above for the original failure.");
                return cached;
            }

            // Reached only for a key the catalog sweep did not cover — a miss, or a prewarm that
            // failed. Still synchronous, and still the reentrancy hazard above if it happens to
            // land inside a callback; CardFaceBaker's per-card guard is what bounds that.
            var handle = Addressables.LoadAssetAsync<Sprite>($"{AddressPrefix}{key}");
            Sprite result = handle.WaitForCompletion();
            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                Debug.LogWarning($"[AddressableUiSpriteProvider] no UI sprite at key '{AddressPrefix}{key}'");
                result = null;
            }

            _cache[key] = result; // cache misses too — a missing placeholder shouldn't re-hit Addressables.
            return result;
        }

        /// <summary>
        /// Loads every Sprite the catalog publishes under <c>ui/</c> into the cache, so no later
        /// <see cref="Get"/> has to block. Idempotent, and safe to call from anywhere it is legal
        /// to block: a failure here is not fatal, it just leaves <see cref="Get"/> on its old
        /// lazy path.
        /// </summary>
        public static void Prewarm()
        {
            if (_prewarmed) return;
            _prewarmed = true;

            int loaded = 0;
            var failed = new List<string>();
            try
            {
                // ResourceLocators is empty until the catalog is up, and Get's callers cannot be
                // relied on to have touched Addressables first.
                Addressables.InitializeAsync().WaitForCompletion();

                foreach (IResourceLocator locator in Addressables.ResourceLocators)
                {
                    IEnumerable<object> keys;
                    try { keys = locator.Keys; }
                    catch (NotSupportedException) { continue; }   // dynamic locators don't enumerate
                    if (keys == null) continue;

                    foreach (object raw in keys)
                    {
                        // Keys carry GUIDs and labels alongside addresses; the prefix is the filter.
                        if (raw is not string address) continue;
                        if (!address.StartsWith(AddressPrefix, StringComparison.Ordinal)) continue;
                        // UI_Core holds TextAssets beside the chrome (IUiTextProvider's payloads).
                        if (!locator.Locate(address, typeof(Sprite), out IList<IResourceLocation> at)
                            || at == null || at.Count == 0) continue;

                        string key = address.Substring(AddressPrefix.Length);
                        if (_cache.ContainsKey(key)) continue;

                        var handle = Addressables.LoadAssetAsync<Sprite>(address);
                        Sprite sprite = handle.WaitForCompletion();
                        bool ok = handle.Status == AsyncOperationStatus.Succeeded;
                        _cache[key] = ok ? sprite : null;
                        if (ok) loaded++; else failed.Add(key);
                    }
                }
            }
            catch (Exception ex)
            {
                // Most likely cause is being called from inside a ResourceManager callback, which
                // is exactly the situation prewarming exists to prevent. Say so rather than dying.
                Debug.LogWarning($"[AddressableUiSpriteProvider] prewarm did not complete ({ex.GetType().Name}: " +
                                 $"{ex.Message}) — {loaded} sprites cached; Get() falls back to loading on demand.");
                return;
            }

            Debug.Log($"[AddressableUiSpriteProvider] prewarmed {loaded} UI sprite(s)" +
                      (failed.Count > 0 ? $", {failed.Count} failed to load" : ""));

            // NAME THEM. A count is not a diagnosis: "4 failed to load" was true on every device
            // run and told nobody WHICH four, while each one is a piece of chrome that will be
            // silently absent from a screen. The keys are the whole value of this line, and they
            // are sorted so two runs can be diffed.
            if (failed.Count > 0)
            {
                failed.Sort(StringComparer.Ordinal);
                Debug.LogWarning("[AddressableUiSpriteProvider] these UI sprites are MISSING and " +
                                 "whatever draws them will not appear: " +
                                 string.Join(", ", failed));
            }
        }

        // Domain reload is what normally clears a static; with "Enter Play Mode Options" set to
        // skip it, a second play session would otherwise inherit sprites from the first.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _cache.Clear();
            _reportedMisses.Clear();
            _prewarmed = false;
        }
    }
}
