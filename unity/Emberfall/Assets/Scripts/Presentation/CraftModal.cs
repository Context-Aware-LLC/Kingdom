using System;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The crafting modal: one card, and the two directions you can move it in — <b>Render</b> (spend
    /// shards, mint a copy, GH#157) and <b>Reduce</b> (destroy a copy, collect shards, GH#158).
    ///
    /// <para><b>The two halves are the screen</b> (Sam, 2026-08-12). Whichever direction the card
    /// cannot go right now is present and dead rather than absent: a card you own none of shows a live
    /// Render half and a dead Reduce half, and the dead half still states its price and its reason. A
    /// half that vanished would make the modal change shape per card and would quietly answer "can I
    /// break this down?" with silence.</para>
    ///
    /// <para><b>Not a MonoBehaviour, deliberately</b> — same shape and same reason as
    /// <see cref="CardWall"/>: it owns no scene state, and a host component constructs it against a
    /// subtree of an already-loaded plate.</para>
    ///
    /// <para><b>It decides nothing.</b> Every number and every refusal on it comes from
    /// <see cref="Crafting"/>, which is the same pure predicate the server binds to
    /// <c>IEntitlementLedger</c> — so an offer this modal draws cannot be one the server then refuses.
    /// That shared-function property is the whole reason <c>Crafting.cs</c> lives in
    /// <c>Emberfall.Core.Collection</c> (netstandard2.1) rather than server-side, and it is worth not
    /// breaking: if you ever find yourself computing a cost or a refusal in this file, the number you
    /// want already exists on <see cref="RenderOffer"/> or <see cref="ReduceOffer"/>.</para>
    ///
    /// <para>The host reads and mutates through the authenticated crafting gateway. A commit stays
    /// open and disabled until the server responds; only a confirmed craft closes the modal.</para>
    /// </summary>
    public sealed class CraftModal
    {
        private readonly VisualElement _rootPlate;
        private readonly VisualElement _modal;
        private readonly VisualElement _scrim;
        private readonly VisualElement _cardFace;
        private readonly Label _cardName;
        private readonly Label _cardRarity;
        private readonly Label _shardBalance;
        private readonly Label _copiesOwned;
        private readonly Label _status;
        private readonly Button _retry;
        private readonly Button _close;
        private readonly VisualElement _loadingIcon;
        private readonly IVisualElementScheduledItem _loadingTick;
        private bool _submitting;
        private bool _balancesUnknown;

        private readonly Button _renderButton;
        private readonly Label _renderCost;
        private readonly Label _renderNote;
        private readonly VisualElement _renderHalf;

        private readonly Button _reduceButton;
        private readonly Label _reducePayout;
        private readonly Label _reduceNote;
        private readonly VisualElement _reduceHalf;

        private readonly CardFaceBaker _baker;
        private Texture2D _cardTexture;

        private CardDefinition _card;

        /// <summary>The card the modal is currently open on, or null when it is closed.</summary>
        public CardDefinition Card => _card;

        public bool IsOpen => _card != null;

        /// <summary>Raised when the player commits a Render. The host spends; this never does.</summary>
        public event Action<CardDefinition> RenderRequested;

        /// <summary>Raised when the player commits a Reduce. The host destroys; this never does.</summary>
        public event Action<CardDefinition> ReduceRequested;
        public event Action<CardDefinition> RefreshRequested;

        /// <summary>
        /// Binds against an already-cloned plate. Every element is required: a null here is a broken
        /// UXML rather than a configuration, exactly as on <see cref="CollectionController"/>, so the
        /// host logs one error naming the plate rather than this failing silently per-field later.
        /// </summary>
        public CraftModal(VisualElement rootPlate, CardFaceBaker baker)
        {
            _rootPlate = rootPlate;
            _baker = baker;

            _modal = rootPlate.Q<VisualElement>("craft-modal");
            _scrim = rootPlate.Q<VisualElement>("craft-scrim");
            _cardFace = rootPlate.Q<VisualElement>("craft-card-face");
            _cardName = rootPlate.Q<Label>("craft-card-name");
            _cardRarity = rootPlate.Q<Label>("craft-card-rarity");
            _shardBalance = rootPlate.Q<Label>("craft-shard-balance");
            _copiesOwned = rootPlate.Q<Label>("craft-copies-owned");
            _status = rootPlate.Q<Label>("craft-status");
            _retry = rootPlate.Q<Button>("craft-retry");
            _close = rootPlate.Q<Button>("craft-close");
            _loadingIcon = rootPlate.Q<VisualElement>("craft-loading-icon");
            if (_loadingIcon != null)
            {
                _loadingTick = _loadingIcon.schedule.Execute(() =>
                    _loadingIcon.style.rotate = new Rotate(new Angle(Time.realtimeSinceStartup * 36f % 360f)))
                    .Every(16);
                _loadingTick.Pause();
            }
            _retry?.RegisterCallback<ClickEvent>(_ => RefreshRequested?.Invoke(_card));

            _cardFace?.parent.RegisterCallback<GeometryChangedEvent>(_ => FitCardToHeight());

            _renderHalf = rootPlate.Q<VisualElement>("craft-render-half");
            _renderButton = rootPlate.Q<Button>("craft-render-button");
            _renderCost = rootPlate.Q<Label>("craft-render-cost");
            _renderNote = rootPlate.Q<Label>("craft-render-note");

            _reduceHalf = rootPlate.Q<VisualElement>("craft-reduce-half");
            _reduceButton = rootPlate.Q<Button>("craft-reduce-button");
            _reducePayout = rootPlate.Q<Label>("craft-reduce-payout");
            _reduceNote = rootPlate.Q<Label>("craft-reduce-note");

            if (_renderButton != null)
                _renderButton.RegisterCallback<ClickEvent>(_ => Commit(RenderRequested));
            if (_reduceButton != null)
                _reduceButton.RegisterCallback<ClickEvent>(_ => Commit(ReduceRequested));

            // Dismiss on the scrim and on the explicit close, but NOT on the modal body — a click that
            // lands on the panel must not fall through to the scrim behind it and close the thing the
            // player is reading. UI Toolkit bubbles, so the body swallows its own clicks.
            if (_scrim != null)
                _scrim.RegisterCallback<ClickEvent>(_ => Close());
            _modal?.RegisterCallback<ClickEvent>(e => e.StopPropagation());
            _close?.RegisterCallback<ClickEvent>(_ => Close());

            Close();
        }

        /// <summary>True when every element this modal binds was found — a false is a broken plate.</summary>
        public bool IsBound =>
            _modal != null && _scrim != null && _cardFace != null && _cardName != null &&
            _cardRarity != null && _shardBalance != null && _copiesOwned != null &&
            _renderHalf != null && _renderButton != null && _renderCost != null && _renderNote != null &&
            _reduceHalf != null && _reduceButton != null && _reducePayout != null && _reduceNote != null &&
            _status != null && _retry != null && _close != null && _loadingIcon != null;

        /// <summary>
        /// Show the modal for one card, drawn entirely from the two offers.
        ///
        /// <para>The caller computes the offers because it is the thing that knows the copy count and
        /// the balance; this draws them and asks nothing further of anyone.</para>
        /// </summary>
        public void Open(CardDefinition def, RenderOffer render, ReduceOffer reduce, long shardBalance)
        {
            if (def == null || !IsBound) return;

            if (_card != def) _cardTexture = null;
            _card = def;
            _submitting = false;
            _balancesUnknown = false;
            _close?.SetEnabled(true);
            _status.style.display = DisplayStyle.None;
            _retry.style.display = DisplayStyle.None;
            SetLoading(_cardTexture == null && _baker != null);

            _cardName.text = def.Name;
            _cardRarity.text = RarityWord(def.Rarity);

            // The rarity accent is the one colour on this panel that varies, and it is the fastest
            // read of what a card costs to make — the price and the colour say the same thing.
            foreach (string cls in RarityClasses) _cardRarity.RemoveFromClassList(cls);
            _cardRarity.AddToClassList(CardClassTokens.RarityToken(def.Rarity, "craft-rarity--"));

            _shardBalance.text = shardBalance.ToString();
            _copiesOwned.text = $"{render.CopiesOwned} owned";

            _renderCost.text = render.Cost.ToString();
            _renderNote.text = RenderNote(render);
            SetHalfEnabled(_renderHalf, _renderButton, render.Offerable);

            _reducePayout.text = reduce.Payout.ToString();
            _reduceNote.text = ReduceNote(reduce, def);
            SetHalfEnabled(_reduceHalf, _reduceButton, reduce.Offerable);

            // The real card front, baked — never a second UI Toolkit re-implementation of the card
            // face. Same baker and same cache the wall uses, so opening this modal on a card already
            // on screen costs no bake at all.
            _baker?.Bake(def, tex =>
            {
                if (_cardFace == null || _card != def) return;
                if (tex == null)
                {
                    if (!_balancesUnknown) ShowError("Could not load card artwork.");
                    return;
                }
                _cardTexture = (Texture2D)tex;
                _cardFace.style.backgroundImage =
                    new StyleBackground(Background.FromTexture2D((Texture2D)tex));
                if (!_balancesUnknown) SetLoading(false);
            });

            _modal.style.display = DisplayStyle.Flex;
            _scrim.style.display = DisplayStyle.Flex;
        }

        public void Close()
        {
            if (_submitting) return;
            SetLoading(false);
            _card = null;
            if (_modal != null) _modal.style.display = DisplayStyle.None;
            if (_scrim != null) _scrim.style.display = DisplayStyle.None;
        }

        private void FitCardToHeight()
        {
            if (_cardFace?.parent == null) return;

            // Fill the body height at the baked collection face's proportions.
            float height = _cardFace.parent.contentRect.height;
            if (float.IsNaN(height) || float.IsInfinity(height) || height <= 0f) return;

            float width = height * CardWall.TileWidth / CardWall.TileHeight;
            if (!Mathf.Approximately(_cardFace.resolvedStyle.height, height))
                _cardFace.style.height = height;
            if (!Mathf.Approximately(_cardFace.resolvedStyle.width, width))
                _cardFace.style.width = width;
        }

        public Task PlayConfirmedCraftAsync(bool render, CancellationToken ct)
            => CraftCardEffect.PlayAsync(_cardFace, _cardTexture, render, ct);

        private void Commit(Action<CardDefinition> handler)
        {
            CardDefinition committed = _card;
            if (committed == null || _submitting || handler == null) return;
            SetWorking(string.Empty, submitting: true);
            handler?.Invoke(committed);
        }

        public void SetWorking(string message, bool submitting = false, bool unknown = false)
        {
            SetLoading(unknown);
            _submitting = submitting;
            _close?.SetEnabled(!submitting);
            _renderButton.SetEnabled(false);
            _reduceButton.SetEnabled(false);
            _status.text = message;
            _status.style.display = unknown || submitting || string.IsNullOrEmpty(message)
                ? DisplayStyle.None : DisplayStyle.Flex;
            _retry.style.display = DisplayStyle.None;
            if (unknown)
            {
                _balancesUnknown = true;
                _shardBalance.text = "...";
                _copiesOwned.text = "...";
                _renderNote.text = "";
                _reduceNote.text = "";
            }
        }

        private void SetLoading(bool loading)
        {
            _modal?.EnableInClassList("craft-modal--loading", loading);
            if (loading) _loadingTick?.Resume();
            else _loadingTick?.Pause();
        }

        public void ShowError(string message)
        {
            SetWorking(message);
            if (_balancesUnknown)
            {
                _shardBalance.text = "--";
                _copiesOwned.text = "--";
            }
            _retry.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// A dead half stays visible and stays readable; only the button stops working. The modifier
        /// carries the treatment so the stylesheet owns what "dead" looks like.
        /// </summary>
        private static void SetHalfEnabled(VisualElement half, Button button, bool enabled)
        {
            half.EnableInClassList("craft-half--dead", !enabled);
            button.SetEnabled(enabled);
        }

        /// <summary>
        /// What the Render half says under its price. Every branch is a
        /// <see cref="RenderRefusal"/>, so a refusal added to that enum surfaces here as the default
        /// rather than as blank space.
        /// </summary>
        private static string RenderNote(RenderOffer offer)
        {
            switch (offer.Refusal)
            {
                case RenderRefusal.None:
                    return offer.CopiesOwned > 0 ? "Make another copy." : "Make this card.";
                case RenderRefusal.InsufficientShards:
                    return "Not enough shards.";
                case RenderRefusal.AtCopyCap:
                    return offer.CopyCap == 1
                        ? "You already have this Legendary."
                        : "You already have every copy you can play.";
                case RenderRefusal.NotCollectible:
                    return "This card cannot be made.";
                case RenderRefusal.UnknownCard:
                    return "Unknown card.";
                default:
                    return "Cannot be made right now.";
            }
        }

        /// <summary>Mirror of <see cref="RenderNote"/> for the other direction.</summary>
        private static string ReduceNote(ReduceOffer offer, CardDefinition def)
        {
            switch (offer.Refusal)
            {
                case ReduceRefusal.None:
                    // "Down to your last one" is the sentence that stops a regretted click, and it is
                    // the only thing this half knows that the payout number does not say.
                    return offer.CopiesAfter == 0
                        ? "This is your last copy."
                        : $"You would keep {offer.CopiesAfter}.";
                case ReduceRefusal.OwnsNone:
                    return "You don't have this card.";
                case ReduceRefusal.NotCollectible:
                    return "This card cannot be broken down.";
                case ReduceRefusal.UnknownCard:
                    return "Unknown card.";
                default:
                    return "Cannot be broken down right now.";
            }
        }

        private static readonly string[] RarityClasses =
        {
            "craft-rarity--common", "craft-rarity--rare", "craft-rarity--epic", "craft-rarity--legendary",
        };

        private static string RarityWord(Rarity r)
        {
            switch (r)
            {
                case Rarity.RARE: return "Rare";
                case Rarity.EPIC: return "Epic";
                case Rarity.LEGENDARY: return "Legendary";
                default: return "Common";
            }
        }
    }
}
