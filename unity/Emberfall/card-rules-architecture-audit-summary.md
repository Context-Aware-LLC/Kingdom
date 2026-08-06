# Emberfall Card-Rules Architecture Audit Summary

## Verdict

The audit's diagnosis is useful, but its proposed architecture should not be executed as written. It identifies real symptoms, then recommends abstractions that preserve—or deepen—the underlying constraints. The proposal requires major revision.

## Blocking findings

### 1. It misses the weapon identity problem

On play, `CommandProcessor.cs:374-386` moves the original weapon card into the graveyard, then creates a second `WeaponInstance` with a new ID. Attack and death triggers use that synthetic identity.

Yet `CardInstance` already has `CurrentDurability`, `Keywords`, `Enchantments`, and the enum already has `Zone.Weapon`. The audit recommends copying more of `CardInstance` into `WeaponInstance` (`claude_audit.md:56`) instead of investigating whether an equipped weapon should simply remain the same card instance.

That matters for traditional mechanics: bounce, steal, copy, transform, silence, resurrection, source-based enchantments, and graveyard queries all become ambiguous when one physical card exists as both a graveyard card and an equipped synthetic entity.

The pre-launch direction worth testing is `PlayerState.Weapon: CardInstance?`, preserving the original ID and moving it to `Zone.Weapon`. That could eliminate much of the seven-file duplicate shape at the same time.

### 2. The proposed target model cannot express gaps the audit itself identifies

The suggested `{Scope, Filter, Choice}` model only offers `AUTO|PLAYER_CHOSEN` (`claude_audit.md:57`). It cannot cleanly represent:

- random selection;
- `TRIGGER_SOURCE`;
- adjacency;
- named target bindings;
- selecting N targets;
- distinct or repeated targets;
- cards in hand, deck, or graveyard;
- previous-result references.

The audit explicitly identifies random selection and multi-effect bindings as missing, then omits both from what it calls “the whole of what's needed” (`claude_audit.md:51-60`). Its final conclusion therefore does not follow from its own findings.

### 3. The engine's source model is at least as cramped as its target model, but the plan ignores it

`QueuedEffect` and `EffectContext` require a `CardInstance Source`, so weapons and hero powers are represented using throwaway `CardInstance` objects. `DamagePipeline` appears source-agnostic, but Lifedrain recovers the controller by looking the source ID up through `Characters.TryFind`. That lookup only sees heroes and board minions, so spell-like or ephemeral sources cannot participate generally.

A future-oriented rules model needs an explicit effect-source context—identity, controller, definition, source kind, and current carrier—plus trigger-event context. Boolean condition composition over only `TURN_OWNER` does not solve this.

### 4. One reachability table would combine fundamentally different rules

The document treats these as one relation (`claude_audit.md:58`):

- whether a card type receives a trigger;
- whether a keyword is meaningful on a carrier;
- whether an action can affect a target;
- whether designers permit a mechanic for balance reasons.

Those are not interchangeable. In particular, PR #48's rejection of `GAIN_ARMOR` on hero powers is balance policy, not execution reachability. Hero powers already cost two mana and are limited to once per turn; gaining armor through one is also a thoroughly traditional TCG mechanic. Hard-rejecting it directly conflicts with the “more flexible and less restrictive” criterion.

These rules can live behind one deep mechanics-validation module, but its interface should preserve distinct typed questions such as trigger-source support, action-target capability, and keyword-carrier support. Balance policy belongs in a separate lint or content rule.

### 5. Adding Weapon to `CharacterRef` is neither safe nor small

`CharacterRef` explicitly represents damageable characters. Several paths depend on its binary nature:

- `CharacterRef.Id` chooses Hero or Minion.
- `DestroyHandler` uses `if minion, else hero`.
- `FreezeHandler`, healing, and damage contain the same assumption.

A weapon case would turn existing `else Hero!` branches into null dereferences unless every consumer were audited. A broader `EntityRef` or `TargetableRef` should represent selectable objects, while `CharacterRef` remains the narrower interface accepted by damage and healing.

## Important corrections

- **A schema-version byte does not solve the seven-touch cost.** The seven sites are real for a new `WeaponInstance` field, but several are deliberate safety classifications. A version value neither updates those sites nor detects a forgotten field. The wire envelope already has `ProtocolCodec.Version = 1`, plus an engine contract version. Canonical serialization is primarily a hash walk, while snapshots are named JSON. The audit conflates hash compatibility, wire compatibility, and schema maintenance.

- **Rules-to-text does not by itself close the Mainspring defect.** Its effect and `cardText` already agree; the drifting third representation is the decorative `DEATHRATTLE` keyword. The fix is to derive both text and presentation markers from the normalized rule representation—or eliminate marker keywords—not merely generate `cardText`.

- **The selector counts are wrong.** At the audited commit there are 13 schema-valid selectors: 11 active and two unimplemented, not “twelve” or “10 active.” Also, C# enums are not automatically exhaustive; that requires an analyzer, explicit tests, or a fail-closed default.

- **The nine-PR statistic is inflated.** It counts #49 and its closed duplicate #50 separately and includes #59, whose own PR describes itself as an aura prerequisite rather than signature-weapon work. It demonstrates activity, not that the weapon request caused nine independent costs.

- **“Merge as-is” is too strong.** As checked, PR #48 is conflicting with master and PR #60 still has a failing required gate. The failure may indeed be flaky, but an unrelated occurrence does not prove this occurrence harmless. Rebase and rerun or repair the gate.

- **Skipping unknown events is unsafe and names the wrong codec.** Event parsing happens in `ProtocolCodec.TryReadEvent`, not `SnapshotJsonCodec`. Silently skipping an ordered event can violate sequence assumptions. Preserve it as an unknown event, negotiate protocol compatibility, or force resynchronization.

## Smallest direction I would approve

1. Define an executable acceptance corpus of representative future mechanics: weapon bounce/steal, random target, trigger source, adjacent target, two independent targets, graveyard selection, aura, conditional effect, and Discover.
2. Preserve weapon identity by testing a migration from `WeaponInstance` to the existing `CardInstance`/`Zone.Weapon` shape.
3. Introduce a deep targeting module whose small interface owns candidate enumeration, chosen-target validation, deterministic/random resolution, bindings, and ordering. Engine and Unity should call the same module.
4. Normalize rules into explicit, action-specific payload types. The current flat `EffectDef` grab-bag is a larger future constraint than the audit's proposed condition tree.
5. Generate card text and display markers from that normalized representation.
6. Keep trigger support, carrier support, target capability, and balance policy separate and typed.
7. Add versioning only after defining an actual compatibility requirement; do not treat it as a maintenance-cost fix.

The codebase-design lens materially changes the conclusion here: moving several switches into one large matrix does not create a deep module. A real targeting/mechanics seam should let validation, runtime resolution, authoring, text generation, and UI affordances gain behavior through one small interface.

## Audit context

- Source audit: `C:\Users\User\Desktop\claude_audit.md`
- Audited source commit: `origin/master @ 2d922b23dd4802d38fdbcf43829604822464ee5c`
- This document recovers the Codex verdict delivered at the end of the August 3, 2026 session “Define adversarial review context.”
- No code or branches were modified during that audit session.
