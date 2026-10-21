using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client.Shop;
using Kingdom.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Editor
{
    /// <summary>Opt-in runtime review of the shipping pack panel with local fixture receipts.
    /// Exercises real clicks, schedules, face baking and rendering without spending any packs.
    /// Run in the shared Editor with -batchmode (graphics enabled), -executeMethod
    /// Kingdom.Editor.PackOpeningPreview.Run. Exits after writing captures and assertions.</summary>
    public static class PackOpeningPreview
    {
        private const string Running = "Kingdom.PackOpeningPreview.Running";
        private static PackRevealPanel _reveal;
        private static VisualElement _root;
        private static RenderTexture _target;
        private static PanelSettings _settings;
        private static PackOpenReceipt _receipt;
        private static VisualElement _pack;
        private static Sprite _wrapper;
        private static float _next;
        private static int _step, _dismissed;
        private static string _output;

        [InitializeOnLoadMethod]
        private static void Resume()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Running, false))
                    EditorApplication.delayCall += Setup;
            };
        }

        public static void Run()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Use the dedicated batch review so an open scene is never replaced.");
            SessionState.SetBool(Running, true);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorApplication.EnterPlaymode();
        }

        private static void Setup()
        {
            try
            {
                string reviews = Path.GetFullPath(Path.Combine(Application.dataPath,
                    "../../art-pipeline/pack-opening"));
                int version = 1;
                do { _output = Path.Combine(reviews, "runtime-review-v" + version++); }
                while (Directory.Exists(_output));
                Directory.CreateDirectory(_output);
                _target = new RenderTexture(1920, 1080, 24);
                _target.Create();
                _settings = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<PanelSettings>(
                    "Assets/UI/Common/KingdomPanelSettings.asset"));
                _settings.targetTexture = _target;
                _settings.clearColor = true;
                var host = new GameObject("PackOpeningPreview");
                host.AddComponent<AudioListener>();
                var document = host.AddComponent<UIDocument>();
                document.panelSettings = _settings;
                document.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                    "Assets/UI/PackVault/PackVaultScreen.uxml");
                _root = document.rootVisualElement;
                var database = CardDatabaseLoader.Load();
                var baker = host.AddComponent<CardFaceBaker>();
                baker.Initialize(database);
                var rarities = new[] { Rarity.COMMON, Rarity.RARE, Rarity.EPIC, Rarity.COMMON, Rarity.LEGENDARY };
                var awards = new List<PackCardAward>();
                foreach (Rarity rarity in rarities)
                {
                    var definition = database.AllCollectible().First(c => c.Rarity == rarity &&
                        awards.All(a => a.CardId != c.Id));
                    awards.Add(new PackCardAward(definition.Id, rarity, PackUpgradeReason.None));
                }
                _receipt = new PackOpenReceipt("pack.pendingham", "local-preview-only", awards);
                _reveal = new PackRevealPanel(_root, () => _dismissed++, baker);
                _pack = new VisualElement();
                _pack.AddToClassList("vault-pack__face");
                _pack.AddToClassList("vault-stage__pack");
                _root.Q("vault-stage").Add(_pack);
                _wrapper = AssetDatabase.LoadAssetAtPath<Sprite>(
                    "Assets/Art/Shop/Packs/PENDINGHAM/pack_pd_crown_purple.png");
                PackWrapperBaker.ApplyTo(_pack, _wrapper);
                _step = -2;
                _next = Time.realtimeSinceStartup + 1.4f;
                EditorApplication.update += Tick;
            }
            catch (Exception ex) { Finish(ex); }
        }

        private static List<VisualElement> Cards() =>
            _root.Query<VisualElement>(className: "reveal-card").ToList();

        private static bool Revealed(VisualElement card) => card.ClassListContains("reveal-card--revealed");

        private static void Click(VisualElement target)
        {
            using (var click = ClickEvent.GetPooled())
            {
                click.target = target;
                target.SendEvent(click);
            }
        }

        private static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            Debug.Log("[PackOpeningPreview] PASS " + label);
        }

        private static void Tick()
        {
            if (Time.realtimeSinceStartup < _next) return;
            try
            {
                List<VisualElement> cards = Cards();
                switch (_step++)
                {
                    case -2:
                        Capture("00a-wrapper");
                        new PackTearAnimator().Play(_receipt, _pack, _wrapper, () => _reveal.Show(_receipt));
                        _next = Time.realtimeSinceStartup + 0.32f;
                        break;
                    case -1:
                        Check(_pack.Query<VisualElement>(className: "tear-half--parted").ToList().Count == 2,
                            "authored wrapper tears in place into two moving pieces");
                        Capture("00b-wrapper-tear");
                        _next = Time.realtimeSinceStartup + 2f;
                        break;
                    case 0:
                        Check(cards.Count == 5 && cards.All(c => c.ClassListContains("reveal-card--shown")),
                            "five card backs arrive");
                        Check(cards.All(c => !Revealed(c)), "single pack waits for player input");
                        Check(cards.All(c => c.worldBound.height > 100 && c.worldBound.width > 100),
                            "cards have a readable layout");
                        Capture("01-face-down");
                        Click(cards[4]);
                        Click(cards[4]);
                        _next = Time.realtimeSinceStartup + 0.65f;
                        break;
                    case 1:
                        Check(Revealed(cards[4]) && cards.Count(Revealed) == 1,
                            "chosen Legendary flips once despite a double tap");
                        Check(_root.Q("reveal-overlay").ClassListContains("reveal-overlay--spotlight"),
                            "Legendary owns the spotlight at reveal");
                        Capture("02-legendary-spotlight");
                        _next = Time.realtimeSinceStartup + 1f;
                        break;
                    case 2:
                        Check(cards.All(c => c.Q(className: "reveal-card__face").resolvedStyle.backgroundImage.texture != null),
                            "all five canonical printed faces baked");
                        Click(cards[2]);
                        Click(cards[0]);
                        Click(cards[2]);
                        _next = Time.realtimeSinceStartup + 0.48f;
                        break;
                    case 3:
                        Check(Revealed(cards[2]) && !Revealed(cards[0]), "rapid taps preserve chosen order");
                        Capture("03-epic-reveal");
                        Click(_root.Q("reveal-dismiss"));
                        _next = Time.realtimeSinceStartup + 2.3f;
                        break;
                    case 4:
                        Check(cards.All(Revealed) && _dismissed == 0,
                            "background tap reveals remaining awards without dismissing");
                        Check(cards.All(c => !c.ClassListContains("reveal-card--queued")),
                            "repeat taps leave no queued visual state behind");
                        Capture("04-all-revealed");
                        Click(_root.Q("reveal-dismiss"));
                        Check(_dismissed == 1, "completed reveal dismisses on background tap");
                        _reveal.Show(_receipt);
                        Click(Cards()[4]);
                        _reveal.Hide();
                        _reveal.Show(_receipt);
                        _next = Time.realtimeSinceStartup + 1.8f;
                        break;
                    case 5:
                        Check(cards.All(c => !Revealed(c)), "replacement cancels old reveal callbacks");
                        Check(!_root.Q("reveal-overlay").ClassListContains("reveal-overlay--spotlight"),
                            "replacement clears the spotlight");
                        using (var key = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Space }))
                        {
                            key.target = cards[0];
                            cards[0].SendEvent(key);
                        }
                        _next = Time.realtimeSinceStartup + 0.8f;
                        break;
                    case 6:
                        Check(Revealed(cards[0]), "keyboard Space reveals the focused card");
                        _reveal.ShowMany(new[] { _receipt, _receipt, _receipt });
                        _next = Time.realtimeSinceStartup + 8f;
                        break;
                    case 7:
                        Check(cards.Count == 6 && cards.All(Revealed), "batch highlights reveal automatically");
                        Check(_root.Q("reveal-list").childCount == 4, "batch receipt accounts for all rarities");
                        Capture("05-batch-receipt");
                        _reveal.Hide();
                        _target.Release();
                        _target.width = 2400;
                        _target.height = 1080;
                        _target.Create();
                        _reveal.Show(_receipt);
                        _next = Time.realtimeSinceStartup + 1.2f;
                        break;
                    case 8:
                        Check(cards.All(c => c.worldBound.xMin >= 0 && c.worldBound.xMax <= 2400),
                            "wide phone keeps all cards inside the viewport");
                        Capture("06-wide-phone");
                        _reveal.Hide();
                        Check(_root.Q("reveal-overlay").ClassListContains("reveal-overlay--hidden"),
                            "hide clears the complete presentation");
                        Finish(null);
                        break;
                }
            }
            catch (Exception ex) { Finish(ex); }
        }

        private static void Capture(string name)
        {
            RenderTexture prior = RenderTexture.active;
            RenderTexture.active = _target;
            var image = new Texture2D(_target.width, _target.height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, _target.width, _target.height), 0, 0);
            image.Apply();
            File.WriteAllBytes(Path.Combine(_output, name + ".png"), image.EncodeToPNG());
            UnityEngine.Object.Destroy(image);
            RenderTexture.active = prior;
        }

        private static void Finish(Exception error)
        {
            EditorApplication.update -= Tick;
            SessionState.EraseBool(Running);
            _reveal?.Hide();
            if (error != null) Debug.LogError("[PackOpeningPreview] FAIL " + error);
            else Debug.Log("[PackOpeningPreview] ALL PASS; captures: " + _output);
            EditorApplication.Exit(error == null ? 0 : 1);
        }
    }
}
