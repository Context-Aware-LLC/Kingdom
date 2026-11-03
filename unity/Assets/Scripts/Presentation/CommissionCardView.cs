using System.Globalization;
using Kingdom.Net.Client;
using Kingdom.Net.Protocol;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The King's commission card in the upper right of the board (King of the Week, #421): the
    /// commission's achievement image, its name on a ribbon, and either how it is scored or this
    /// seat's score so far.
    ///
    /// <para><b>The server decides everything shown.</b> A null <see cref="CommissionProgress"/> shows
    /// nothing, which is how a friend match and a seat that cannot bank hide the card. A null score
    /// shows the rules text. A score that needs a win is greyed with "Counts if you win". The client
    /// never computes or filters a score.</para>
    ///
    /// <para><b>Two forms, by screen aspect.</b> Wider than 2:1, the corner past the table is free
    /// and the full card sits there as furniture: it never takes a press. At 2:1 and narrower that
    /// corner holds the opponent's hero ring, so the card collapses to a chip of image and number
    /// in the band above the table. A tap opens the full card, and the next tap anywhere closes it;
    /// while it is open <see cref="IsOpen"/> gates board input, because UI Toolkit does not block
    /// Unity's physics input (the same reason as <see cref="MatchSettingsController"/>).</para>
    ///
    /// <para>Built as its own UIDocument through Resources on the settings panel, the pattern
    /// <see cref="MatchEndBanner"/> uses, and sorted under every other document on that panel.
    /// Its right inset is <c>.emb-edge</c>'s alone, clear of the punch-hole camera. Updates arrive on the main thread through NetworkedGameBootstrap's marshalling
    /// session, the same path as OnSnapshot.</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class CommissionCardView : MonoBehaviour
    {
        /// <summary>Negative because everything else on this panel in a match sorts at the default
        /// 0 and must cover the card: the settings overlay, and NetworkedGameBootstrap's two
        /// ConnectionFailedScreen documents. At 0 or above, the chip would stay tappable over their
        /// scrims. The match-end banner (100) covers it too.</summary>
        private const int SortingOrder = -1;

        /// <summary>Screen width over height at and below which the full card meets the opponent's
        /// hero ring and the chip stands in for it.</summary>
        private const float FullCardMinAspect = 2f;

        private VisualElement _root;
        private VisualElement _scrim;
        private VisualElement _chip;
        private VisualElement _chipImage;
        private Label _chipScore;
        private VisualElement _card;
        private VisualElement _image;
        private Label _ribbon;
        private Label _rules;
        private Label _score;
        private Label _sub;
        private IUiSpriteProvider _sprites;

        private bool _shown;
        private bool _wide;
        private bool _open;
        private bool _pointerOverChip;
        private int _screenWidth;
        private int _screenHeight;

        /// <summary>True while the chip's full card is open. Board input waits until it closes.</summary>
        public bool IsOpen => _shown && !_wide && _open;

        /// <summary>What InputRouter.UiBlocksInput reads: the card is open, or the pointer is on the
        /// chip, which has no physics collider (the settings gear's reason exactly).</summary>
        public bool IsBlockingInput => IsOpen || (_shown && !_wide && _pointerOverChip);

        /// <summary>Build the card and show what <paramref name="session"/> holds now. The match
        /// start's card raises no event, so this read is the only way it is seen; later cards arrive
        /// through <see cref="OnCommission"/>.</summary>
        public static CommissionCardView Mount(NetworkedMatchSession session)
        {
            var go = new GameObject("CommissionCardUI");
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("CommissionCard");
            doc.sortingOrder    = SortingOrder;
            var view = go.AddComponent<CommissionCardView>();
            view.Init();
            view.Show(session?.Commission);
            return view;
        }

        /// <summary>The marshalled <c>NetworkedMatchSession.OnCommission</c>: main thread only.</summary>
        public void OnCommission(CommissionProgress progress)
        {
            if (this == null) return;   // the scene went while the update was queued
            Show(progress);
        }

        private void Init()
        {
#if UNITY_EDITOR
            _sprites = new EditorUiSpriteProvider();
#else
            _sprites = new AddressableUiSpriteProvider();
#endif
            var root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[CommissionCard] No rootVisualElement: is CommissionCard.uxml in a Resources folder?");
                return;
            }

            _root      = root.Q<VisualElement>("cc-root");
            _scrim     = root.Q<VisualElement>("cc-scrim");
            _chip      = root.Q<VisualElement>("cc-chip");
            _chipImage = root.Q<VisualElement>("cc-chip-image");
            _chipScore = root.Q<Label>("cc-chip-score");
            _card      = root.Q<VisualElement>("cc-card");
            _image     = root.Q<VisualElement>("cc-image");
            _ribbon    = root.Q<Label>("cc-ribbon");
            _rules     = root.Q<Label>("cc-rules");
            _score     = root.Q<Label>("cc-score");
            _sub       = root.Q<Label>("cc-sub");

            if (_chip != null)
            {
                _chip.RegisterCallback<ClickEvent>(_ => SetOpen(!_open));
                _chip.RegisterCallback<PointerEnterEvent>(_ => _pointerOverChip = true);
                _chip.RegisterCallback<PointerLeaveEvent>(_ => _pointerOverChip = false);
            }
            // The full card is Ignore, so a tap on it lands here or on the chip: both close it.
            _scrim?.RegisterCallback<ClickEvent>(_ => SetOpen(false));

            _wide = IsWide(Screen.width, Screen.height);
            _screenWidth = Screen.width;
            _screenHeight = Screen.height;
        }

        private void Update()
        {
            // A desktop window resizes and a foldable unfolds; the form follows the screen.
            if (_screenWidth == Screen.width && _screenHeight == Screen.height) return;
            _screenWidth = Screen.width;
            _screenHeight = Screen.height;
            bool wide = IsWide(_screenWidth, _screenHeight);
            if (wide == _wide) return;
            _wide = wide;
            _open = false;
            Layout();
        }

        private static bool IsWide(int width, int height)
            => height > 0 && width > FullCardMinAspect * height;

        private void SetOpen(bool open)
        {
            _open = open;
            Layout();
        }

        /// <summary>Draw <paramref name="progress"/> exactly as sent.</summary>
        private void Show(CommissionProgress progress)
        {
            string id = progress?.Id;
            // A newer server can name a commission this build has no words for. Show nothing
            // rather than a raw key (KingText's contract).
            string name = KingText.Commission(id, "name");
            _shown = progress != null && name != null;
            if (!_shown)
            {
                _open = false;
                Layout();
                return;
            }

            Sprite sprite = _sprites?.Get(KingText.AchievementSpriteKey(id));
            SetImage(_image, sprite);
            SetImage(_chipImage, sprite);
            if (_ribbon != null) _ribbon.text = name;

            bool scored = progress.Score.HasValue;
            string number = scored ? progress.Score.Value.ToString(CultureInfo.InvariantCulture) : " ";
            if (_rules != null)
            {
                _rules.text = scored ? " " : (KingText.Commission(id, "scored") ?? " ");
                _rules.style.display = scored ? DisplayStyle.None : DisplayStyle.Flex;
            }
            if (_score != null)
            {
                _score.text = number;
                _score.style.display = scored ? DisplayStyle.Flex : DisplayStyle.None;
                _score.EnableInClassList("cc-score--if-win", progress.CountsIfYouWin);
            }
            if (_chipScore != null)
            {
                _chipScore.text = number;
                _chipScore.style.display = scored ? DisplayStyle.Flex : DisplayStyle.None;
                _chipScore.EnableInClassList("cc-score--if-win", progress.CountsIfYouWin);
            }
            if (_sub != null)
            {
                // A score that still needs a win says so; otherwise the best it has to beat.
                string sub = !scored ? null
                    : progress.CountsIfYouWin ? Loc.Get("king_counts_if_win")
                    : progress.Best.HasValue
                        ? Loc.Format("king_best", progress.Best.Value.ToString(CultureInfo.InvariantCulture))
                        : null;
                _sub.text = sub ?? " ";
                _sub.style.display = sub == null ? DisplayStyle.None : DisplayStyle.Flex;
            }
            Layout();
        }

        private static void SetImage(VisualElement target, Sprite sprite)
        {
            if (target == null) return;
            target.style.backgroundImage = sprite == null
                ? new StyleBackground(StyleKeyword.None)
                : new StyleBackground(sprite);
        }

        /// <summary>Which form shows, from what is shown and how wide the screen is.</summary>
        private void Layout()
        {
            if (_root == null) return;
            if (!_shown)
            {
                _root.style.display = DisplayStyle.None;
                return;
            }
            _root.style.display = DisplayStyle.Flex;

            bool cardShown = _wide || _open;
            if (_card != null)  _card.style.display  = cardShown ? DisplayStyle.Flex : DisplayStyle.None;
            if (_chip != null)  _chip.style.display  = _wide ? DisplayStyle.None : DisplayStyle.Flex;
            if (_scrim != null) _scrim.style.display = !_wide && _open ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void OnDisable()
        {
            _open = false;
            _pointerOverChip = false;
        }
    }
}
