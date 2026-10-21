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
    /// The pack-opening moment: a <see cref="PackOpenReceipt"/> dealt face down for the player
    /// to reveal. Batches reveal automatically. Split out of <see cref="ShopPanelController"/> on 2026-08-20,
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
        private readonly List<RevealCard> _revealCards = new();
        private readonly List<IVisualElementScheduledItem> _beats = new();
        private readonly Label _hint;
        private RevealCard _flipping;
        private bool _automatic;
        private int _requestOrder;

        private sealed class RevealCard
        {
            public VisualElement Root, Face, Back;
            public PackRevealEffect Effect;
            public Rarity Rarity;
            public bool Arrived, Ready, Requested, Revealed;
            public int RequestOrder;
        }

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
            dismiss?.RegisterCallback<ClickEvent>(_ => AdvanceOrDone());
            _overlay?.RegisterCallback<DetachFromPanelEvent>(_ => Hide());
            if (_overlay != null)
            {
                _hint = new Label { name = "reveal-hint", pickingMode = PickingMode.Ignore };
                _hint.AddToClassList("reveal-hint");
                _overlay.Add(_hint);
            }

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
            _automatic = false;
            _cardRow.Clear();
            _cardRow.RemoveFromClassList(BatchClass);
            _cardRow.RemoveFromClassList(CrowdedClass);
            _list?.Clear();
            _list?.AddToClassList(ListHiddenClass);
            if (_title != null) _title.text = Loc.Format("pack_reveal_title_single", TokenText.PackName(receipt.PackId));
            if (_summary != null) _summary.text = CardCount(receipt.Awards.Count);

            for (int i = 0; i < receipt.Awards.Count; i++)
            {
                int delay = 100 + (i * 105);
                _cardRow.Add(BuildRevealCard(receipt.Awards[i], delay));
            }

            _overlay.RemoveFromClassList(HiddenClass);
            UpdateHint();
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
            _automatic = true;
            _cardRow.Clear();
            _cardRow.AddToClassList(BatchClass);
            _cardRow.RemoveFromClassList(CrowdedClass);

            var awards = new List<PackCardAward>();
            foreach (PackOpenReceipt receipt in receipts)
                if (receipt != null) awards.AddRange(receipt.Awards);

            if (_title != null)
                _title.text = receipts.Count == 1
                    ? Loc.Format("pack_reveal_title_single", TokenText.PackName(receipts[0].PackId))
                    : Loc.Plural("pack_reveal_title_many", receipts.Count, receipts.Count);
            if (_summary != null)
            {
                string cards = CardCount(awards.Count);
                _summary.text = string.IsNullOrEmpty(note)
                    ? cards
                    : Loc.Format("pack_reveal_summary_with_note", cards, note);
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
            UpdateHint();
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
                string name = definition != null ? CardLoc.Name(definition, _cards) : Loc.Format("pack_reveal_card_fallback_name", award.CardId);

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
                    var total = new Label(Loc.Format("pack_reveal_copies", copies)) { pickingMode = PickingMode.Ignore };
                    total.AddToClassList("reveal-list__total");
                    header.Add(total);
                }
                panel.Add(header);

                var body = new VisualElement { pickingMode = PickingMode.Ignore };
                body.AddToClassList("reveal-list__body");

                if (rarity == Rarity.COMMON)
                {
                    var commonCount = new Label(CardCount(copies))
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
                var copies = new Label(Loc.Format("pack_reveal_copies", award.Value)) { pickingMode = PickingMode.Ignore };
                copies.AddToClassList("reveal-list__copies");
                row.Add(copies);
            }

            return row;
        }

        private static string RarityWord(Rarity r) => GameTerms.Rarity(r);

        private static string CardCount(int count) => Loc.Plural("pack_reveal_card_count", count, count);

        public void Hide()
        {
            _generation++;
            foreach (IVisualElementScheduledItem beat in _beats) beat.Pause();
            _beats.Clear();
            foreach (RevealCard card in _revealCards) card.Effect.Stop();
            _revealCards.Clear();
            _flipping = null;
            _requestOrder = 0;
            _overlay?.RemoveFromClassList("reveal-overlay--spotlight");
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

        // A background tap finishes the reveal before it can dismiss it. This also gives the
        // existing Android back target a safe fast path without adding another chrome button.
        private void AdvanceOrDone()
        {
            bool pending = false;
            var remaining = new List<RevealCard>(_revealCards);
            remaining.Sort((a, b) =>
            {
                int rarity = a.Rarity.CompareTo(b.Rarity);
                return rarity != 0 ? rarity : _revealCards.IndexOf(a).CompareTo(_revealCards.IndexOf(b));
            });
            foreach (RevealCard card in remaining)
            {
                if (card.Revealed) continue;
                if (!card.Requested) card.RequestOrder = _requestOrder++;
                card.Requested = true;
                pending = true;
            }
            if (pending || _flipping != null) { PumpReveals(); return; }
            Done();
        }

        private void UpdateHint()
        {
            if (_hint == null) return;
            bool finished = _flipping == null && _revealCards.TrueForAll(c => c.Revealed);
            _hint.text = Loc.Get(finished ? "pack_reveal_return_hint" :
                _automatic ? "pack_reveal_opening_hint" : "pack_reveal_tap_hint");
        }

        private VisualElement BuildRevealCard(PackCardAward award, int revealDelayMs)
        {
            CardDefinition definition = Definition(award.CardId);

            var card = new VisualElement { focusable = true, tabIndex = 0 };
            card.AddToClassList("reveal-card");
            card.AddToClassList(CardClassTokens.RarityToken(award.Rarity, "reveal-card--"));
            var effect = new PackRevealEffect(award.Rarity);
            card.Add(effect);
            var face = new VisualElement { pickingMode = PickingMode.Ignore };
            face.AddToClassList("reveal-card__face");
            card.Add(face);
            var back = new VisualElement { pickingMode = PickingMode.Ignore };
            back.AddToClassList("reveal-card__back");
            card.Add(back);
            var state = new RevealCard
            {
                Root = card, Face = face, Back = back, Effect = effect, Rarity = award.Rarity,
                Requested = _automatic,
                RequestOrder = ((int)award.Rarity * 1000) + _revealCards.Count,
            };
            _revealCards.Add(state);
            card.RegisterCallback<ClickEvent>(evt =>
            {
                evt.StopPropagation();
                RequestReveal(state);
            });
            card.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.Space) return;
                evt.StopPropagation();
                evt.PreventDefault();
                RequestReveal(state);
            });
            card.RegisterCallback<PointerEnterEvent>(_ => effect.SetHovered(true));
            card.RegisterCallback<PointerLeaveEvent>(_ => effect.SetHovered(false));
            card.RegisterCallback<FocusInEvent>(_ => effect.SetHovered(true));
            card.RegisterCallback<FocusOutEvent>(_ => effect.SetHovered(false));

            // This fallback is held out of view while CardFaceBaker's serial queue builds the
            // canonical printed card. It appears only if capture fails, keeping the award readable
            // without ever layering a second name or rarity treatment over a real face.
            var fallback = new VisualElement { pickingMode = PickingMode.Ignore };
            fallback.AddToClassList("reveal-card__fallback");
            face.Add(fallback);

            var flourish = new VisualElement { pickingMode = PickingMode.Ignore };
            flourish.AddToClassList("reveal-card__flourish");
            fallback.Add(flourish);

            // No rarity line (Sam, 2026-09-27): the border colour already says it.

            // The illustration-and-name treatment is only the resilient fallback. The vault injects
            // a CardFaceBaker below, which replaces it with the canonical printed card as soon as
            // that off-camera capture completes; hosts without a rig can still use the panel.
            var art = new VisualElement { pickingMode = PickingMode.Ignore };
            art.AddToClassList("reveal-card__art");
            fallback.Add(art);
            PaintArt(art, definition);

            var name = new Label(definition != null ? CardLoc.Name(definition, _cards) : Loc.Format("pack_reveal_card_fallback_name", award.CardId));
            name.pickingMode = PickingMode.Ignore;
            name.AddToClassList("reveal-card__name");
            fallback.Add(name);

            if (_audio == null)
                _audio = PackRevealAudio.Create();
            PaintFace(state, fallback, definition);
            Schedule(() =>
            {
                state.Arrived = true;
                card.AddToClassList("reveal-card--shown");
                _audio?.CardArrived(_revealCards.IndexOf(state));
                PumpReveals();
            }, revealDelayMs);

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
        private void PaintFace(RevealCard card, VisualElement fallback, CardDefinition definition)
        {
            if (_faceBaker == null || definition == null)
            {
                card.Ready = true;
                return;
            }

            // Reserve the card's layout slot but do not show the old mini-card while the canonical
            // face is in flight. A successful reveal is full-card-first; the fallback appears only
            // when capture fails or its callback exceeds the watchdog below.
            fallback.style.display = DisplayStyle.None;
            int generation = _generation;
            _faceBaker.Bake(definition, texture =>
            {
                if (generation != _generation) return;
                if (texture is Texture2D face)
                {
                    card.Face.style.backgroundImage =
                        new StyleBackground(Background.FromTexture2D(face));
                    fallback.style.display = DisplayStyle.None;
                }
                else
                    fallback.style.display = DisplayStyle.Flex;
                card.Ready = true;
                PumpReveals();
            });
            // A missing art callback must not trap a confirmed award behind its back forever.
            // Normally the canonical face is ready well before a player touches the card.
            Schedule(() =>
            {
                if (card.Ready) return;
                fallback.style.display = DisplayStyle.Flex;
                card.Ready = true;
                Debug.LogWarning("[PackReveal] face capture timed out; showing the award fallback.");
                PumpReveals();
            }, 2500);
        }

        private void RequestReveal(RevealCard card)
        {
            if (card.Requested || card.Revealed || !_revealCards.Contains(card)) return;
            card.RequestOrder = _requestOrder++;
            card.Requested = true;
            card.Root.AddToClassList("reveal-card--queued");
            PumpReveals();
        }

        // One reveal owns the spotlight. Fast taps queue safely, and loading completion cannot
        // reorder an automatic run: common cards go first, with the strongest pull saved for last.
        private void PumpReveals()
        {
            if (_flipping != null) return;
            RevealCard next = null;
            foreach (RevealCard card in _revealCards)
            {
                if (!card.Requested || card.Revealed) continue;
                if (next == null || card.RequestOrder < next.RequestOrder) next = card;
            }
            if (next == null) { UpdateHint(); return; }
            if (!next.Arrived || !next.Ready) return;

            _flipping = next;
            bool legendary = next.Rarity == Rarity.LEGENDARY;
            int chargeMs = legendary ? 320 : next.Rarity == Rarity.EPIC ? 180 : 60;
            next.Root.RemoveFromClassList("reveal-card--queued");
            next.Root.AddToClassList("reveal-card--charging");
            next.Effect.SetHovered(true);
            if (legendary)
            {
                next.Root.AddToClassList("reveal-card--celebrating");
                _overlay.AddToClassList("reveal-overlay--spotlight");
            }
            Schedule(() => next.Root.AddToClassList("reveal-card--turning"), chargeMs);
            Schedule(() =>
            {
                next.Back.style.display = DisplayStyle.None;
                next.Face.style.visibility = Visibility.Visible;
                next.Root.AddToClassList("reveal-card--revealed");
                next.Root.RemoveFromClassList("reveal-card--turning");
                next.Root.RemoveFromClassList("reveal-card--charging");
                next.Revealed = true;
                next.Effect.Reveal();
                if (legendary) _audio?.LegendaryRevealed();
                else _audio?.CardRevealed(next.Rarity);
            }, chargeMs + 140);
            Schedule(() =>
            {
                next.Root.RemoveFromClassList("reveal-card--celebrating");
                _overlay.RemoveFromClassList("reveal-overlay--spotlight");
                _flipping = null;
                UpdateHint();
                PumpReveals();
            }, chargeMs + (legendary ? 1000 : next.Rarity == Rarity.EPIC ? 600 : 360));
        }

        private void Schedule(Action action, int delayMs)
        {
            int generation = _generation;
            _beats.Add(_overlay.schedule.Execute(() =>
            {
                if (generation != _generation || _overlay.panel == null) return;
                action();
            }).StartingIn(delayMs));
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
