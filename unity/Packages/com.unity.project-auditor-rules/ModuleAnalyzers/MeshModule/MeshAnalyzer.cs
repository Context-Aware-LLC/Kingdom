using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using UnityEditor;
using UnityEngine.Rendering;

namespace Unity.ProjectAuditorRules.MeshModuleAnalyzers
{
    class MeshAnalyzer : MeshModuleAnalyzer
    {
        internal const string PAA1000 = nameof(PAA1000);
        internal const string PAA1001 = nameof(PAA1001);
        internal const string PAA1002 = nameof(PAA1002);
        internal const string PAA1003 = nameof(PAA1003);

        internal static readonly Descriptor k_MeshReadWriteEnabledDescriptor = new Descriptor(
            PAA1000,
            "Mesh: Read/Write enabled",
            Areas.Memory,
            "The <b>Read/Write Enabled</b> flag in the Model Import Settings is enabled. This causes the mesh data to be duplicated in memory.",
            "If not required, disable the <b>Read/Write Enabled</b> option in the Model Import Settings."
        )
        {
            MessageFormat = "Mesh '{0}' Read/Write is enabled",
            DocumentationUrl = "https://docs.unity3d.com/Manual/FBXImporter-Model.html",
            Fixer = (issue, analysisParams) =>
            {
                var modelImporter = AssetImporter.GetAtPath(issue.RelativePath) as ModelImporter;
                if (modelImporter != null)
                {
                    modelImporter.isReadable = false;
                    modelImporter.SaveAndReimport();
                    return true;
                }

                return false;
            }
        };

        internal static readonly Descriptor k_Mesh32BitIndexFormatUsedDescriptor = new Descriptor(
            PAA1001,
            "Mesh: Index Format is 32 bits",
            Areas.Memory,
            "The <b>Index Format</b> in the Model Import Settings is set to <b>32 bit</b>, but the model does not have enough vertices to require 32 bit indices. This increases the mesh size and may not work on certain mobile devices.",
            "Consider using changing the <b>Index Format</b> option in the Model Import Settings. This should be set to either <b>16 bits</b> or <b>Auto</b>."
        )
        {
            MessageFormat = "Mesh '{0}' Index Format is 32 bits",
            DocumentationUrl = "https://docs.unity3d.com/Manual/FBXImporter-Model.html",
            Fixer = (issue, analysisParams) =>
            {
                var modelImporter = AssetImporter.GetAtPath(issue.RelativePath) as ModelImporter;
                if (modelImporter != null)
                {
                    modelImporter.indexFormat = ModelImporterIndexFormat.Auto;
                    modelImporter.SaveAndReimport();
                    return true;
                }

                return false;
            }
        };

        internal static readonly Descriptor k_Mesh32BitIndexFormatUsedNoImporterDescriptor = new Descriptor(
            PAA1003,
            "Mesh: Index Format is 32 bits",
            Areas.Memory,
            "The mesh's <b>Index Format</b> is <b>32 bit</b>, but the mesh does not have enough vertices to require 32 bit indices. This increases the mesh size and may not work on certain mobile devices.",
            "Rebuild the mesh with a <b>16 bit</b> Index Format, either via script or by using the Quick Fix button."
        )
        {
            MessageFormat = "Mesh '{0}' Index Format is 32 bits",
            DocumentationUrl = "https://docs.unity3d.com/ScriptReference/Mesh-indexFormat.html",
            Fixer = (issue, analysisParams) =>
            {
                return SetMeshTo16BitIndexFormat(issue.RelativePath);
            }
        };

        internal static readonly Descriptor k_MeshReadWriteEnabledNoImporterDescriptor = new Descriptor(
            PAA1002,
            "Mesh: Read/Write enabled",
            Areas.Memory,
            "The <b>Read/Write Enabled</b> flag is enabled. This causes the mesh data to be duplicated in memory.",
            "If not required, disable the <b>Read/Write Enabled</b> option via script or by using the Quick Fix button."
        )
        {
            MessageFormat = "Mesh '{0}' Read/Write is enabled",
            DocumentationUrl = "https://docs.unity3d.com/ScriptReference/Mesh-isReadable.html",
            Fixer = (issue, analysisParams) =>
            {
                var model = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(issue.RelativePath);
                if (model != null)
                {
                    using (var s = new SerializedObject(model))
                    {
                        s.UpdateIfRequiredOrScript();

                        SerializedProperty prop = s.FindProperty("m_IsReadable");
                        if (prop != null)
                        {
                            prop.boolValue = false;
                            s.ApplyModifiedProperties();
                            AssetDatabase.SaveAssetIfDirty(model);
                            return true;
                        }
                    }
                }

                return false;
            }
        };

        // TODO: Uncomment these when it's time to re-implement vertex/triangle count analysis.
        // [DiagnosticParameter("MeshVertexCountLimit", 5000)]
        // int m_VertexCountLimit;
        //
        // [DiagnosticParameter("MeshTriangleCountLimit", 5000)]
        // int m_TriangleCountLimit;

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_MeshReadWriteEnabledDescriptor);
            registerDescriptor(k_Mesh32BitIndexFormatUsedDescriptor);
            registerDescriptor(k_MeshReadWriteEnabledNoImporterDescriptor);
            registerDescriptor(k_Mesh32BitIndexFormatUsedNoImporterDescriptor);
        }

        public override IEnumerable<ReportItem> Analyze(MeshAnalysisContext context)
        {
            var mesh = context.Mesh;
            var modelImporter = context.Importer as ModelImporter;

            // Read/write
            if (mesh.isReadable)
            {
                var id = (modelImporter != null) ? k_MeshReadWriteEnabledDescriptor.Id : k_MeshReadWriteEnabledNoImporterDescriptor.Id;

                yield return context.CreateIssue(IssueCategory.AssetIssue, id, context.Name)
                    .WithLocation(context.Importer?.assetPath ?? AssetDatabase.GetAssetPath(mesh));
            }

            // 32bit indices
            if (mesh.indexFormat == IndexFormat.UInt32 &&
                mesh.vertexCount <= 65535)
            {
                var id = (modelImporter != null) ? k_Mesh32BitIndexFormatUsedDescriptor.Id : k_Mesh32BitIndexFormatUsedNoImporterDescriptor.Id;

                yield return context.CreateIssue(IssueCategory.AssetIssue, id, context.Name)
                    .WithLocation(context.Importer?.assetPath ?? AssetDatabase.GetAssetPath(mesh));
            }
        }

        static bool SetMeshTo16BitIndexFormat(string path)
        {
            var mesh = AssetDatabase.LoadAssetAtPath<UnityEngine.Mesh>(path);
            if (mesh == null || mesh.indexFormat != IndexFormat.UInt32)
                return false;

            // The 32-bit index buffer stores 4 bytes per index, so we can't just flip the format flag: we have to
            // read the existing indices and re-write them once the buffer is 16 bit. Reading requires the mesh to
            // be CPU-readable.
            if (!mesh.isReadable)
                return false;

            var subMeshCount = mesh.subMeshCount;
            var indices = new int[subMeshCount][];
            var topologies = new UnityEngine.MeshTopology[subMeshCount];
            for (var i = 0; i < subMeshCount; i++)
            {
                indices[i] = mesh.GetIndices(i);
                topologies[i] = mesh.GetTopology(i);
            }

            mesh.indexFormat = IndexFormat.UInt16;
            for (var i = 0; i < subMeshCount; i++)
                mesh.SetIndices(indices[i], topologies[i], i);

            AssetDatabase.SaveAssetIfDirty(mesh);
            return true;
        }
    }
}
