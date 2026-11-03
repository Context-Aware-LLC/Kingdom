using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Kingdom.Net.Client.King;
using Kingdom.Net.Protocol;
using Kingdom.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The Succession Roll (King of the Week, #421): this week's King and his commission, then every
    /// King since the first, newest first, each with his fate and the player who was his Favorite.
    /// Opened from the crown in the main menu dock, which shows nothing else.
    ///
    /// <para><b>The server's Roll is the record</b> (<c>GET /king/roll</c>), read each time it opens.
    /// A failed read says so: an answered Roll always holds the reign on the throne, so an empty one
    /// can only be an unread one.</para>
    ///
    /// <para>Built as its own UIDocument through Resources, the way <see cref="LedgerOverlay"/> is,
    /// and read on the herald's scroll the way <see cref="MatchProclamation"/> is.</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class SuccessionRollOverlay : MonoBehaviour
    {
        private const int TimeoutSeconds = 10;

        /// <summary>The overlay's GameObject name. The menu's arrival sequence finds an open one by it.</summary>
        public const string ObjectName = "SuccessionRollUI";

        private Label _status;
        private ScrollView _list;
        private ProclamationScroll _scroll;
        private bool _closing;

        /// <summary>Open the Roll over whatever screen is up. Returns the overlay's GameObject.</summary>
        /// <param name="king">The King on the throne, whose epithet and commission head the Roll; null
        /// leaves that block hidden. The menu passes the one it already read, so opening costs no
        /// second request.</param>
        public static GameObject Open(KingNow king = null)
        {
            var go = new GameObject(ObjectName);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("SuccessionRoll");
            go.AddComponent<SuccessionRollOverlay>().Init(king);
            return go;
        }

        private void Init(KingNow king)
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[SuccessionRoll] No rootVisualElement: is SuccessionRoll.uxml in a Resources folder?");
                Destroy(gameObject);
                return;
            }
            UiLocalizer.Attach(root);

            _status = root.Q<Label>("roll-status");
            _list = root.Q<ScrollView>("roll-list");
            Button close = root.Q<Button>("roll-close");
            if (close != null) close.clicked += Close;

            ShowNow(root, king);

            VisualElement sheet = root.Q<VisualElement>("roll-sheet");
            if (sheet?.parent != null) _scroll = new ProclamationScroll(sheet);

            ShowStatus(Loc.Get("king_roll_status_loading"));
            _scroll?.Open();
            _ = LoadAsync();
        }

        /// <summary>This week: his epithet, his commission, how it is scored, his decree, and the
        /// image of the achievement it is named for. A commission this build has no words for is
        /// left out, never printed by id, and so is its image.</summary>
        private static void ShowNow(VisualElement root, KingNow king)
        {
            VisualElement now = root.Q<VisualElement>("roll-now");
            if (now == null || king == null) return;

            SetLine(root.Q<Label>("roll-now-epithet"), KingText.Epithet(king.Epithet));
            string name = KingText.Commission(king.Commission, "name");
            SetLine(root.Q<Label>("roll-now-commission"), name == null ? null : Loc.Format("king_menu_commission", name));
            SetLine(root.Q<Label>("roll-now-scored"), name == null ? null : KingText.Commission(king.Commission, "scored"));
            SetLine(root.Q<Label>("roll-now-decree"), name == null ? null : KingText.Commission(king.Commission, "decree"));

            VisualElement icon = root.Q<VisualElement>("roll-now-icon");
            if (icon != null)
            {
#if UNITY_EDITOR
                IUiSpriteProvider sprites = new EditorUiSpriteProvider();
#else
                IUiSpriteProvider sprites = new AddressableUiSpriteProvider();
#endif
                Sprite sprite = name == null ? null : sprites.Get(KingText.AchievementSpriteKey(king.Commission));
                if (sprite != null) icon.style.backgroundImage = new StyleBackground(sprite);
                else icon.style.display = DisplayStyle.None;
            }
            now.style.display = DisplayStyle.Flex;
        }

        private static void SetLine(Label label, string text)
        {
            if (label == null) return;
            label.text = string.IsNullOrEmpty(text) ? " " : text;
            label.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void Close()
        {
            // A tap while the scroll moves is a tap on a button half behind a roller.
            if (_closing || _scroll?.IsMoving == true) return;
            _closing = true;
            if (_scroll == null) { Destroy(gameObject); return; }
            _scroll.Close(() => { if (this != null) Destroy(gameObject); });
        }

        private async Task LoadAsync()
        {
            IReadOnlyList<RollEntry> roll;
            try
            {
                roll = await new HttpKingGateway(
                    CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(TimeoutSeconds)).FetchRollAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SuccessionRoll] read threw ({ex.GetType().Name}: {ex.Message}).");
                roll = Array.Empty<RollEntry>();
            }

            if (this == null) return;   // closed while the read was in flight

            if (roll.Count == 0)
            {
                ShowStatus(Loc.Get("king_roll_status_failed"));
                return;
            }
            ShowRoll(roll);
        }

        private void ShowStatus(string text)
        {
            if (_status != null)
            {
                _status.text = text;
                _status.style.display = DisplayStyle.Flex;
            }
            if (_list != null) _list.style.display = DisplayStyle.None;
        }

        private void ShowRoll(IReadOnlyList<RollEntry> roll)
        {
            if (_list == null) return;
            if (_status != null) _status.style.display = DisplayStyle.None;
            _list.style.display = DisplayStyle.Flex;
            _list.Clear();
            // The server lists the reign on the throne first; it has no fate yet and no Favorite.
            for (int i = 0; i < roll.Count; i++)
                _list.Add(Entry(roll[i], reigning: i == 0));
        }

        private static VisualElement Entry(RollEntry entry, bool reigning)
        {
            var box = new VisualElement();
            box.AddToClassList("roll-entry");

            var head = new VisualElement();
            head.AddToClassList("roll-entry__head");
            head.Add(Line(Loc.Format("king_roll_reign", entry.Reign), "roll-entry__reign"));
            string epithet = KingText.Epithet(entry.Epithet);
            head.Add(Line(epithet, "roll-entry__epithet"));
            box.Add(head);

            box.Add(Line(reigning ? Loc.Get("king_roll_reigning") : KingText.Fate(entry.Fate), "roll-entry__fate"));

            var favor = new VisualElement();
            favor.AddToClassList("roll-entry__favor");
            // Null is no Favorite; "" is a Favorite with no name to show (KingText.SubjectName).
            if (entry.Favorite == null)
            {
                // Two literal calls, not one with a ternary key, so LocalizationVerify sees both.
                Label none = Line(reigning ? Loc.Get("king_roll_undecided") : Loc.Get("king_roll_no_favorite"),
                    "roll-entry__favor-of");
                none.AddToClassList("roll-entry__favor-of--none");
                favor.Add(none);
            }
            else
            {
                favor.Add(NameLabel(KingText.SubjectName(entry.Favorite)));
                favor.Add(Line(Loc.Format("king_roll_favorite_of", epithet), "roll-entry__favor-of"));
            }
            box.Add(favor);
            return box;
        }

        /// <summary>
        /// The one place a Favorite's name is drawn. It is PEER-AUTHORED, and this is the renderer's
        /// half of the obligation security.md sets for every such name, the same pair as
        /// <c>FriendsDrawerController.NameLabel</c>: <b>rich text off</b>, here, and <b>a clip in
        /// pixels</b>, in <c>.roll-entry__name</c> (king.uss).
        /// </summary>
        private static Label NameLabel(string name)
        {
            var label = new Label(name)
            {
                enableRichText = false,
                parseEscapeSequences = false,
            };
            label.AddToClassList("roll-entry__name");
            return label;
        }

        private static Label Line(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }
    }

    /// <summary>
    /// The King's words. The server sends pool indices and a commission id; every word is the
    /// client's, in <c>strings.xml</c>. <c>KingVerify</c> (Editor) proves each row exists,
    /// because these keys are built and <c>LocalizationVerify</c> sees only literal ones.
    ///
    /// <para><b>A raw key is the one thing these must never show.</b> A newer server can name an
    /// epithet, fate or commission this build has no row for, so each falls back to plain words, and
    /// an unknown commission reads as null for the caller to leave out.</para>
    /// </summary>
    public static class KingText
    {
        /// <summary>The <c>strings.xml</c> row for epithet pool index <paramref name="index"/> (0-based;
        /// the rows count from 1).</summary>
        public static string EpithetKey(int index) => "king_epithet_" + (index + 1).ToString(CultureInfo.InvariantCulture);

        /// <summary>The <c>strings.xml</c> row for fate pool index <paramref name="index"/>.</summary>
        public static string FateKey(int index) => "king_fate_" + (index + 1).ToString(CultureInfo.InvariantCulture);

        /// <summary>A commission's row: <paramref name="part"/> is <c>name</c>, <c>scored</c> or
        /// <c>decree</c>.</summary>
        public static string CommissionKey(string id, string part) => "commission_" + id + "_" + part;

        /// <summary>The parts every commission in <see cref="CommissionIds"/> carries.</summary>
        public static readonly string[] CommissionParts = { "name", "scored", "decree" };

        /// <summary>An achievement's image, which the commission card drawn from it shows too: the
        /// UI sprite key of a file of that name under <c>Assets/Art/UI/Achievements/</c>. The one
        /// place the name is built; the Ledger, the card and KingVerify all ask here.</summary>
        public static string AchievementSpriteKey(string id) => "achievement_" + id;

        /// <summary>His seal on a commission's Ledger page (spec, "Prizes"): the menu's crown, in
        /// UI_Core already. No seal art of his own exists; replacing the sprite under this key
        /// is all a new one needs.</summary>
        public const string SealSpriteKey = "ui_king_crown";

        public static string Epithet(int index) => Loc.Find(EpithetKey(index)) ?? Loc.Get("king_epithet_unknown");

        public static string Fate(int index) => Loc.Find(FateKey(index)) ?? Loc.Get("king_fate_unknown");

        /// <summary>The commission's <paramref name="part"/>, or null when this build has no row.</summary>
        public static string Commission(string id, string part)
            => string.IsNullOrEmpty(id) ? null : Loc.Find(CommissionKey(id, part));

        /// <summary>A name off the Favor board or the Roll. The server sends <c>""</c> for a player with
        /// no name to show (a blank, or a per-match seat handle: <c>FavorNames</c>), and the words
        /// for that are the client's.</summary>
        public static string SubjectName(string shown)
            => string.IsNullOrEmpty(shown) ? Loc.Get("king_favor_unnamed") : shown;
    }
}
