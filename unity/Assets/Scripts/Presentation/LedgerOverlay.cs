using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Kingdom.Net.Client.Achievements;
using Kingdom.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The Ledger of Bad Ideas (whimsy pass 2026-10-17, #2): every achievement, earned ones dated and
    /// sealed, the rest UNDER REVIEW until someone stumbles into them. Opened from the book beside
    /// the Card Vault on the main menu (Sam, 2026-10-21); the game's one collection that cannot be bought.
    ///
    /// <para><b>The server's list is the record</b> (<c>GET /achievements</c>), read each time the
    /// Ledger opens, so an entry earned in the match just played is there without a sync. A failed
    /// read says so and offers nothing else: an empty Ledger and an unread one must not look alike.</para>
    ///
    /// <para>Built as its own UIDocument through Resources, the pattern
    /// <see cref="ConnectionFailedOverlay"/> uses, on the match panel settings, whose sort order puts
    /// it above the screen that opened it.</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class LedgerOverlay : MonoBehaviour
    {
        /// <summary>Two to a row, three rows to a page; ledger.uss sizes the page for exactly this.</summary>
        public const int EntriesPerPage = 6;

        private const int TimeoutSeconds = 10;
        private const float SealSize = 72f;

        private VisualElement _page;
        private Label _status;
        private Label _count;
        private Label _pageLabel;
        private Button _prev;
        private Button _next;
        private VisualElement _pager;
        private IUiSpriteProvider _sprites;
        private IReadOnlyList<LedgerEntry> _entries = Array.Empty<LedgerEntry>();
        private int _pageIndex;

        /// <summary>Open the Ledger over whatever screen is up. Returns the overlay's GameObject.</summary>
        public static GameObject Open()
        {
            var go = new GameObject("LedgerUI");
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("LedgerScreen");
            go.AddComponent<LedgerOverlay>().Init();
            return go;
        }

        private void Init()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[Ledger] No rootVisualElement: is LedgerScreen.uxml in a Resources folder?");
                Destroy(gameObject);
                return;
            }
            UiLocalizer.Attach(root);
#if UNITY_EDITOR
            _sprites = new EditorUiSpriteProvider();
#else
            _sprites = new AddressableUiSpriteProvider();
#endif
            _page = root.Q<VisualElement>("ledger-page");
            _status = root.Q<Label>("ledger-status");
            _count = root.Q<Label>("ledger-count");
            _pageLabel = root.Q<Label>("ledger-page-label");
            _pager = root.Q<VisualElement>("ledger-pager");
            _prev = root.Q<Button>("ledger-prev");
            _next = root.Q<Button>("ledger-next");

            if (_prev != null) _prev.clicked += () => Turn(-1);
            if (_next != null) _next.clicked += () => Turn(+1);
            Button close = root.Q<Button>("ledger-close");
            if (close != null) close.clicked += () => Destroy(gameObject);

            ShowStatus(Loc.Get("ledger_ledger_status"));
            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            AchievementSnapshot snapshot;
            try
            {
                if (!AuthGateway.IsSignedIn)
                    snapshot = AchievementSnapshot.Failed(AchievementFetchStatus.Unauthenticated, "Not signed in.");
                else
                {
                    string token = await AuthGateway.TokenForConnectAsync();
                    snapshot = await new HttpAchievementGateway(
                        CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(TimeoutSeconds)).FetchAsync(token);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Ledger] read threw ({ex.GetType().Name}: {ex.Message}).");
                snapshot = AchievementSnapshot.Failed(AchievementFetchStatus.Offline, ex.Message);
            }

            if (this == null) return;   // closed while the read was in flight

            switch (snapshot.Status)
            {
                case AchievementFetchStatus.Known:
                    _entries = Ledger.Of(snapshot.Achievements);
                    _pageIndex = 0;
                    ShowPage();
                    break;
                case AchievementFetchStatus.Unauthenticated:
                    ShowStatus(Loc.Get("ledger_status_signed_out"));
                    break;
                default:
                    Debug.LogWarning($"[Ledger] not read ({snapshot.Status}: {snapshot.Detail}).");
                    ShowStatus(Loc.Get("ledger_status_fetch_failed"));
                    break;
            }
        }

        private void ShowStatus(string text)
        {
            if (_status != null)
            {
                _status.text = text;
                _status.style.display = DisplayStyle.Flex;
            }
            if (_page != null) _page.style.display = DisplayStyle.None;
            if (_pager != null) _pager.style.visibility = Visibility.Hidden;
            if (_count != null) _count.text = " ";
        }

        private void Turn(int by)
        {
            int pages = PageCount;
            if (pages == 0) return;
            _pageIndex = Mathf.Clamp(_pageIndex + by, 0, pages - 1);
            ShowPage();
        }

        private int PageCount => (_entries.Count + EntriesPerPage - 1) / EntriesPerPage;

        private void ShowPage()
        {
            if (_page == null) return;
            if (_status != null) _status.style.display = DisplayStyle.None;
            _page.style.display = DisplayStyle.Flex;
            _page.Clear();

            int earned = 0;
            foreach (LedgerEntry entry in _entries) if (entry.Earned) earned++;
            if (_count != null) _count.text = Loc.Format("ledger_count_entered", earned, _entries.Count);

            int pages = PageCount;
            if (_pager != null) _pager.style.visibility = pages > 1 ? Visibility.Visible : Visibility.Hidden;
            if (_pageLabel != null) _pageLabel.text = Loc.Format("ledger_page_page_1_d_of", _pageIndex + 1, Mathf.Max(pages, 1));
            _prev?.SetEnabled(_pageIndex > 0);
            _next?.SetEnabled(_pageIndex < pages - 1);

            Sprite seal = _sprites?.Get(SealStamp.SpriteKey);
            int first = _pageIndex * EntriesPerPage;
            for (int i = first; i < Mathf.Min(first + EntriesPerPage, _entries.Count); i++)
                _page.Add(Entry(_entries[i], seal));
        }

        private static VisualElement Entry(LedgerEntry entry, Sprite seal)
        {
            var box = new VisualElement();
            box.AddToClassList("ledger-entry");
            if (!entry.Earned)
            {
                box.AddToClassList("ledger-entry--unearned");
                box.Add(Line(Loc.Get("ledger_entry_under_review"), "ledger-entry__name"));
                return box;
            }

            box.Add(Line(entry.Text.Name, "ledger-entry__name"));
            box.Add(Line(entry.Text.Plain, "ledger-entry__plain"));
            box.Add(Line(entry.Text.Citation, "ledger-entry__citation"));
            if (entry.EarnedAt is DateTimeOffset at)
                box.Add(Line(Loc.Format("ledger_entry_date", at.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture)),
                    "ledger-entry__date"));
            // The wax a past stamp left: a fixed tilt per entry, so turning back to a page does not
            // reshuffle every seal on it.
            if (seal != null)
                SealStamp.Place(box, seal, SealSize, right: 8f, top: 4f,
                    tiltDegrees: Tilt(entry.Text.Id));
            return box;
        }

        /// <summary>-8 to +7 degrees, the same every time for the same id.</summary>
        private static float Tilt(string id)
        {
            int sum = 0;
            foreach (char c in id) sum += c;
            return (sum & 15) - 8;
        }

        private static Label Line(string text, string className)
        {
            var label = new Label(text);
            label.AddToClassList(className);
            return label;
        }
    }
}
