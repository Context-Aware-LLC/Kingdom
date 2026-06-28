# Validation & Import Pipeline

**Owner:** Editor Tooling
**Status:** v1.0 — M1 Vertical Slice baseline
**Binds to:** `interfaces.md §4` (`ICardImporter`, `ICardValidator`, `ValidationReport`, `ValidationIssue`), `card-system.md §2` (JSON↔SO bake), `card-system.md §5` (validator rule set), `card-system.md §7` (`requiresEngineeringPrimitive` gate), `design/card-schema.md` (D1 §2.1, §4–6), `design/balance-guardrails.md` (stat budgets §‹budget›, aura caps §5, audit gate §7)
**Assembly:** `Emberfall.Editor` (bake/import + CI batch). `ICardValidator` impl lives in `Emberfall.Core.Cards` (UnityEngine-free) so it runs headless on the server too.
**Mitigates:** R3 (bad data caught before it ships) and reinforces R2 (data-only balance with guardrails).

> **Key architectural point:** the *validator* is engine-side and shared (`card-system.md §5`: "runs in your editor window AND at server/client load"). Editor Tooling does **not** own a second validator. We own the **import pipeline** (`ScriptedImporter`/postprocessor + bake) and the **batch report + CI gate** that drive the shared `ICardValidator` over the whole library. Where a rule below is balance-budget/art-integrity specific (not core legality), it lives in an **editor-side `ICardValidator` extension** layered on top of the core validator — never forking it.

---

## 1. Two validation layers, one report shape

| Layer | Where it runs | Owner | Rules |
|---|---|---|---|
| **Core legality** (`ICardValidator`) | editor + server + client load | Architect (`Emberfall.Core.Cards`) | §3 rules C1–C20: schema/type/grammar/token-ref/`requiresEngineeringPrimitive`. UnityEngine-free. |
| **Editor/pipeline checks** | editor + CI only | Editor Tooling | §4 rules E1–E10: stat-budget warnings, art-Addressable integrity, set/Addressable hygiene, naming. Needs the AssetDatabase/Addressables, so editor-only. |

Both emit the **same `ValidationReport`** shape (`interfaces.md §4`): `Ok`, `IReadOnlyList<ValidationIssue>` (severity + field + message), `RequiresEngineeringPrimitive`. The editor checks compose by wrapping the core report:

```csharp
// Emberfall.Editor — layered, does NOT reimplement core legality.
public sealed class EditorCardValidator : ICardValidator
{
    private readonly ICardValidator _core;          // injected core validator
    private readonly IAddressableArtCatalog _art;   // artId resolution
    private readonly IBalanceGuardrails _budget;    // balance-guardrails numbers
    public ValidationReport Validate(CardDefinition def, ICardDatabase db)
    {
        var core = _core.Validate(def, db);         // C1–C20 (authoritative)
        var extra = RunEditorChecks(def, db);       // E1–E10 (warnings + art errors)
        return Merge(core, extra);                  // preserves RequiresEngineeringPrimitive
    }
}
```

CI and the server both call the **core** `ICardValidator` (no Unity); the editor window and the batch report call `EditorCardValidator` to add the art/budget layer.

---

## 2. Import pipeline — ScriptedImporter over AssetPostprocessor

**Decision: a `ScriptedImporter` keyed to `.card.json`** (not a generic `AssetPostprocessor`). Justification:

- The bake is a true *import* (one source file `cards/<setId>/<id>.card.json` → one `CardDefinitionSO` artifact). `ScriptedImporter` models exactly this 1:1 source→artifact relationship, gives correct dependency tracking + reimport-on-change, and keeps the generated SO out of source control (it's a build artifact).
- It is **idempotent** by construction (reimporting the same JSON yields the same SO) — satisfies the postprocessor idempotency rule.
- An `AssetPostprocessor` still has a small role: `OnPostprocessAllAssets` assigns/repairs the Addressable group + label after import (§6 of `pipeline-automation.md`), since Addressable entries aren't part of the importer's artifact.

```csharp
[ScriptedImporter(version: 3, ext: "card.json")]
public sealed class CardScriptedImporter : ScriptedImporter
{
    public override void OnImportAsset(AssetImportContext ctx)
    {
        var json = File.ReadAllText(ctx.assetPath);
        var def  = CardJson.Deserialize(json);                  // shared parser
        var report = _validator.Validate(def, _jsonDb);

        if (report.RequiresEngineeringPrimitive)                // hard gate
            { ctx.LogImportError($"[{def.Id}] requiresEngineeringPrimitive — cannot bake data-only. See guardrails §7."); return; }
        foreach (var i in report.Issues)
            if (i.Severity == Error) ctx.LogImportError($"[{def.Id}] {i.Field}: {i.Message}");
            else                     ctx.LogImportWarning($"[{def.Id}] {i.Field}: {i.Message}");
        if (!report.Ok) return;                                 // do not produce a broken SO

        var so = ScriptableObject.CreateInstance<CardDefinitionSO>();
        so.SetCanonicalJson(json);                              // stores JSON verbatim (card-system §1.2)
        ctx.AddObjectToAsset("card", so);
        ctx.SetMainObject(so);
    }
}
```

This **binds to** `ICardImporter` — `CardImporter.Import(jsonPath)` triggers `AssetDatabase.ImportAsset` on the path (driving `CardScriptedImporter`); `CardImporter.ImportSet(setId)` batches the set's files inside `StartAssetEditing/StopAssetEditing`. The importer interface is the public seam; the `ScriptedImporter` is its implementation.

> **Rename note:** files are `.card.json` so the `ScriptedImporter` extension is unambiguous and plain config JSON elsewhere isn't swept in. This is a tooling convention on top of D1's `cards/<setId>/<id>.json` and should be confirmed with the Architect (see §8 clarifications).

---

## 3. Core legality rules (C-series) — mirrors `card-system.md §5` / D1 §2.1

These are the authoritative `ICardValidator` checks (Architect-owned; listed here so the tool surfaces them and CI gates on them).

| # | Rule | Severity | Field |
|---|---|---|---|
| C1 | `id` matches `^[a-z0-9_]+$` | error | id |
| C2 | `id` unique across the library | error | id |
| C3 | `name` 1–32 chars, non-empty | error | name |
| C4 | `MINION`: `attack`≥0 and `health`≥1 present; `durability` null | error | stats |
| C5 | `SPELL`: `attack`/`health`/`durability` all null; has ≥1 effect OR `requiresEngineeringPrimitive` | error | stats/effects |
| C6 | `WEAPON`: `attack`≥0 and `durability`≥1 present; `health` null | error | stats |
| C7 | `cost` in 0–25 | error | cost |
| C8 | numeric stats within D1 ranges (`attack` 0–30, `health`/`durability` per type) | error | stats |
| C9 | every `EffectDef.action` is a known `Action` verb | error | effects[i].action |
| C10 | required `amount` present for verbs that use it (`DEAL_DAMAGE`,`HEAL`,`DRAW`,`SUMMON`,`BUFF`,`GAIN_MANA`,`DISCOVER`,`RESTORE_DURABILITY`,`SET_STAT`,`ADD_TO_HAND`) | error | effects[i].amount |
| C11 | required `payload` present + well-typed (`SUMMON`/`ADD_TO_HAND`/`TRANSFORM`/`EQUIP`→cardId; `GIVE_KEYWORD`→keyword; `DISCOVER`→pool; `GAIN_MANA`→permanent) | error | effects[i].payload |
| C12 | every payload card-id (`tokenId`/`cardId`/`pool[]`) resolves in `db` | error | effects[i].payload |
| C13 | summoned tokens are `collectible:false` where appropriate | warn | effects[i].payload |
| C14 | `TargetSpec.selector` is a valid `Selector`; `filter` sub-fields valid enums/ints | error | effects[i].target |
| C15 | `chosenBy:PLAYER` only where the selector admits a player-chosen target | error | effects[i].target.chosenBy |
| C16 | `count` is ≥1 or `-1` (all) | error | effects[i].target.count |
| C17 | `vfxEvent` override (if present) is in the D7 closed vocabulary | error | effects[i].vfxEvent |
| C18 | every `Keyword` in `keywords[]` is a known enum value | error | keywords |
| C19 | `setId`, `class`, `rarity`, `tribe`, `type` are valid enum values | error | (field) |
| C20 | **`requiresEngineeringPrimitive` gate:** any effect that can't compose from existing verbs ⇒ must be `true`; a `true` value sets `report.RequiresEngineeringPrimitive` and **blocks data-only bake** | error/gate | requiresEngineeringPrimitive |

---

## 4. Editor/pipeline rules (E-series) — Editor Tooling-owned layer

These need the AssetDatabase / Addressables / balance numbers, so they live in `EditorCardValidator`.

| # | Rule | Severity | Source |
|---|---|---|---|
| E1 | **Art integrity:** `artId` resolves to an entry in the art Addressable group (key `artId`) | **error** | `card-system.md §6` (art group), D1 §2 (`artId` FK) |
| E2 | `artId` matches `^[a-z0-9_]+$` and is non-empty | error | D1 §2 |
| E3 | **Stat-budget warning:** minion `(attack+health)` vs cost-curve baseline from `balance-guardrails.md`; flag over/under-statted vanilla minions | **warn** | guardrails stat budget |
| E4 | **Spell-value warning:** `DEAL_DAMAGE`/`HEAL`/`DRAW` magnitude vs cost vs guardrail spell-value table | warn | guardrails |
| E5 | **Aura-cap warning:** cards that mass-grant persistent auras (`auraShield/auraStealth/auraGlow/auraTaunt` via `GIVE_KEYWORD`/`SET`) flagged against the D8 simultaneous-VFX budget | warn | guardrails §5, D1 §6 |
| E6 | `cardText` token consistency: `$dmg`/`$heal` tokens have a matching effect amount; `[KEYWORD]` tokens reference real keywords on the card | warn | D1 §2 (cardText tokens) |
| E7 | `cardText` ≤140, `flavorText` ≤160 | error | D1 §2 |
| E8 | Addressable hygiene: baked SO address == `card/<setId>/<id>`, label == `set:<setId>` | error | `card-system.md §6` |
| E9 | File-path hygiene: JSON path `cards/<setId>/<id>.card.json` and `setId`/`id` match the path | warn | D1 §1 |
| E10 | Rarity/collectible sanity: `LEGENDARY` and `collectible:false` token combination flagged (likely authoring slip) | warn | D2/D1 |

E3–E5 are **warnings, not blockers** — balance is iterative (R2) and shouldn't gate a save, but they must be *visible* in the editor and the report. E1/E2/E7/E8 are **errors** (a missing art reference or bad address is a shipped bug).

---

## 5. The `requiresEngineeringPrimitive` audit gate

Per `card-system.md §7` and guardrails §7, this is the disciplined escape hatch — honored identically in three places:

1. **Editor window:** baking is refused; banner routes to Shepherd+Eng (`card-authoring-tool.md §6.3`).
2. **ScriptedImporter:** `LogImportError` and no SO produced (§2 above) — the card cannot exist as a baked asset.
3. **CI batch (§7):** any `report.RequiresEngineeringPrimitive == true` fails the build with a dedicated exit code so it's distinguishable from an ordinary validation error in the gate dashboard.

The gate is never silently overridable in tooling; clearing it is an engineering action (add a reusable `IActionHandler`/`IKeywordRule`, extend the enum) per `card-system.md §7` steps 3–4.

---

## 6. Batch "validate entire card set" report

`CardSetValidator` (menu `Tools/Emberfall/Validate All Cards`, and a CLI entry point for CI):

1. Build a `JsonCardDatabase` over `cards/**/*.card.json` (so cross-references resolve as on the server).
2. For each card: `EditorCardValidator.Validate(def, db)`.
3. Aggregate into a `CardSetReport`:
   - counts: total, clean, warnings, errors, `requiresEngineeringPrimitive`.
   - per-card rows: id, set, severity, issue list.
4. Emit in two formats:
   - **Console / window panel** (grouped by severity, click-to-select the offending asset).
   - **CSV + JUnit XML artifacts** (`card-validation-report.csv`, `card-validation.xml`) for CI dashboards — texture-budget-auditor-style CSV of every violation.

```csharp
public readonly struct CardSetReport
{
    public readonly int Total, Clean, Warnings, Errors, NeedsPrimitive;
    public readonly IReadOnlyList<ValidationReport> PerCard;
    public bool GateOk => Errors == 0 && NeedsPrimitive == 0;   // warnings don't fail CI
}
```

---

## 7. CI gate (`-batchmode -executeMethod`)

Headless entry point for GitHub Actions / Unity license runner (`PROJECT-PLAN` build/CI):

```csharp
public static class CardCI
{
    // unity -batchmode -quit -executeMethod Emberfall.Editor.CardCI.ValidateAll
    public static void ValidateAll()
    {
        var report = CardSetValidator.RunHeadless();
        File.WriteAllText("artifacts/card-validation-report.csv", report.ToCsv());
        File.WriteAllText("artifacts/card-validation.xml", report.ToJUnit());
        if (report.NeedsPrimitive > 0) EditorApplication.Exit(3);   // distinct: needs eng primitive
        if (report.Errors > 0)         EditorApplication.Exit(2);   // validation errors
        EditorApplication.Exit(0);                                  // warnings allowed
    }
}
```

- Runs on every PR touching `cards/**` (and a server-side mirror runs the **core** `ICardValidator` only, with no Unity, as a second cheap gate).
- Exit codes: `0` pass (warnings ok), `2` validation errors, `3` `requiresEngineeringPrimitive` present. CI maps `3` to the Shepherd+Eng review label automatically.
- This is the structural promise of R3: a card with a missing art reference, an invalid effect, or an unresolved token **cannot merge**.

---

## 8. Seams where I need Architect confirmation / a small addition

1. **`ValidationIssue` shape.** `interfaces.md §4` states issues carry "error/warn + field + message". I'm assuming a `Severity { Error, Warning }` enum and a string `Field` path (e.g. `effects[2].payload.tokenId`). Please confirm the exact `ValidationIssue` field names so the editor and CSV bind to them. **(needed for E-series + CSV)**
2. **`CardDefinitionSO.SetCanonicalJson(...)`.** The SO in `card-system.md §1.2` shows a private `_json` + `ToDefinition()`, but no setter. The `ScriptedImporter` needs an editor-writable way to store the verbatim JSON. Request: an Architect-blessed editor-only setter (e.g. `internal void SetCanonicalJson(string)` exposed to `Emberfall.Data`/`Emberfall.Editor`) so the bake doesn't reach into serialized fields by reflection.
3. **Validator extensibility.** Is `ICardValidator` intended to be **composable** (so `EditorCardValidator` can wrap the core impl), or should art/budget checks be a *separate* `IContentLinter` interface so the core validator stays purely about legality? I've assumed composition with a merged `ValidationReport`; if you'd prefer a separate linter seam for the E-series, that's a one-line interface add and I'll bind to it instead. **(this is the main design question)**
4. **Art catalog query.** E1 needs to resolve `artId` → Addressable entry without loading the sprite. Confirm there's an editor-time `IAddressableArtCatalog` (or that I should read the Addressables settings group directly). If Tech Art owns the art group, I need a read-only lookup seam from them.
5. **Balance guardrail numbers.** E3–E5 reference `balance-guardrails.md` stat/spell-value tables and aura caps. Those sections are referenced but I haven't been given the concrete numbers — I need the Game Designer's finalized budget tables (or a small `IBalanceGuardrails` provider) to implement the warning thresholds. Until then E3–E5 ship as stubs that log "budget data pending."

Nothing above contradicts the interface contracts — items 1–2 are additive clarifications, item 3 is a yes/no on layering vs. a new seam.
