using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Profile;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.Services.CloudBuild.Editor.Components
{
    class BuildAutomationCredentialsConfig : VisualElement
    {
        const string k_Uxml = "Packages/com.unity.services.cloud-build/Editor/UXML/BuildAutomationCredentialsConfig.uxml";

        const string k_CredentialsDropDownLabel = "credentials-dropdown";
        const string k_CredentialsBindingProperty = "m_CredentialsId";
        const string k_CredentialsHelpBoxLabel = "credentials-help-box";
        const string k_CredentialsToggle = "credentials-toggle";
        const string k_NewCredentialsButtonLabel = "new-credentials-button";
        const string k_RefreshCredentialsButtonLabel = "refresh-credentials-button";
        const string k_SignBuildInfoButtonLabel = "sign-build-info-button";
        const int k_DaysToExpirationWarning = 30;
        const int k_DaysToExpirationError = 0;

        readonly BuildAutomationApiClient m_ApiClient;
        readonly BuildAutomationSettings m_BuildAutomationSettings;
        readonly bool m_CredentialsRequired;
        readonly bool m_CredentialsSupported;

        Dictionary<string, GetCredentialsResponse> m_CredentialsMap;
        DropdownField m_CredentialsDropDown;
        HelpBox m_CredentialsHelpBox;
        Toggle m_CredentialsToggle;
        Button m_NewCredentialsButton;
        Button m_RefreshCredentialsButton;

        public BuildAutomationCredentialsConfig(BuildAutomationApiClient mApiClient, SerializedObject serializedObject)
        {
            if (serializedObject.targetObject is not BuildAutomationSettings buildAutomationSettings)
                throw new InvalidOperationException("Editor object is not of type BuildAutomationSettings.");

            m_ApiClient = mApiClient;
            m_BuildAutomationSettings = buildAutomationSettings;

            m_CredentialsSupported = BuildAutomationUtilities.GetCredentialPlatform(m_BuildAutomationSettings.buildTarget) != "unknown";
            m_CredentialsRequired = BuildAutomationUtilities.TargetRequiresCredentials(m_BuildAutomationSettings.buildTarget);

            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(k_Uxml);
            visualTree.CloneTree(this);

            InitializeVisualElements(this, serializedObject);
        }

        void InitializeVisualElements(VisualElement root, SerializedObject serializedObject)
        {
            m_CredentialsDropDown = root.Q<DropdownField>(k_CredentialsDropDownLabel);
            m_CredentialsHelpBox = root.Q<HelpBox>(k_CredentialsHelpBoxLabel);
            m_CredentialsToggle = root.Q<Toggle>(k_CredentialsToggle);
            m_CredentialsToggle.RegisterValueChangedCallback(OnSigningToggled);
            m_CredentialsDropDown.BindProperty(serializedObject.FindProperty(k_CredentialsBindingProperty));
            m_CredentialsDropDown.RegisterValueChangedCallback(OnCredentialsValueChanged);
            m_CredentialsDropDown.formatSelectedValueCallback = FormatCredentialsSelected;
            m_CredentialsDropDown.formatListItemCallback = FormatCredentialsList;

            m_NewCredentialsButton = root.Q<Button>(k_NewCredentialsButtonLabel);
            m_NewCredentialsButton.clicked += OnNewCredentialsButtonClicked;
            m_RefreshCredentialsButton = root.Q<Button>(k_RefreshCredentialsButtonLabel);
            m_RefreshCredentialsButton.clicked += OnRefreshCredentialsButtonClicked;

            // Add a button that links to the UBA signing docs
            var signBuildInfoButton = root.Q<Button>(k_SignBuildInfoButtonLabel);
            var helpIcon = EditorGUIUtility.IconContent("_Help");
            signBuildInfoButton.style.width = 18;
            signBuildInfoButton.style.height = 18;
            signBuildInfoButton.style.alignItems = new StyleEnum<Align>(Align.Center);
            var helpButtonImage = new Image {
                style = {
                    backgroundImage = new StyleBackground(helpIcon.image as Texture2D),
                    width = 16,
                    height = 16,
                }
            };
            signBuildInfoButton.Add(helpButtonImage);
            signBuildInfoButton.tooltip = "View documentation about the build signing process";
            signBuildInfoButton.clicked += () =>
            {
                var url = BuildAutomationDashboardUrls.GetBuildSigningUrl();
                Application.OpenURL(url);
            };

            if (m_CredentialsRequired)
            {
                m_CredentialsToggle.value = true;
                m_CredentialsToggle.enabledSelf = false;
            }

            m_CredentialsDropDown.enabledSelf = m_CredentialsToggle.value;
        }

        public async Task FetchCredentials(CancellationToken cancellationToken)
        {
            if (!m_CredentialsSupported)
            {
                return;
            }
            try
            {
                var platform = BuildAutomationUtilities.GetCredentialPlatform(m_BuildAutomationSettings.buildTarget);
                var credentials = await m_ApiClient.GetCredentialsForPlatform(platform);

                cancellationToken.ThrowIfCancellationRequested();

                m_CredentialsMap = new Dictionary<string, GetCredentialsResponse>();
                var credentialIds = credentials.Select(credential => credential.CredentialId).ToList();
                for (var i = 0; i < credentialIds.Count; i++)
                {
                    m_CredentialsMap.Add(credentialIds[i], credentials[i]);
                }

                m_CredentialsDropDown.choices = credentialIds;
                if (string.IsNullOrEmpty(m_CredentialsDropDown.value) && credentialIds.Count > 0 && m_CredentialsToggle.value)
                {
                    m_CredentialsDropDown.value = credentialIds.FirstOrDefault();
                }
                UpdateCredentialsHelpBox(m_CredentialsDropDown.value);
            }
            catch (OperationCanceledException)
            {
                // This is expected when cancellation occurs, don't treat as error
                return;
            }
        }

        void OnRefreshCredentialsButtonClicked()
        {
            FetchCredentials(CancellationToken.None);
        }

        void OnNewCredentialsButtonClicked()
        {
            var url = BuildAutomationDashboardUrls.NewCredentialsByPlatformUrl(BuildAutomationUtilities.GetCredentialPlatform(m_BuildAutomationSettings.buildTarget));
            Application.OpenURL(url);
        }

        void OnSigningToggled(ChangeEvent<bool> evt)
        {
            m_CredentialsDropDown.enabledSelf = evt.newValue;
            m_CredentialsDropDown.value = evt.newValue ?  m_CredentialsDropDown.choices.FirstOrDefault() : "";
        }

        void OnCredentialsValueChanged(ChangeEvent<string> evt)
        {
            UpdateCredentialsHelpBox(evt.newValue);
        }

        void UpdateCredentialsHelpBox(string credentialsId)
        {
            if (string.IsNullOrEmpty(credentialsId) || m_CredentialsMap == null)
            {
                m_CredentialsHelpBox.style.display = DisplayStyle.None;
                return;
            }

            var credentials = m_CredentialsMap.GetValueOrDefault(credentialsId, null);
            var expiration = BuildAutomationUtilities.GetExpirationDateFromCredentials(credentials, m_BuildAutomationSettings.buildTarget);
            if (string.IsNullOrEmpty(expiration))
            {
                m_CredentialsHelpBox.style.display = DisplayStyle.None;
            }
            else
            {
                var expirationDate = DateTime.Parse(expiration);
                m_CredentialsHelpBox.style.display = DisplayStyle.Flex;

                var daysToExpiration = (expirationDate - DateTime.Now).TotalDays;
                if (daysToExpiration < k_DaysToExpirationError)
                {
                    m_CredentialsHelpBox.text = $"{credentials.Label} credentials expired on {BuildAutomationUtilities.FormatDate(expiration)}";
                    m_CredentialsHelpBox.messageType = HelpBoxMessageType.Error;
                }
                else if (daysToExpiration < k_DaysToExpirationWarning)
                {
                    m_CredentialsHelpBox.text = $"{credentials.Label} credentials will expire on {BuildAutomationUtilities.FormatDate(expiration)}";
                    m_CredentialsHelpBox.messageType = HelpBoxMessageType.Warning;
                }
                else
                {
                    m_CredentialsHelpBox.messageType = HelpBoxMessageType.Info;
                    m_CredentialsHelpBox.style.display = DisplayStyle.None;
                }
            }
        }

        string FormatCredentialsSelected(string credentialId)
        {
            if (string.IsNullOrEmpty(credentialId))
            {
                return "";
            }

            if (m_CredentialsMap == null)
            {
                return credentialId;
            }

            var cred = m_CredentialsMap.GetValueOrDefault(credentialId, null);
            return cred != null ? cred.Label : credentialId;
        }

        string FormatCredentialsList(string credentialId)
        {
            if (string.IsNullOrEmpty(credentialId))
            {
                return "";
            }

            if (m_CredentialsMap == null)
            {
                return credentialId;
            }

            var cred = m_CredentialsMap.GetValueOrDefault(credentialId, null);
            var expiration = BuildAutomationUtilities.GetExpirationDateFromCredentials(cred, m_BuildAutomationSettings.buildTarget);
            var expirationString = "";
            if (!string.IsNullOrEmpty(expiration))
            {
                expirationString = $" (expires {BuildAutomationUtilities.FormatDate(expiration).Replace("/", "-")})";
            }
            return cred != null ? $"{cred.Label}{expirationString}" : credentialId;
        }
    }
}
