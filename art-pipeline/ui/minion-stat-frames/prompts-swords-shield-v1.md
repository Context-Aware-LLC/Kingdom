# Sword and shield stat frames — 2026-10-04

Built-in imagegen edits. Final textures are 128×128 RGBA with a 123px maximum painted span, matching the previous stat assets' canvas occupancy. On-card size and number positions are unchanged. Earlier fist and circular assets remain preserved.

- Minion power: `minion-power-sword-yellow-v1-128.png` → `socket_attack_sword.png`.
- Weapon power: `weapon-power-sword-red-v1-128.png` → `socket_weapon_power_sword.png`.
- Weapon toughness (internal durability): `weapon-toughness-shield-green-v1-128.png` → `socket_weapon_toughness_shield.png`.
- Minion health keeps the existing red heart.

## Yellow sword

Use case: precise-object-edit. Reference/edit target is the supplied yellow circular stat socket for a fantasy card game. Replace its circular silhouette with a clearly recognizable SWORD-SHAPED FRAME, upright blade pointing up, broad simple triangular tip, very broad short blade, obvious horizontal crossguard below the center, short grip and pommel at the bottom. The sword shape is the entire asset; NO circle or shield behind it. Keep the existing golden yellow mottled interior and weathered silver-gray metal edge with warm brown inner edging. The broad central blade is a flat unobstructed yellow number well; no lines, text, numerals, emblem, blade fuller or highlights crossing the center. Make the central blade as broad as possible for a white stat digit to be overlaid at the exact center of the canvas. A compact stout sword silhouette that reads immediately at 32 pixels, minimal ornamentation, matching the painterly materials of the input. Front facing 2D UI sprite, centered, complete uncut silhouette occupying about 94% of a square canvas, transparent background, no ground shadow. One asset only.

## Red sword

Use case: precise-object-edit. Image 1 is the edit target: the yellow SWORD-SHAPED game stat frame. Image 2 is a color reference only: the existing ruby-red stat socket. Recolor ONLY the golden yellow interior of the sword to the ruby red from image 2 for weapon power. Preserve the exact sword silhouette, orientation, proportions, wide central number well, crossguard, grip, pommel, canvas framing, silver-gray metal trim, brown inner edging and mottled painterly material of image 1. No number, text, insignia or additional object. Same single front-facing sprite, square canvas, true transparent background. Do not redesign anything. The result must match the yellow sword geometry, differing only in red rather than yellow interior.

## Green shield

Use case: precise-object-edit. Edit target/reference is the supplied ruby-red circular game stat socket. Change ONLY the silhouette to a broad classic medieval SHIELD-shaped frame and change the red interior to rich medium emerald GREEN for weapon toughness. Shield has a gently curved almost horizontal upper edge, broad shoulders, near-vertical slightly convex sides and a short centered bottom point. Its silhouette must read as a shield immediately at 32px, distinct from a heart: absolutely NO top notch, no heart lobes. Preserve weathered silver-gray metal outer band, warm brown inner edging, mottled painted texture, restrained lighting and depth of the reference. Broad unobstructed flat green central well so a white number can be overlaid at exact canvas center. No text, numerals, heraldry, emblem, cross, rivets inside or line dividing center. Front-facing 2D fantasy card-game UI sprite, centered complete uncut silhouette, about94% of square canvas, true transparent background, no ground shadow. Single shield frame only.

