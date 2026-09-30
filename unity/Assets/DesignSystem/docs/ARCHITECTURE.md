# Architecture

The design system is a stack of independent USS files imported by a single master stylesheet, plus a theme-authoring pipeline and one runtime helper script. This document walks through the layers and the load-bearing decisions behind them.

## The stack

```
DesignSystemBehaviour.cs          ← C# helper, auto-attaches to UIDocument
        │
        ▼
DesignSystem.uss                 ← master, @imports the layers below
        │
        ├── DesignTokens.uss     ← :root variables only — no element rules
       parent-state cascade
        ├── Buttons.uss          ← .ds-btn variants, sizes, icon button
        ├── Inputs.uss           ← .ds-input, .ds-search, .ds-dropdown, .ds-textarea
        ├── TabsAndFilters.uss   ← .ds-tabs, .ds-tab, .ds-view-toggle
        ├── Cards.uss            ← .ds-animal-card, .ds-info-row, .ds-swatch-row
        ├── Navigation.uss       ← .ds-side-nav, .ds-side-rail, .ds-bottom-nav, .ds-profile
        ├── Badges.uss           ← .ds-badge, .ds-tag, .ds-chip, .ds-avatar, .ds-notif-*
        ├── Controls.uss         ← .ds-toggle, .ds-check, .ds-radio, .ds-slider, .ds-range, scoped scrollbars
        ├── Overlays.uss         ← .ds-modal, .ds-dialog, .ds-toast, .ds-sheet, .ds-empty
        ├── Feedback.uss         ← .ds-progress, .ds-spinner, .ds-skeleton, .ds-pagination, showcase helpers
        └── Mobile.uss           ← .mobile-prefixed overrides — loaded LAST
```

A consumer attaches **one stylesheet** (`DesignSystem.uss`) to their `UIDocument`. The `@import` chain pulls everything else.

## Tokens

`DesignTokens.uss` declares CSS custom properties on `:root`. Every other file references them via `var(--…)`:


`.mobile` is a single class added to your screen root. Every responsive rule lives in `Mobile.uss` and is prefixed with `.mobile`:


The selector `.mobile .ds-btn` has higher specificity (`0,2,0`) than `.ds-btn` (`0,1,0`), so the mobile rule always wins when the screen root carries `.mobile`.

Pattern across the system:

- Buttons / inputs / tabs grow to 48 px tall (touch target minimum).
- Slider thumbs grow 18 px → 24 px with recomputed `margin-top: -12px` for centring.
- Modals widen, side rails compact, bottom-nav bar takes over from side-nav.

You toggle the class once at screen build time:

```csharp
if (PanelSettingsHelper.IsMobileLayout())   // your own platform check
    root.AddToClassList("mobile");
```

Same UXML, same component classes — the layout flips.

## Adding a new layer

If a new component family doesn't fit `Cards.uss` or `Overlays.uss`:

1. Create `<Family>.uss` next to the existing layer files.
2. Use only `var(--…)` token references.
3. Append to `DesignSystem.uss`'s `@import` chain — typically before `Mobile.uss`.
4. Add `.mobile` overrides for the new family in `Mobile.uss`.

The `@import` order is the single source of truth for cascade. Don't try to control order via specificity tricks; let the import sequence carry it.

## Scrollbar styling

Unity's UI Toolkit ScrollView is a compound element with internal classes outside the `.ds-` namespace. The system styles them in `Controls.uss`, scoped to `.ds-root` so the rules don't leak into editor windows or other UI Toolkit panels in the same project:

```css
.ds-root .unity-scroller__low-button,
.ds-root .unity-scroller__high-button { display: none; }

.ds-root .unity-scroll-view__vertical-scroller   { width: 8px; }
.ds-root .unity-scroll-view__horizontal-scroller { height: 8px; }

.ds-root .unity-base-slider__tracker {
    background-color: transparent;
    border-width: 0;
}

.ds-root .unity-base-slider__dragger {
    background-color: var(--color-border-strong);
    border-radius: 4px;
    border-width: 0;
    transition-property: background-color;
    transition-duration: var(--transition-fast);
}

.ds-root .unity-base-slider__dragger:hover {
    background-color: var(--color-text-secondary);
}
```

Both axes share the same thumb pattern. The thumb auto-themes through `--color-border-strong` and `--color-text-secondary`, so a theme override repaints the scrollbar with no extra rules.

Why scoped to `.ds-root` instead of the universal `.unity-base-slider__dragger`? The same internal classes are used by Unity's editor windows, the Inspector, the UI Builder, and any other UI Toolkit panel in the project. A naked `.unity-base-slider__dragger { … }` rule would re-skin every scrollbar in every editor tool — not what consumers want when dropping the design system into an existing project.
