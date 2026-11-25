using System.Collections;
using HandyDeviceControl.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace HandyDeviceControl.Development.Tests.Editor {
    public sealed class ConnectionSceneTests {
        [UnitySetUp] public IEnumerator EnterScene() {
            yield return new EnterPlayMode();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/ConnectionTest.unity",
                new LoadSceneParameters(LoadSceneMode.Single));
        }

        [UnityTearDown] public IEnumerator LeaveScene() { yield return new ExitPlayMode(); }

        [UnityTest] public IEnumerator SwitchingTransportRequiresTheCorrespondingDeviceOrKey() {
            var panel = Object.FindFirstObjectByType<HandyConnectionCanvas>();
            Assert.That(panel, Is.Not.Null);
            using (var data = new SerializedObject(panel)) {
                Assert.That(AssetDatabase.GetAssetPath(data.FindProperty("settings").objectReferenceValue),
                    Is.EqualTo("Assets/Settings/HandyConnectionSettings.asset"));
            }
            var form = panel.GetComponentInChildren<HandyConnectionForm>(true);
            var status = panel.GetComponentInChildren<HandyConnectionStatus>(true);
            yield return null;
            var input = EventSystem.current.currentInputModule as UnityEngine.InputSystem.UI.InputSystemUIInputModule;
            Assert.That(input, Is.Not.Null);
            Assert.That(input.leftClick.action.name, Is.EqualTo("Click"));
            Assert.That(form.check.interactable || form.startTest.interactable || status.stop.interactable, Is.False);
            Assert.That(form.connectionKey.textComponent.font.HasCharacter('한'), Is.True);
            form.connectionKey.text = "test";
            Assert.That(form.check.interactable, Is.True);
            form.bluetooth.isOn = true;
            yield return null;
            Assert.That(form.credentials.activeInHierarchy, Is.False);
            Assert.That(form.discovery.activeInHierarchy, Is.True);
            Assert.That(form.check.interactable, Is.False);
            Assert.That(panel.Session, Is.Null);
            form.wifi.isOn = true;
            Assert.That(form.credentials.activeInHierarchy && form.check.interactable, Is.True);
            Assert.That(status.state.text, Is.EqualTo("연결 대기"));
        }

        [UnityTest] public IEnumerator ExistingToolkitPanelsStillUseTheSameConnectionState() {
            var root = new GameObject("기존 연결 패널");
            root.SetActive(false);
            try {
                var doc = root.AddComponent<UIDocument>();
                doc.panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>("Assets/UI/ConnectionPanel.asset");
                doc.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/UI/ConnectionTest.uxml");
                root.AddComponent<HandyConnectionPanel>();
                root.SetActive(true);
                yield return null;
                var key = doc.rootVisualElement.Q<TextField>("connection-key");
                var check = doc.rootVisualElement.Q<Button>("check");
                key.value = "test";
                Assert.That(check.enabledSelf, Is.True);
                doc.rootVisualElement.Q<RadioButtonGroup>("transport").value = 1;
                Assert.That(check.enabledSelf, Is.False);
            } finally { Object.Destroy(root); }
        }
    }
}
