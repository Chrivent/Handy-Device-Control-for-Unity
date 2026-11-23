using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;

namespace HandyDeviceControl.Development.Tests.Editor {
    public sealed class InputActionImportTests {
        [Test] public void DefaultUiActionsLoadFromTheInputSystemPackage() {
            const string path = "Packages/com.unity.inputsystem/InputSystem/Runtime/Plugins/PlayerInput/DefaultInputActions.inputactions";
            var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(path);
            Assert.That(actions, Is.Not.Null, path);
            Assert.That(actions.FindAction("UI/Click"), Is.Not.Null);
        }
    }
}
