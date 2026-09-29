using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.SceneManagement;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Background music, in POOLS chosen by screen. Each pool is one subfolder of
    /// <c>Assets/Sound/Music/</c>, registered by <c>AddressablesSetup.Run()</c> under the label
    /// <c>music-&lt;folder&gt;</c>; the pool plays shuffled, forever. Sam, 2026-09-28: one menu
    /// track, three battle tracks, and a vault track to follow. A screen whose pool is missing or
    /// empty plays the menu pool, so a pool can be added by dropping a folder in, before or after
    /// the code that asks for it.
    ///
    /// <para>Self-bootstraps once per process via <see cref="RuntimeInitializeOnLoadMethodAttribute"/>
    /// rather than being wired into a specific scene's bootstrap, unlike CardSoundDirector/
    /// HeroVoiceDirector: music has no match-specific state to bind to, and <c>DontDestroyOnLoad</c>
    /// lets the menu pool keep playing across menu-to-menu transitions instead of restarting.</para>
    /// </summary>
    public sealed class MusicDirector : MonoBehaviour
    {
        private const string MenuPool = "menu";
        private const string BattlePool = "battle";
        private const string VaultPool = "vault";
        private const float FadeOutSeconds = 0.8f;

        private sealed class Pool
        {
            public readonly List<AudioClip> Clips = new();
            public readonly List<int> Order = new();
            public int Cursor;
        }

        private readonly Dictionary<string, Pool> _pools = new();
        private readonly List<AsyncOperationHandle> _handles = new();
        private AudioSource _source;
        private string _playing;
        private bool _suspended;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // HeadlessVerify and CI batchmode runs never need music, and batchmode Addressables/
            // audio timing is untested territory this director has no reason to enter.
            if (Application.isBatchMode) return;

            var go = new GameObject("~MusicDirector");
            // HideInHierarchy, NOT HideAndDontSave: the DontSave half exempts an object from the
            // teardown that ends a play session, so an Editor Stop leaves this player alive and
            // audible with nothing in the hierarchy to reach it by. DontDestroyOnLoad alone is what
            // survives a scene load, which is the only survival this needs.
            go.hideFlags = HideFlags.HideInHierarchy;
            DontDestroyOnLoad(go);
            go.AddComponent<MusicDirector>();
        }

        private void Awake()
        {
            // TryGetComponent, not `??` — see CardSoundDirector.Init for why the null-coalescing
            // operator is the wrong null check against a UnityEngine.Object.
            if (!TryGetComponent(out AudioSource source)) source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.outputAudioMixerGroup = AudioSettings.MusicGroup;
            _source = source;

            LoadPool(MenuPool);
            LoadPool(BattlePool);
            LoadPool(VaultPool);
        }

        /// <summary>Locations first, then assets: <c>LoadAssetsAsync</c> on a label nothing carries
        /// fails with an InvalidKeyException in the log, and a pool with no folder yet (the vault,
        /// as of this writing) is a normal state here, not an error.</summary>
        private void LoadPool(string name)
        {
            var locate = Addressables.LoadResourceLocationsAsync("music-" + name, typeof(AudioClip));
            _handles.Add(locate);
            locate.Completed += located =>
            {
                if (located.Status != AsyncOperationStatus.Succeeded || located.Result.Count == 0) return;
                var load = Addressables.LoadAssetsAsync<AudioClip>((IList<IResourceLocation>)located.Result, null);
                _handles.Add(load);
                load.Completed += loaded =>
                {
                    if (loaded.Status != AsyncOperationStatus.Succeeded || loaded.Result == null) return;
                    var pool = new Pool();
                    pool.Clips.AddRange(loaded.Result);
                    Reshuffle(pool);
                    _pools[name] = pool;
                };
            };
        }

        private void Update()
        {
            if (_source == null || _suspended || OpeningCinematic.IsActive) return;

            string want = PoolForScene(SceneManager.GetActiveScene().name);
            if (!_pools.ContainsKey(want)) want = MenuPool;
            if (!_pools.ContainsKey(want)) return;

            // Leaving a pool mid-track fades rather than cuts. The fade runs on the source, not the
            // mixer, so the player's Music volume and Mute are untouched by it.
            if (_source.isPlaying && want != _playing)
            {
                _source.volume -= Time.unscaledDeltaTime / FadeOutSeconds;
                if (_source.volume > 0f) return;
                _source.Stop();
            }
            if (!_source.isPlaying) PlayNext(want);
        }

        // Scene names, not paths: SceneManager reports the name, and EmberfallBuild's list holds
        // the paths these came from. Anything unlisted is a menu screen.
        private static string PoolForScene(string scene) => scene switch
        {
            "BoardNetworked" => BattlePool,
            "PackVault" => VaultPool,
            _ => MenuPool,
        };

        /// <summary>Go quiet with the app instead of playing on over it.
        ///
        /// <para><c>runInBackground</c> is 1 in ProjectSettings and stays 1 — a match holds a live
        /// socket and has to survive the player glancing at a notification. The cost is that
        /// nothing else stops this player when Emberfall leaves the foreground: Unity keeps
        /// Update()-ing, the AudioSource keeps its clip, and Android keeps the process cached long
        /// past the point the player believes they closed the game.</para>
        ///
        /// <para>The <see cref="_suspended"/> gate is the load-bearing half, and it is not just an
        /// optimisation. <see cref="Update"/> restarts playback the instant <c>isPlaying</c> goes
        /// false, and losing audio focus to another app is exactly that — so ungated, the next
        /// frame calls Play() and takes the focus straight back, cutting off whatever the player
        /// just started. It presents as the game refusing to yield audio, which is precisely what
        /// an unguarded restart loop is.</para></summary>
        private void OnApplicationPause(bool paused)
        {
            if (_source == null) return;
            _suspended = paused;
            if (paused) _source.Pause();
            else _source.UnPause();
        }

        private void OnApplicationFocus(bool hasFocus) => OnApplicationPause(!hasFocus);

        private void PlayNext(string name)
        {
            Pool pool = _pools[name];
            if (pool.Cursor >= pool.Order.Count) Reshuffle(pool);
            _source.clip = pool.Clips[pool.Order[pool.Cursor]];
            _source.volume = 1f;
            _source.Play();
            pool.Cursor++;
            _playing = name;
        }

        // Fisher-Yates, re-run whenever the cursor exhausts the current pass — the loop never runs
        // dry and never repeats the same fixed order twice in a row.
        private static void Reshuffle(Pool pool)
        {
            pool.Order.Clear();
            for (int i = 0; i < pool.Clips.Count; i++) pool.Order.Add(i);
            for (int i = pool.Order.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (pool.Order[i], pool.Order[j]) = (pool.Order[j], pool.Order[i]);
            }
            pool.Cursor = 0;
        }

        private void OnDestroy()
        {
            // Reverse order: each asset load depends on the locations handle that produced it.
            for (int i = _handles.Count - 1; i >= 0; i--)
                if (_handles[i].IsValid()) Addressables.Release(_handles[i]);
        }
    }
}
