#if !UNITY_6000_4_OR_NEWER || UNITY_6000_7_OR_NEWER

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using UnityEditor;
using UnityEditor.Macros;
using UnityEngine;
#if UNITY_6000_3_OR_NEWER
using UnityEngine.Assemblies;
#endif

namespace Unity.ProjectAuditorRules.SettingsModuleAnalyzers
{
    /// <summary>
    /// Reports project settings declared in this package's Rules/ProjectSettings.json.
    /// </summary>
    class ProjectSettingsAnalyzer : SettingsModuleAnalyzer
    {
        const string k_RulesFilePath = "Packages/com.unity.project-auditor-rules/Rules/ProjectSettings.json";

        static readonly HashSet<string> k_UnityAssemblyFileNames = new HashSet<string>(new[] { "UnityEngine.dll", "UnityEditor.dll" }, StringComparer.OrdinalIgnoreCase);

        readonly List<Assembly> m_Assemblies = new List<Assembly>();
        readonly List<ValueTuple<string, string>> m_ProjectSettingsMapping = new List<ValueTuple<string, string>>();

        List<Descriptor> m_Descriptors;

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            AddUnityAssemblies();

            // UnityEditor
            m_ProjectSettingsMapping.Add(new ValueTuple<string, string>("UnityEditor.PlayerSettings", "Project/Player"));
            m_ProjectSettingsMapping.Add(new ValueTuple<string, string>("UnityEditor.Rendering.EditorGraphicsSettings", "Project/Graphics"));

            // UnityEngine
            m_ProjectSettingsMapping.Add(new ValueTuple<string, string>("UnityEngine.Physics2D", "Project/Physics 2D"));
            m_ProjectSettingsMapping.Add(new ValueTuple<string, string>("UnityEngine.Physics", "Project/Physics"));
            m_ProjectSettingsMapping.Add(new ValueTuple<string, string>("UnityEngine.Time", "Project/Time"));
            m_ProjectSettingsMapping.Add(new ValueTuple<string, string>("UnityEngine.QualitySettings", "Project/Quality"));
            m_ProjectSettingsMapping.Add(new ValueTuple<string, string>("UnityEngine.AudioModule", "Project/Audio"));

            m_Descriptors = LoadDescriptors();
            foreach (var descriptor in m_Descriptors)
                registerDescriptor(descriptor);
        }

        public override IEnumerable<ReportItem> Analyze(SettingsAnalysisContext context)
        {
            if (m_Descriptors == null)
                throw new Exception("Descriptors Database not initialized.");

            foreach (var descriptor in m_Descriptors)
            {
                if (!descriptor.IsSupported(context.Params))
                    continue;

                var issue = Evaluate(context, descriptor);
                if (issue != null)
                    yield return issue;
            }
        }

        ReportItem Evaluate(AnalysisContext context, Descriptor descriptor)
        {
            // Evaluate a Unity API static method or property
            var assembly = m_Assemblies.Find(a => a.GetType(descriptor.Type) != null);
            if (assembly == null)
            {
                Debug.LogWarning($"Could not find the assembly declaring {descriptor.Type}.");
                return null;
            }

            var type = assembly.GetType(descriptor.Type);

            var methodName = descriptor.Method;
            var property = type.GetProperty(descriptor.Method);
            if (property != null)
                methodName = "get_" + descriptor.Method;

            var paramTypes = Array.Empty<Type>();
            var args = Array.Empty<object>();

            try
            {
#if UNITY_6000_4_OR_NEWER
                var value = MethodEvaluator.Eval(assembly.GetLoadedAssemblyPath(),
                    descriptor.Type, methodName, paramTypes, args);
#else
                var value = MethodEvaluator.Eval(assembly.Location,
                    descriptor.Type, methodName, paramTypes, args);
#endif

                if (value != null && value.ToString() == descriptor.Value)
                    return NewIssue(context, descriptor, descriptor.Title);
            }
            catch (ArgumentException e)
            {
                Debug.LogWarning($"Could not evaluate {descriptor.Type}.{methodName}. Exception: {e.Message}");
            }

            return null;
        }

        ReportItem NewIssue(AnalysisContext context, Descriptor descriptor, string description)
        {
            var projectWindowPath = m_ProjectSettingsMapping.Find(p => descriptor.Type.StartsWith(p.Item1)).Item2;

            return context.CreateIssue(IssueCategory.ProjectSetting, descriptor.Id, description)
                .WithLocation(projectWindowPath);
        }

        void AddUnityAssemblies()
        {
#if UNITY_6000_4_OR_NEWER
            var assemblies = CurrentAssemblies.GetLoadedAssemblies();
#else
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
#endif
            foreach (var assembly in assemblies)
            {
                if (assembly.IsDynamic)
                    continue;

#if UNITY_6000_4_OR_NEWER
                var path = assembly.GetLoadedAssemblyPath();
#else
                var path = assembly.Location;
#endif
                if (!string.IsNullOrEmpty(path))
                {
                    if (k_UnityAssemblyFileNames.Contains(Path.GetFileName(path)))
                        m_Assemblies.Add(assembly);
                }
            }

            if (m_Assemblies.Count != k_UnityAssemblyFileNames.Count)
                throw new InvalidOperationException($"Could not find all Unity assemblies.");
        }

#pragma warning disable CS0649

        [Serializable]
        sealed class SerializedDescriptor
        {
            public string id;
            public string title;
            public string defaultSeverity;
            public string[] areas;
            public string[] platforms;
            public string description;
            public string recommendation;
            public string documentationUrl;
            public string minimumVersion;
            public string maximumVersion;
            public string type;
            public string method;
            public string returnType;
            public string value;

            internal SerializedDescriptor()
            {
                // only for json serialization purposes.
                type = string.Empty;
                method = string.Empty;
                returnType = string.Empty;
                defaultSeverity = Severity.Default.ToString();
            }
        }

        [Serializable]
        sealed class SerializedDescriptorCollection
        {
            public SerializedDescriptor[] descriptors;
        }

#pragma warning restore CS0649

        static List<Descriptor> LoadDescriptors()
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(k_RulesFilePath);
            if (asset == null)
                throw new Exception("Could not load " + k_RulesFilePath);

            var rawDescriptors = JsonUtility.FromJson<SerializedDescriptorCollection>("{\"descriptors\":" + asset.text + "}").descriptors;
            var descriptors = new List<Descriptor>(rawDescriptors.Length);

            foreach (var rawDescriptor in rawDescriptors)
            {
                if (string.IsNullOrEmpty(rawDescriptor.id))
                    throw new Exception("Descriptor with null id loaded from " + k_RulesFilePath);

                var areas = (Areas)Enum.Parse(typeof(Areas), string.Join(", ", rawDescriptor.areas));

#if UNITY_6000_4_OR_NEWER
                SerializableEnum<BuildTarget>[] platforms = null;
                if (rawDescriptor.platforms != null)
                {
                    platforms = new SerializableEnum<BuildTarget>[rawDescriptor.platforms.Length];
                    for (var i = 0; i < rawDescriptor.platforms.Length; ++i)
                    {
                        platforms[i] = new SerializableEnum<BuildTarget>(
                            (BuildTarget)Enum.Parse(typeof(BuildTarget), rawDescriptor.platforms[i]));
                    }
                }
#else
                BuildTarget[] platforms = null;
                if (rawDescriptor.platforms != null)
                {
                    platforms = new BuildTarget[rawDescriptor.platforms.Length];
                    for (var i = 0; i < rawDescriptor.platforms.Length; ++i)
                    {
                        platforms[i] = 
                            (BuildTarget)Enum.Parse(typeof(BuildTarget), rawDescriptor.platforms[i]);
                    }
                }
#endif

                var desc = new Descriptor(rawDescriptor.id, rawDescriptor.title, areas, rawDescriptor.description,
                    rawDescriptor.recommendation)
                {
                    Type = rawDescriptor.type ?? string.Empty,
                    Method = rawDescriptor.method ?? string.Empty,
                    ReturnType = rawDescriptor.returnType ?? string.Empty,
                    Value = rawDescriptor.value,
                    Platforms = platforms,
                    DefaultSeverity = rawDescriptor.defaultSeverity == Severity.Default.ToString()
                        ? Severity.Moderate
                        : (Severity)Enum.Parse(typeof(Severity), rawDescriptor.defaultSeverity),
                    DocumentationUrl = rawDescriptor.documentationUrl ?? string.Empty,
                    MinimumVersion = rawDescriptor.minimumVersion ?? string.Empty,
                    MaximumVersion = rawDescriptor.maximumVersion ?? string.Empty
                };

                if (string.IsNullOrEmpty(desc.Title))
                {
                    desc.Title = (string.IsNullOrEmpty(desc.Type) || string.IsNullOrEmpty(desc.Method))
                        ? string.Empty
                        : desc.Type + "." + desc.Method;
                }

                descriptors.Add(desc);
            }

            return descriptors;
        }
    }
}

#endif
