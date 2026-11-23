using System.Collections;
using HandyDeviceControl.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
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

        [UnityTearDown] public IEnumerator LeaveScene() {
            yield return new ExitPlayMode();
        }

        [UnityTest] public IEnumerator SwitchingTransportRequiresTheCorrespondingDeviceOrKey() {
            var panel = Object.FindFirstObjectByType<HandyConnectionPanel>();
            Assert.That(panel, Is.Not.Null);
            using (var binding = new SerializedObject(panel)) {
                var settings = binding.FindProperty("settings").objectReferenceValue;
                Assert.That(AssetDatabase.GetAssetPath(settings), Is.EqualTo("Assets/Settings/HandyConnectionSettings.asset"));
            }
            var document = panel.GetComponent<UIDocument>();
            Assert.That(document.panelSettings, Is.Not.Null);
            yield return null;
            var root = document.rootVisualElement;
            Assert.That(root.panel, Is.Not.Null);
            var connectionKey = root.Q<TextField>("connection-key");
            var check = root.Q<Button>("check");
            var transport = root.Q<RadioButtonGroup>("transport");
            Assert.That(check.enabledSelf, Is.False);
            Assert.That(root.Q<Button>("start").enabledSelf, Is.False);
            Assert.That(connectionKey.isPasswordField, Is.False);
            connectionKey.value = "test";
            Assert.That(check.enabledSelf, Is.True);

            transport.value = 1;
            yield return null;
            Assert.That(root.Q("credentials").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(root.Q("bluetooth").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(check.enabledSelf, Is.False);
            Assert.That(panel.Session, Is.Null);

            transport.value = 0;
            yield return null;
            Assert.That(root.Q("credentials").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(check.enabledSelf, Is.True);
        }
    }
}
