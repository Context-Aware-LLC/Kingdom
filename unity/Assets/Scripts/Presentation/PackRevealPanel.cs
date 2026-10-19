using System;
using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client.Shop;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The pack-opening moment: a <see cref="PackOpenReceipt"/> dealt out as a row of cards that
    /// arrive one after another. Split out of <see cref="ShopPanelController"/> on 2026-08-20,
    /// where it had been a second responsibility joined to the store by exactly two calls.
    ///
    /// <para><b>Host-agnostic, and that is the whole point of the split.</b> It queries its
    /// <c>reveal-*</c> elements out of whatever root it is handed, so the same panel serves the
    /// store's overlay and <see cref="PackVaultController"/>'s screen. A pack opened anywhere other
    /// than the store — the onboarding grant is the one already waiting — could not have had a
    /// reveal at all while this lived inside the store's controller. That prediction came true on
    /// 2026-09-03: the vault opens granted packs, and the frame this used to name
    /// (<c>PackRevealScreenController</c>) was deleted in the same commit.</para>
    ///
    /// <para><b>Not <see cref="CardRevealController"/>.</b> That is EF-049, the in-match hover
    /// reveal: MonoBehaviours, TextMeshPro, board colliders. Same word, unrelated subsystem. The
    /// <c>Pack</c> prefix here is load-bearing.</para>
    ///
    /// <para>Nothing here reads the wallet, the catalog or the gateway. A receipt is the entire
    /// input, and the receipt is server-authored — so this cannot invent an award.</para>
    /// </summary>
    public sealed class PackRevealPanel
    {
        private const string HiddenClass = "reveal-overlay--hidden";
        private const string BatchClass = "reveal-cards--batch";
        private const string CrowdedClass = "reveal-cards--crowded";
        private const string ListHiddenClass = "reveal-list--hidden";

        /// <summary>The row a single pack deals, and the floor <see cref="ShowMany"/> fills up to.</summary>
        private const int StandardRow = 5;

        /// <summary>The most cards <see cref="ShowMany"/> deals face-up. Ten fits one row at the batch
        /// card width; the rest are in the list.</summary>
        private const int MaxShown = 10;

        private readonly VisualElement _overlay;
        private readonly VisualElement _cardRow;

        /// <summary>The batch list under the shown row. Optional: only the vault's markup has one.</summary>
        private readonly VisualElement _list;
        private readonly Label _title;
        private readonly Label _summary;
        private readonly Action _onDone;
        private readonly CardFaceBaker _faceBaker;

        private ICardDatabase _cards;
        private IArtProvider _art;
        private PackRevealAudio _audio;

        /// <summary>Bumped when a reveal is hidden or replaced. An art callback carrying a stale generation
        /// is painting onto a card that has already been cleared off the row — the same version
        /// guard <see cref="CardWall"/> keeps, and for the same reason: <see cref="IArtProvider"/>
        /// may answer synchronously or frames later, and a second pack opened during the wait would
        /// otherwise take the first one's illustrations.</summary>
        private int _generation;

        /// <param name="root">Any element the <c>reveal-*</c> tree sits under.</param>
        /// <param name="onDone">What dismissing does after hiding. Null for a host that owns its own
        /// dismissal — the store just wants its overlay back — and a way home for a host where the
        /// reveal IS the screen.</param>
        public PackRevealPanel(VisualElement root, Action onDone = null, CardFaceBaker faceBaker = null)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            _overlay = root.Q<VisualElement>("reveal-overlay");
            _cardRow = root.Q<VisualElement>("reveal-cards");
            _title = root.Q<Label>("reveal-title");
            _summary = root.Q<Label>("reveal-summary");
            _list = root.Q<VisualElement>("reveal-list");
            _onDone = onDone;
            _faceBaker = faceBaker;

            // A tap anywhere off a card (Sam, 2026-09-27): there is no Done button. The dismiss layer
            // sits behind the cards, so a card swallows its own tap and only the gaps reach this.
            VisualElement dismiss = root.Q<VisualElement>("reveal-dismiss");
            dismiss?.RegisterCallback<ClickEvent>(_ => Done());
            _overlay?.RegisterCallback<DetachFromPanelEvent>(_ => Hide());

            // Start hidden regardless of what the markup said. A host that forgets the hidden class
            // would otherwise open on an empty stage, which reads as a bug in the pack, not the UXML.
            Hide();
        }

        /// <summary>True when the host's markup actually carried a reveal tree. A host without one
        /// gets a panel that silently does nothing rather than a null to guard at every call.</summary>
        public bool IsBound => _overlay != null && _cardRow != null;

        public void Show(PackOpenReceipt receipt)
        {
            if (receipt == null || !IsBound) return;
            Hide();
            _cardRow.Clear();
            _cardRow.RemoveFromClassList(BatchClass);
            _cardRow.RemoveFromClassList(CrowdedClass);
            _list?.Clear();
            _list?.AddToClassList(ListHiddenClass);
            if (_title != null) _title.text = TokenText.PackName(receipt.PackId) + " opened";
            if (_summary != null)
                _summary.text = receipt.Awards.Count == 1
                    ? "1 card"
                    : $"{receipt.Awards.Count} cards";

            for (int i = 0; i < receipt.Awards.Count; i++)
            {
                int delay = 140 + (i * 170);
                _cardRow.Add(BuildRevealCard(receipt.Awards[i], delay));
            }

            _overlay.RemoveFromClassList(HiddenClass);
        }

        /// <summary>
        /// Several packs at once (Sam, 2026-09-27). Ten packs is fifty cards, and fifty cards dealt
        /// face-up is a wall nobody reads — so the cards SHOWN are the ones worth a moment, and
        /// everything is accounted for in a list underneath.
        ///
        /// <para><b>The shown row:</b> every legendary and epic, best first; when those are fewer than
        /// <see cref="StandardRow"/>, rares (then commons) fill it up to that — "one rare, the epics
        /// and the legendary". Capped at <see cref="MaxShown"/>; anything past the cap is still in
        /// the list.</para>
        ///
        /// <para><b>The list:</b> one translucent plate per rarity. Rare and up are NAMED without
        /// repeating class labels; commons are COUNTED. Naming thirty-odd commons is the wall again,
        /// and there is no ScrollView to put it in (#285/#307).</para>
        ///
        /// <para>A host with no <c>reveal-list</c> in its markup gets the shown row alone.</para>
        /// </summary>
        /// <param name="note">A line for the summary when the run stopped short, or null.</param>
        public void ShowMany(IReadOnlyList<PackOpenReceipt> receipts, string note = null)
        {
            if (receipts == null || receipts.Count == 0 || !IsBound) return;
            if (receipts.Count == 1 && string.IsNullOrEmpty(note)) { Show(receipts[0]); return; }

            Hide();
            _cardRow.Clear();
            _cardRow.AddToClassList(BatchClass);
            _cardRow.RemoveFromClassList(CrowdedClass);

            var awards = new List<PackCardAward>();
            foreach (PackOpenReceipt receipt in receipts)
                if (receipt != null) awards.AddRange(receipt.Awards);

            if (_title != null)
                _title.text = receipts.Count == 1 ? TokenText.PackName(receipts[0].PackId) + " opened"
                                                  : $"{receipts.Count} packs opened";
            if (_summary != null)
            {
                string cards = awards.Count == 1 ? "1 card" : $"{awards.Count} cards";
                _summary.text = string.IsNullOrEmpty(note) ? cards : cards + " · " + note;
            }

            // Stable: equal rarities keep pack order, so the reveal does not reshuffle on a re-render.
            var order = new List<int>(awards.Count);
            for (int i = 0; i < awards.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                int byRarity = awards[b].Rarity.CompareTo(awards[a].Rarity);
                return byRarity != 0 ? byRarity : a.CompareTo(b);
            });
            var ranked = new List<PackCardAward>(order.Count);
            foreach (int i in order) ranked.Add(awards[i]);

            int highs = 0;
            foreach (PackCardAward award in ranked)
                if (award.Rarity >= Rarity.EPIC) highs++;
            int shown = Math.Min(Math.Max(highs, Math.Min(StandardRow, ranked.Count)), MaxShown);
            _cardRow.EnableInClassList(CrowdedClass, shown > StandardRow);

            for (int i = 0; i < shown; i++)
            {
                int delay = 140 + (i * 120);
                VisualElement card = BuildRevealCard(ranked[i], delay);
                if (shown > StandardRow)
                    card.style.width = Length.Percent((100f / shown) - 1f);
                _cardRow.Add(card);
            }

            BuildList(awards, note);
            _overlay.RemoveFromClassList(HiddenClass);
        }

        /// <summary>Everything in the list ignores picking, so a tap on it reaches the dismiss layer
        /// behind — the reveal's only way out.</summary>
        private void BuildList(List<PackCardAward> awards, string note)
        {
            if (_list == null) return;
            _list.Clear();
            _list.RemoveFromClassList(ListHiddenClass);

            // The vault's reveal has no summary line, so a run that stopped short says so here.
            if (!string.IsNullOrEmpty(note))
            {
                var stopped = new Label(note) { pickingMode = PickingMode.Ignore };
                stopped.AddToClassList("reveal-list__note");
                _list.Add(stopped);
            }

            // rarity → (name → copies). Class deliberately does not enter this read model: a
            // batch receipt is about what rarity arrived, not which deck-building bucket owns it.
            var byRarity = new SortedDictionary<Rarity, List<KeyValuePair<string, int>>>();
            foreach (PackCardAward award in awards)
            {
                CardDefinition definition = Definition(award.CardId);
                string name = definition != null ? definition.Name : $"Card {award.CardId}";

                if (!byRarity.TryGetValue(award.Rarity, out List<KeyValuePair<string, int>> names))
                    byRarity[award.Rarity] = names = new List<KeyValuePair<string, int>>();

                int at = names.FindIndex(n => n.Key == name);
                if (at < 0) names.Add(new KeyValuePair<string, int>(name, 1));
                else names[at] = new KeyValuePair<string, int>(name, names[at].Value + 1);
            }

            var rarityOrder = new[]
            {
                Rarity.COMMON, Rarity.RARE, Rarity.EPIC, Rarity.LEGENDARY,
            };

            foreach (Rarity rarity in rarityOrder)
            {
                if (!byRarity.TryGetValue(rarity, out List<KeyValuePair<string, int>> names)) continue;

                int copies = 0;
                foreach (KeyValuePair<string, int> name in names) copies += name.Value;

                var panel = new VisualElement { pickingMode = PickingMode.Ignore };
                panel.AddToClassList("reveal-list__panel");
                panel.AddToClassList(CardClassTokens.RarityToken(rarity, "reveal-list__panel--"));

                var header = new VisualElement { pickingMode = PickingMode.Ignore };
                header.AddToClassList("reveal-list__header");

                var heading = new Label(RarityWord(rarity)) { pickingMode = PickingMode.Ignore };
                heading.AddToClassList("reveal-list__rarity");
                header.Add(heading);

                if (rarity != Rarity.COMMON)
                {
                    var total = new Label($"×{copies}") { pickingMode = PickingMode.Ignore };
                    total.AddToClassList("reveal-list__total");
                    header.Add(total);
                }
                panel.Add(header);

                var body = new VisualElement { pickingMode = PickingMode.Ignore };
                body.AddToClassList("reveal-list__body");

                if (rarity == Rarity.COMMON)
                {
                    var commonCount = new Label(copies == 1 ? "1 card" : $"{copies} cards")
                        { pickingMode = PickingMode.Ignore };
                    commonCount.AddToClassList("reveal-list__common-count");
                    body.Add(commonCount);
                }
                else
                {
                    foreach (KeyValuePair<string, int> name in names)
                        body.Add(BuildNamedAward(name));
                }

                panel.Add(body);
                _list.Add(panel);
            }
        }

        private static VisualElement BuildNamedAward(KeyValuePair<string, int> award)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.AddToClassList("reveal-list__item");

            var marker = new VisualElement { pickingMode = PickingMode.Ignore };
            marker.AddToClassList("reveal-list__marker");
            row.Add(marker);

            var name = new Label(award.Key) { pickingMode = PickingMode.Ignore };
            name.AddToClassList("reveal-list__name");
            row.Add(name);

            if (award.Value > 1)
            {
                var copies = new Label($"×{award.Value}") { pickingMode = PickingMode.Ignore };
                copies.AddToClassList("reveal-list__copies");
                row.Add(copies);
            }

            return row;
        }

        private static string RarityWord(Rarity r) => r switch
        {
            Rarity.RARE => "Rare",
            Rarity.EPIC => "Epic",
            Rarity.LEGENDARY => "Legendary",
            _ => "Common",
        };

        public void Hide()
        {
            _generation++;
            _overlay?.AddToClassList(HiddenClass);
            if (_audio == null) return;
            _audio.Stop();
            UnityEngine.Object.Destroy(_audio.gameObject);
            _audio = null;
        }

        private void Done()
        {
            Hide();
            _onDone?.Invoke();
        }

        private VisualElement BuildRevealCard(PackCardAward award, int revealDelayMs)
        {
            CardDefinition definition = Definition(award.CardId);

            var card = new VisualElement();
            card.AddToClassList("reveal-card");
            card.AddToClassList(CardClassTokens.RarityToken(award.Rarity, "reveal-card--"));

            // This fallback is held out of view while CardFaceBaker's serial queue builds the
            // canonical printed card. It appears only if capture fails, keeping the award readable
            // without ever layering a second name or rarity treatment over a real face.
            var fallback = new VisualElement();
            fallback.AddToClassList("reveal-card__fallback");
            card.Add(fallback);

            var flourish = new VisualElement();
            flourish.AddToClassList("reveal-card__flourish");
            fallback.Add(flourish);

            // No rarity line (Sam, 2026-09-27): the border colour already says it.

            // The illustration-and-name treatment is only the resilient fallback. The vault injects
            // a CardFaceBaker below, which replaces it with the canonical printed card as soon as
            // that off-camera capture completes; hosts without a rig can still use the panel.
            var art = new VisualElement();
            art.AddToClassList("reveal-card__art");
            fallback.Add(art);
            PaintArt(art, definition);

            var name = new Label(definition != null ? definition.Name : $"Card {award.CardId}");
            name.AddToClassList("reveal-card__name");
            fallback.Add(name);

            if (award.Rarity == Rarity.LEGENDARY && _audio == null)
                _audio = PackRevealAudio.Create();
            PaintFace(card, fallback, definition, revealDelayMs, award.Rarity);

            // No award line. It read "NEW CARD" or "DUPE -> n SHARDS", and since the no-auto-sharding
            // ruling (Sam, 2026-08-12) every slot is a card — leaving "NEW CARD" would assert newness the
            // receipt no longer carries, and a third copy would be labelled new.
            //
            // No pity line either (Sam, 2026-09-27): nothing on the reveal may say a card was a
            // guaranteed or pity upgrade. award.Upgrade is still on the receipt; it is just not shown.

            return card;
        }

        /// <summary>Reveal the same complete printed card face used by the collection wall, falling
        /// back to the illustration tile only when capture fails. The generation check prevents a
        /// late face from a dismissed reveal painting onto a later pack's card element.</summary>
        private void PaintFace(VisualElement target, VisualElement fallback,
                               CardDefinition definition, int revealDelayMs, Rarity rarity)
        {
            if (_faceBaker == null || definition == null)
            {
                RevealAfter(target, revealDelayMs, rarity);
                return;
            }

            // Reserve the card's layout slot but do not show the old mini-card while the canonical
            // face is in flight. A successful reveal is full-card-first; the fallback appears only
            // when the baker explicitly reports that it could not capture one.
            fallback.style.display = DisplayStyle.None;
            int generation = _generation;
            _faceBaker.Bake(definition, texture =>
            {
                if (generation != _generation) return;
                if (texture is Texture2D face)
                    target.style.backgroundImage =
                        new StyleBackground(Background.FromTexture2D(face));
                else
                    fallback.style.display = DisplayStyle.Flex;
                RevealAfter(target, revealDelayMs, rarity);
            });
        }

        private void RevealAfter(VisualElement target, int delayMs, Rarity rarity)
        {
            int generation = _generation;
            target.schedule.Execute(() =>
            {
                if (generation != _generation || target.panel == null) return;
                target.AddToClassList("reveal-card--shown");
                if (rarity == Rarity.LEGENDARY) _audio?.LegendaryRevealed();
            }).StartingIn(delayMs);
        }

        /// <summary>The printed card behind an award, or null when the database will not open. Both
        /// the name and the illustration come off it, so one lookup answers both — and a card that
        /// cannot be resolved falls back to "Card {id}" rather than pretending to be a name.</summary>
        private CardDefinition Definition(int cardId)
        {
            try
            {
                _cards ??= CardDatabaseLoader.Load();
                if (_cards.TryGet(cardId, out CardDefinition definition)) return definition;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[PackReveal] card database unavailable: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Paint one card's illustration, if it has one.
        ///
        /// <para><b>A card with no art is not a failure.</b> The element keeps its USS background and
        /// the card reads as a card without a picture — the same posture <c>CardFaceBaker</c> takes,
        /// because art lands per card and lags authoring by design.</para>
        /// </summary>
        private void PaintArt(VisualElement target, CardDefinition definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.ArtId)) return;

#if UNITY_EDITOR
            _art ??= new EditorArtProvider();
#else
            _art ??= new AddressableArtProvider();
#endif
            int generation = _generation;
            _art.LoadArt(definition.ArtId, sprite =>
            {
                if (sprite == null || generation != _generation) return;
                target.style.backgroundImage = new StyleBackground(sprite);
            });
        }
    }
}
