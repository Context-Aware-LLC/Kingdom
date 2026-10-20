using System;
using System.Runtime.CompilerServices;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Translates the text a UXML file authored — labels, button captions, tooltips, field
    /// placeholders — by matching each one against the English table (<see cref="Loc.KeyForSource"/>).
    /// One <see cref="Attach"/> per UIDocument root covers the tree it has and every element attached
    /// under it later (cloned rows, opened modals), so a screen needs no per-label wiring.
    ///
    /// <para><b>Only text that still reads as authored English is touched.</b> Text a controller
    /// writes at runtime is left alone unless it happens to equal a table string, and a
    /// controller's own copy goes through <see cref="Loc"/> at the call site. Typed input is never
    /// read: everything inside a text field is skipped.</para>
    ///
    /// <para>Each element remembers the key it matched and the text it wrote, so a language change
    /// re-renders from the key — and a controller that has since overwritten the text keeps its own.</para>
    ///
    /// <para><b>Free in English.</b> Until the player has ever been off the source language there is
    /// nothing to translate and nothing to restore, so every entry point returns at once.</para>
    /// </summary>
    public static class UiLocalizer
    {
        private sealed class Slot { public string Key, Written; }
        private sealed class Keys { public readonly Slot Text = new Slot(), Tooltip = new Slot(), Placeholder = new Slot(); }

        private static readonly ConditionalWeakTable<VisualElement, Keys> Matched =
            new ConditionalWeakTable<VisualElement, Keys>();

        private static readonly ConditionalWeakTable<VisualElement, Action> Redraws =
            new ConditionalWeakTable<VisualElement, Action>();

        private static bool _everTranslated;

        /// <summary>Translates <paramref name="root"/>'s tree now, and anything attached under it later.
        /// Safe to call again on the same root (e.g. from every OnEnable).</summary>
        public static void Attach(VisualElement root)
        {
            if (root == null) return;
            if (!Redraws.TryGetValue(root, out Action redraw))
            {
                redraw = () => Localize(root);
                Redraws.Add(root, redraw);
                root.RegisterCallback<AttachToPanelEvent>(OnAttached, TrickleDown.TrickleDown);
                root.RegisterCallback<DetachFromPanelEvent>(e =>
                {
                    if (e.target == root) Loc.LanguageChanged -= redraw;
                });
            }
            Loc.LanguageChanged -= redraw;
            Loc.LanguageChanged += redraw;
            Localize(root);
        }

        // Every element attached gets its own event, so this handles one element, not its subtree.
        private static void OnAttached(AttachToPanelEvent evt)
        {
            if (Active && evt.target is VisualElement ve) Apply(ve);
        }

        /// <summary>Translates <paramref name="root"/> and its descendants in place.</summary>
        public static void Localize(VisualElement root)
        {
            if (root != null && Active) root.Query<VisualElement>().ForEach(Apply);
        }

        private static bool Active
        {
            get
            {
                if (!Loc.IsSource) _everTranslated = true;
                return _everTranslated;
            }
        }

        private static void Apply(VisualElement e)
        {
            if (e is TextField || e.GetFirstAncestorOfType<TextField>() != null)
            {
                if (e is TextField field && !string.IsNullOrEmpty(field.textEdition.placeholder))
                    field.textEdition.placeholder = Resolve(field.textEdition.placeholder, KeysOf(e).Placeholder);
                return;
            }
            if (e is TextElement te && !string.IsNullOrEmpty(te.text))
                te.text = Resolve(te.text, KeysOf(e).Text);
            if (!string.IsNullOrEmpty(e.tooltip))
                e.tooltip = Resolve(e.tooltip, KeysOf(e).Tooltip);
        }

        private static Keys KeysOf(VisualElement e) => Matched.GetValue(e, _ => new Keys());

        private static string Resolve(string current, Slot slot)
        {
            // Text we did not write was set by a controller since; match it afresh.
            if (slot.Key == null || current != slot.Written) slot.Key = Loc.KeyForSource(current);
            if (slot.Key == null) return current;
            slot.Written = Loc.Get(slot.Key);
            return slot.Written;
        }
    }
}
