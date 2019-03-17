using System;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using System.Xml;
using UnityEngine;
using UnityEditor;
using UniGLTF;
using VRM;
using VRCSDK2;

namespace Esperecyan.Unity.VRMConverterForVRChat
{
    /// <summary>
    /// 変換ダイアログ。
    /// </summary>
    public class Wizard : ScriptableWizard
    {
        private static readonly string EditorUserSettingsName = typeof(Wizard).Namespace;
        private static readonly string EditorUserSettingsXmlNamespace = "https://pokemori.booth.pm/items/1025226";

        /// <summary>
        /// 変換後の処理を行うコールバック関数。
        /// </summary>
        /// <param name="avatar"></param>
        /// <param name="meta"></param>
        public delegate void PostConverting(GameObject avatar, VRMMeta meta);

        /// <summary>
        /// ダイアログの最小サイズ。
        /// </summary>
        private static readonly Vector2 MinSize = new Vector2(x: 800, y: 350);

        /// <summary>
        /// リストにおける一段回分の字下げ幅。
        /// </summary>
        private static readonly int Indent = 20;

        /// <summary>
        /// 複製・変換対象のアバター。
        /// </summary>
        [SerializeField]
        private Animator avatar;

        /// <summary>
        /// アバターの<see cref="VRC_AvatarDescriptor.Animations"/>に設定する値。
        /// </summary>
        [SerializeField]
        private VRC_AvatarDescriptor.AnimationSet defaultAnimationSet;

        /// <summary>
        /// オートアイムーブメントを有効化するなら<c>true</c>、無効化するなら<c>false</c>。
        /// </summary>
        [SerializeField, Localizable]
        private bool enableEyeMovement = true;

        /// <summary>
        /// VRoid Studioから出力されたモデルがなで肩になる問題について、ボーンのPositionを変更するなら<c>true</c>。
        /// </summary>
        [SerializeField, Localizable]
        private bool fixVroidSlopingShoulders = true;

        /// <summary>
        /// Directional Lightがないワールド向けにマテリアルを変更するなら <c>true</c>。
        /// </summary>
        [SerializeField, Localizable]
        private bool useOldMtoon = true;

        /// <summary>
        /// 各種コールバック関数のユーザー設定値。
        /// </summary>
        [SerializeField, Localizable]
        private MonoScript callbackFunctions;

        /// <summary>
        /// <see cref="ComponentsReplacer.SwayingParametersConverter"/>のユーザー設定値。
        /// </summary>
        private ComponentsReplacer.SwayingParametersConverter swayingParametersConverter;

        /// <summary>
        /// <see cref="Wizard.PostConverting"/>のユーザー設定値。
        /// </summary>
        private Wizard.PostConverting postConverting;

        /// <summary>
        /// 変換ダイアログを開きます。
        /// </summary>
        /// <param name="avatar"></param>
        internal static void Open(GameObject avatar)
        {
            var wizard = DisplayWizard<Wizard>(title: Converter.Name + " " + Converter.Version, createButtonName: Gettext._("Duplicate and Convert"));
            wizard.minSize = Wizard.MinSize;

            wizard.avatar = avatar.GetComponent<Animator>();

            wizard.LoadSettings();
        }

        /// <summary>
        /// <see cref="Wizard"/>の保存するフィールド一覧を取得します。
        /// </summary>
        /// <returns></returns>
        private IEnumerable<FieldInfo> GetSavedFieldInfos()
        {
            return this.GetType().GetFields(bindingAttr: BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(info => info.Name != "avatar" && info.GetCustomAttributes(attributeType: typeof(SerializeField), inherit: false).Length > 0);
        }

        /// <summary>
        /// アバターごとの変換設定を記録したXML文書を返します。
        /// </summary>
        /// <returns>まだ何も保存されていない場合、またはXMLパースエラーが発生した場合は、ルート要素のみ存在する文書を返します。</returns>
        private XmlDocument GetSettingsList()
        {
            var defaultDocument = new XmlDocument();
            defaultDocument.AppendChild(defaultDocument.CreateElement(qualifiedName: "list", namespaceURI: Wizard.EditorUserSettingsXmlNamespace));

            string configValue = EditorUserSettings.GetConfigValue(Wizard.EditorUserSettingsName);
            if (string.IsNullOrEmpty(configValue))
            {
                return defaultDocument;
            }

            var document = new XmlDocument();
            try
            {
                document.LoadXml(xml: configValue);
            }
            catch (XmlException)
            {
                return defaultDocument;
            }

            return document;
        }

        /// <summary>
        /// XML文書から指定したタイトルのアバターの変換設定を取得します。
        /// </summary>
        /// <param name="document"></param>
        /// <param name="title"></param>
        /// <returns>存在しない場合、ルート要素へ属性が設定されていないsettings要素を追加し、それを返します。</returns>
        private XmlElement GetSettings(XmlDocument document, string title)
        {
            XmlElement settings = document.GetElementsByTagName(localName: "settings", namespaceURI: Wizard.EditorUserSettingsXmlNamespace)
                .Cast<XmlElement>().FirstOrDefault(predicate: element => element.GetAttribute(name: "title") == title);
            if (settings != null) {
                return settings;
            }

            settings = document.CreateElement(qualifiedName: "settings", namespaceURI: Wizard.EditorUserSettingsXmlNamespace);
            document.DocumentElement.AppendChild(settings);
            return settings;
        }
        
        /// <summary>
        /// 選択されているアバターの変換設定を反映します。
        /// </summary>
        private void LoadSettings()
        {
            string title = this.avatar.GetComponent<VRMMeta>().Meta.Title;
            if (string.IsNullOrEmpty(title))
            {
                return;
            }

            XmlElement settings = this.GetSettings(document: this.GetSettingsList(), title: title);
            if (string.IsNullOrEmpty(settings.GetAttribute("title")))
            {
                return;
            }

            foreach (FieldInfo info in this.GetSavedFieldInfos())
            {
                string value = settings.GetAttribute(info.Name);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                Type type = info.FieldType;
                object fieldValue = null;
                if (typeof(Enum).IsAssignableFrom(type))
                {
                    fieldValue = Enum.Parse(enumType: type, value: value);
                }
                else if (type == typeof(bool))
                {
                    fieldValue = bool.Parse(value);
                }
                else if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                {
                    fieldValue = AssetDatabase.LoadAssetAtPath(value, type);
                }
                info.SetValue(obj: this, value: fieldValue);
            }
        }

        /// <summary>
        /// 選択されているアバターの変換設定を保存します。
        /// </summary>
        private void SaveSettings()
        {
            string title = this.avatar.GetComponent<VRMMeta>().Meta.Title;
            if (string.IsNullOrEmpty(title))
            {
                return;
            }

            XmlDocument document = this.GetSettingsList();
            XmlElement settings = this.GetSettings(document: document, title: title);
            settings.SetAttribute("title", title);
            
            foreach (FieldInfo info in this.GetSavedFieldInfos())
            {
                Type type = info.FieldType;
                string value = "";
                if (typeof(Enum).IsAssignableFrom(type) || type == typeof(bool))
                {
                    value = info.GetValue(obj: this).ToString();
                }
                else if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                {
                    value = AssetDatabase.GetAssetPath(this.callbackFunctions);
                }

                settings.SetAttribute(info.Name, value);
            }

            var writer = new Writer();
            document.Save(writer: writer);
            EditorUserSettings.SetConfigValue(Wizard.EditorUserSettingsName, writer.ToString());
            writer.Close();
        }

        protected override bool DrawWizardGUI()
        {
            base.DrawWizardGUI();
            isValid = true;

            if (this.callbackFunctions)
            {
                Type callBackFunctions = this.callbackFunctions.GetClass();

                this.swayingParametersConverter = Delegate.CreateDelegate(
                    type: typeof(ComponentsReplacer.SwayingParametersConverter),
                    target: callBackFunctions,
                    method: "SwayingParametersConverter",
                    ignoreCase: false,
                    throwOnBindFailure: false
                ) as ComponentsReplacer.SwayingParametersConverter;

                this.postConverting = Delegate.CreateDelegate(
                    type: typeof(Wizard.PostConverting),
                    target: callBackFunctions,
                    method: "PostConverting",
                    ignoreCase: false,
                    throwOnBindFailure: false
                ) as Wizard.PostConverting;
            }

            var indentStyle = new GUIStyle() { padding = new RectOffset() { left = Wizard.Indent } };
            EditorGUILayout.LabelField(
                label: (this.swayingParametersConverter != null ? "☑" : "☐")
                    + " public static DynamicBoneParameters SwayingParametersConverter(SpringBoneParameters, BoneInfo)",
                style: indentStyle
            );
            
            EditorGUILayout.LabelField(
                label: (this.postConverting != null ? "☑" : "☐")
                    + " public static void PostConverting(GameObject, VRMMeta)",
                style: indentStyle
            );

            foreach (var type in Converter.RequiredComponents) {
                if (!this.avatar.GetComponent(type: type))
                {
                    EditorGUILayout.HelpBox(string.Format(Gettext._("Not set “{0}” component."), type), MessageType.Error);
                    isValid = false;
                }
            }

            string version = VRChatUtility.GetSupportedUnityVersion();
            if (version != "" && Application.unityVersion != version)
            {
                EditorGUILayout.HelpBox(string.Format(
                    Gettext._("Unity {0} is running. If you are using a different version than {1}, VRChat SDK might not work correctly. Recommended using Unity downloaded from {2} ."),
                    Application.unityVersion,
                    version,
                    VRChatUtility.DownloadURL
                ), MessageType.Warning);
            }

            return true;
        }

        private void OnWizardCreate()
        {
            var avatar = Instantiate(this.avatar.gameObject) as GameObject;

            this.SaveSettings();

            IEnumerable<Converter.Message> messages = Converter.Convert(
                avatar: avatar,
                defaultAnimationSet: this.defaultAnimationSet,
                swayingParametersConverter: this.swayingParametersConverter,
                assetsPath: GetAssetsPath(vrm: this.avatar.gameObject),
                enableAutoEyeMovement: this.enableEyeMovement,
                fixVRoidSlopingShoulders: this.fixVroidSlopingShoulders,
                changeMaterialsForWorldsNotHavingDirectionalLight: this.useOldMtoon
            );

            if (this.postConverting != null) {
                this.postConverting(avatar, this.avatar.gameObject.GetComponent<VRMMeta>());
            }

            ResultDialog.Open(messages: messages);
        }

        /// <summary>
        /// プレハブのパスを取得します。
        /// </summary>
        /// <param name="vrm"></param>
        /// <returns></returns>
        private string GetAssetsPath(GameObject vrm)
        {
            var path = UnityPath.FromAsset(asset: vrm);
            if (!path.IsUnderAssetsFolder)
            {
                var prefab = PrefabUtility.GetPrefabParent(vrm);
                if (prefab)
                {
                    path = UnityPath.FromAsset(asset: prefab);
                }
                else
                {
                    path = UnityPath.FromUnityPath("Assets/" + avatar.name);
                }
            }

            return path.Value;
        }
    }
}