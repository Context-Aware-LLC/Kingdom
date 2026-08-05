# Stone UI material set

Four transparent vector skins for the main menu and deck builder. Every SVG composites its
entire surface at `opacity="0.65"`, so the existing UI Toolkit background color can continue to
set the state and overall value beneath the stone artwork.

| Asset | Intended surface | Native view box | Notes |
| --- | --- | --- | --- |
| `ui_button_stone_slate.svg` | Main-menu slats | 600 × 128 | Cool, neutral, quiet label zone |
| `ui_button_stone_ember.svg` | Main-menu slats | 600 × 128 | Warmer basalt with edge-only ember seams |
| `ui_panel_stone_slate.svg` | Deck-builder panels | 1200 × 800 | Dressed blue-grey slate, least visually active |
| `ui_panel_stone_shale.svg` | Deck-builder panels | 1200 × 800 | Layered graphite shale with faint iron warmth |

The button artwork is authored at 2× the current `300 × 64 px` main-menu slat size. The panel
artwork keeps its central field subdued and reserves a 48 px perimeter for optional nine-slice
use. When used as a plain stretched `background-image`, `preserveAspectRatio="none"` allows the
art to follow the element bounds.

Suggested starting pair: slate buttons plus shale panels. This gives the two screens related
materials without making every surface visually identical.
