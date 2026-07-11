using Emberfall.Core.Cards;
using Emberfall.Data;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Read-only inspector for the baked artifact (card-authoring-tool.md §7). There is deliberately
    /// no field-by-field editable inspector — editing the SO directly would drift from the canonical
    /// JSON, so this closes that loophole and routes edits back to the authoring window.
    /// </summary>
    [CustomEditor(typeof(CardDefinitionSO))]
    public sealed class CardDefinitionSOEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var so = (CardDefinitionSO)target;
            EditorGUILayout.HelpBox(
                "Generated from the canonical card JSON. Edit via Emberfall ▸ Card Authoring Tool.",
                MessageType.Info);

            CardDefinition def;
            try { def = so.ToDefinition(); }
            catch (System.Exception ex)
            {
                EditorGUILayout.HelpBox($"Failed to parse baked JSON: {ex.Message}", MessageType.Error);
                return;
            }

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("id", def.Id);
                EditorGUILayout.TextField("name", def.Name);
                EditorGUILayout.TextField("type", def.Type.ToString());
                EditorGUILayout.IntField("cost", def.Cost);
                if (def.Attack.HasValue) EditorGUILayout.IntField("attack", def.Attack.Value);
                if (def.Health.HasValue) EditorGUILayout.IntField("health", def.Health.Value);
                if (def.Durability.HasValue) EditorGUILayout.IntField("durability", def.Durability.Value);
                EditorGUILayout.TextField("keywords", string.Join(", ", def.Keywords));
                EditorGUILayout.IntField("effect count", def.Effects.Count);
            }

            if (GUILayout.Button("Open in Card Authoring Tool"))
                CardAuthoringWindow.Open();
        }
    }
}
