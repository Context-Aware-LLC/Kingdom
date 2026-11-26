using System.Globalization;
using Kingdom.Core.Collection;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The in-app pack odds (EF-080f): what a retail pack can hold, opened from the pack detail
    /// panel, where the buy buttons are. Google Play requires the odds of a paid randomized item
    /// to be visible before purchase; this is that disclosure.
    ///
    /// <para><b>Every number is read off the constants the server opens packs with</b>:
    /// <see cref="BoosterPack.All"/> (the same list <c>ShopEndpoint</c> and <c>PackCatalog</c> sell
    /// from), <see cref="PackRarityOdds"/> and <see cref="PackPityState"/>. So this sheet cannot
    /// drift from the opener. The two prose copies can: <c>Kingdom.Tools.OddsDisclosure</c>
    /// regenerates <c>docs/dev-docs/data/pack-odds.md</c>, and the store-facing
    /// <c>docs/pub-docs/economy/booster-packs.md</c> is hand-edited from it. A guarantee added
    /// to <see cref="BoosterPackOpener"/> needs a line here as well as there.</para>
    ///
    /// <para><b>A pack id the build does not know shows no odds link at all</b>
    /// (<see cref="Find"/> returns null), rather than the default odds: a disclosure that guesses
    /// is worse than an absent one, and the server sells only what <see cref="BoosterPack.All"/>
    /// lists, so a miss means a newer server than this client.</para>
    /// </summary>
    public sealed class PackOddsPanel
    {
        private const string HiddenClass = "pack-odds-overlay--hidden";

        private readonly VisualElement _overlay;
        private readonly Label _title;
        private readonly VisualElement _body;

        public PackOddsPanel(VisualElement root)
        {
            _overlay = root?.Q<VisualElement>("pack-odds-overlay");
            _title = root?.Q<Label>("pack-odds-title");
            _body = root?.Q<VisualElement>("pack-odds-body");
            var close = root?.Q<Button>("pack-odds-close");
            if (close != null) close.clicked += Hide;
            // The scrim closes it; the sheet itself does not.
            _overlay?.RegisterCallback<ClickEvent>(evt => { if (evt.target == _overlay) Hide(); });
        }

        /// <summary>The retail pack sold under <paramref name="packId"/>, or null.</summary>
        public static BoosterPack Find(string packId)
        {
            if (string.IsNullOrEmpty(packId)) return null;
            foreach (BoosterPack pack in BoosterPack.All())
                if (pack.Id == packId) return pack;
            return null;
        }

        public void Show(string packId, string title)
        {
            BoosterPack pack = Find(packId);
            if (_overlay == null || _body == null || pack == null) return;

            if (_title != null) _title.text = title;
            _body.Clear();
            _body.Add(Line(Loc.Format("pack_odds_intro", pack.CardCount), "pack-odds-intro"));

            var columns = new VisualElement();
            columns.AddToClassList("pack-odds-columns");

            var table = new VisualElement();
            table.AddToClassList("pack-odds-table");
            AddRow(table, "rarity_common", pack.Odds.CommonPermille);
            AddRow(table, "rarity_rare", pack.Odds.RarePermille);
            AddRow(table, "rarity_epic", pack.Odds.EpicPermille);
            AddRow(table, "rarity_legendary", pack.Odds.LegendaryPermille);
            columns.Add(table);

            // Mirrors BoosterPackOpener.ApplyGuarantees and PackPityState's three constants.
            var rules = new VisualElement();
            rules.AddToClassList("pack-odds-rules");
            rules.Add(Line(Loc.Get("pack_odds_rules_lead"), "pack-odds-rules__lead"));
            rules.Add(Rule(Loc.Get("pack_odds_rule_rare_floor")));
            rules.Add(Rule(Loc.Format("pack_odds_rule_epic", PackPityState.EpicPityPacks)));
            rules.Add(Rule(Loc.Format("pack_odds_rule_first_legendary", PackPityState.LegendaryFirstWindowPacks)));
            rules.Add(Rule(Loc.Format("pack_odds_rule_legendary", PackPityState.LegendaryPityPacks)));
            rules.Add(Rule(Loc.Get("pack_odds_rule_per_set")));
            columns.Add(rules);

            _body.Add(columns);
            _body.Add(Line(Loc.Get("pack_odds_footer"), "pack-odds-footer"));

            _overlay.RemoveFromClassList(HiddenClass);
        }

        public void Hide() => _overlay?.AddToClassList(HiddenClass);

        private static void AddRow(VisualElement table, string rarityKey, int permille)
        {
            var row = new VisualElement();
            row.AddToClassList("pack-odds-row");
            row.Add(Line(Loc.Get(rarityKey), "pack-odds-row__rarity"));
            // Permille to a percent without a float: 30 -> "3", 25 -> "2.5" (or "2,5").
            string percent = (permille / 10m).ToString("0.#", Loc.Culture);
            row.Add(Line(Loc.Format("pack_odds_percent", percent), "pack-odds-row__chance"));
            table.Add(row);
        }

        // No bullet glyph: a non-ASCII mark is a bet on the font's coverage (see .emb-back-button
        // in theme.uss), so the rules are set off by indent in shop.uss instead.
        private static Label Rule(string text) => Line(text, "pack-odds-rule");

        private static Label Line(string text, string cssClass)
        {
            var label = new Label(text);
            label.AddToClassList(cssClass);
            return label;
        }
    }
}
