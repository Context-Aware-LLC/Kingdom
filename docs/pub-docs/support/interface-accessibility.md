# Interface Accessibility

Kingdoms's interface is being built around the standards below. This is an implementation target, not a final accessibility-conformance claim; release testing will determine which guarantees can be stated without qualification.

## Readability

- Actionable labels and game-state text use a phone-scale legibility floor, with smaller text allowed only when strong weight, contrast, and background treatment preserve readability.
- Body text targets at least a 4.5:1 contrast ratio; large text targets at least 3:1.
- Color never carries a rule by itself. Shape, text, iconography, brightness, or motion supplies a second cue.
- Interface text is expected to reflow up to 200% operating-system text scale without clipping actionable labels.

## Input parity

- Touch targets are at least 44 pixels at the reference scale.
- An action available through mouse hover also has a touch equivalent, usually long-press.
- Touch dragging has a pointer-friendly alternative.
- Meta screens are designed for full keyboard and gamepad navigation, with a visible focus indicator and logical focus order.
- Modals keep focus inside themselves until closed, and transient notices are announced without interrupting the player.

## Motion and feedback

- Reduced-motion settings replace decorative slides and flourishes with quieter transitions.
- Critical game-state feedback remains visible even when decorative motion is reduced.
- Rejected actions provide a plain-language reason in addition to visual feedback, so a failed play does not look like ignored input.

## Mobile layout

Interactive controls stay inside device safe areas around notches, rounded corners, and home indicators. Decorative board art may extend underneath those regions, but cards and buttons do not.

Accessibility defects should be reported with the device, input method, screen, and the exact action that became unreadable or unreachable.
