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
    /// His Majesty's Commission (King of the Week, #421): this week's commission, with the image of
    /// the achievement it is named for, his epithet, how it is scored and his decree; and under it,
    /// last week's Favorite. Opened from the crown in the main menu dock, which shows nothing else,
    /// because beside the crown over the stage art the words could not be read (Sam, 2026-11-03).
    ///
    /// <para><b>Past winners are not here.</b> They are on a leaderboard of their own, the Succession
    /// Roll tab of the main menu's leaderboard modal (Sam, 2026-11-03). The sheet's other button
    /// closes it and opens that modal on this reign's Favor board.</para>
    ///
    /// <para>The commission comes from the King the menu already read, so it shows at once. Last
    /// week's Favorite is the second line of the server's Roll (<c>GET /king/roll</c>, newest
    /// first), read on opening; until it is read, if it cannot be, or in Reign 1, that block stays
    /// hidden without changing the sheet's height.</para>
    ///
    /// <para>Built as its own UIDocument through Resources, the way <see cref="LedgerOverlay"/> is,
    /// and read on the herald's scroll the way <see cref="MatchProclamation"/> is. The class keeps
    /// the feature's first name; the menu's arrival sequence finds the open overlay by
    /// <see cref="ObjectName"/>.</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class SuccessionRollOverlay : MonoBehaviour
    {
        private const int TimeoutSeconds = 10;

        /// <summary>The overlay's GameObject name. The menu's arrival sequence finds an open one by it.</summary>
        public const string ObjectName = "SuccessionRollUI";

        private VisualElement _last;
        private VisualElement _lastLine;
        private ProclamationScroll _scroll;
        private bool _closing;

        /// <summary>Open the sheet over whatever screen is up. Returns the overlay's GameObject.</summary>
        /// <param name="king">The King on the throne, whose commission the sheet shows. The menu
        /// passes the one it already read, so the commission costs no second request.</param>
        /// <param name="openFavor">Opens this reign's Favor board, called once the sheet has rolled
        /// up. Null, or no King, hides the button.</param>
        public static GameObject Open(KingNow king = null, Action openFavor = null)
        {
            var go = new GameObject(ObjectName);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("SuccessionRoll");
            go.AddComponent<SuccessionRollOverlay>().Init(king, openFavor);
            return go;
        }

        private void Init(KingNow king, Action openFavor)
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[SuccessionRoll] No rootVisualElement: is SuccessionRoll.uxml in a Resources folder?");
                Destroy(gameObject);
                return;
            }
            UiLocalizer.Attach(root);

            Button close = root.Q<Button>("roll-close");
            if (close != null) close.clicked += () => Close(null);
            Button favor = root.Q<Button>("roll-favor");
            if (favor != null)
            {
                // The board is this reign's, so with no King there is nothing for it to show.
                if (openFavor == null || king == null) favor.style.display = DisplayStyle.None;
                else favor.clicked += () => Close(openFavor);
            }

            ShowNow(root, king);
            _last = root.Q<VisualElement>("roll-last");
            _lastLine = root.Q<VisualElement>("roll-last-line");
            if (_last != null) _last.style.visibility = Visibility.Hidden;

            VisualElement sheet = root.Q<VisualElement>("roll-sheet");
            if (sheet?.parent != null) _scroll = new ProclamationScroll(sheet);
            _scroll?.Open();
            _ = LoadLastWeekAsync();
        }

        /// <summary>His epithet, his commission, how it is scored, his decree, and the image of the
        /// achievement it is named for. A commission this build has no words for is left out, never
        /// printed by id, and so is its image.</summary>
        private static void ShowNow(VisualElement root, KingNow king)
        {
            string name = king == null ? null : KingText.Commission(king.Commission, "name");
            SetLine(root.Q<Label>("roll-now-epithet"), king == null ? null : KingText.Epithet(king.Epithet));
            SetLine(root.Q<Label>("roll-now-commission"), name == null ? null : Loc.Format("king_menu_commission", name));
            SetLine(root.Q<Label>("roll-now-scored"), name == null ? null : KingText.Commission(king.Commission, "scored"));
            SetLine(root.Q<Label>("roll-now-decree"), name == null ? null : KingText.Commission(king.Commission, "decree"));

            VisualElement icon = root.Q<VisualElement>("roll-now-icon");
            if (icon == null) return;
#if UNITY_EDITOR
            IUiSpriteProvider sprites = new EditorUiSpriteProvider();
#else
            IUiSpriteProvider sprites = new AddressableUiSpriteProvider();
#endif
            Sprite sprite = name == null ? null : sprites.Get(KingText.AchievementSpriteKey(king.Commission));
            if (sprite != null) icon.style.backgroundImage = new StyleBackground(sprite);
            else icon.style.display = DisplayStyle.None;
        }

        private static void SetLine(Label label, string text)
        {
            if (label == null) return;
            label.text = string.IsNullOrEmpty(text) ? " " : text;
            label.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        /// <param name="then">Runs once the sheet is gone, so whatever it opens is not drawn under
        /// the scroll.</param>
        private void Close(Action then)
        {
            // A tap while the scroll moves is a tap on a button half behind a roller.
            if (_closing || _scroll?.IsMoving == true) return;
            _closing = true;
            if (_scroll == null) { Destroy(gameObject); then?.Invoke(); return; }
            _scroll.Close(() =>
            {
                if (this == null) return;
                Destroy(gameObject);
                then?.Invoke();
            });
        }

        private async Task LoadLastWeekAsync()
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
                return;
            }

            if (this == null || _last == null || _lastLine == null) return;   // closed while the read was in flight
            // Newest first: the reign on the throne, then last week's. Reign 1 has no last week.
            if (roll.Count < 2) return;
            ShowLastWeek(roll[1]);
        }

        private void ShowLastWeek(RollEntry entry)
        {
            _lastLine.Clear();
            // Null is no Favorite; "" is a Favorite with no name to show (KingText.SubjectName).
            if (entry.Favorite == null)
            {
                Label none = Line(Loc.Get("king_roll_no_favorite"), "roll-entry__favor-of");
                none.AddToClassList("roll-entry__favor-of--none");
                _lastLine.Add(none);
            }
            else
            {
                _lastLine.Add(NameLabel(KingText.SubjectName(entry.Favorite)));
                _lastLine.Add(Line(Loc.Format("king_roll_favorite_of", KingText.Epithet(entry.Epithet)), "roll-entry__favor-of"));
            }
            _last.style.visibility = Visibility.Visible;
        }

        /// <summary>
        /// The one place a Favorite's name is drawn here. It is PEER-AUTHORED, and this is the
        /// renderer's half of the obligation security.md sets for every such name, the same pair as
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
