using NUnit.Framework;
using UnityEditor;
using UnityEngine.InputSystem;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace HandyDeviceControl.Development.Tests.Editor {
    public sealed class InputActionImportTests {
        [Test] public void DefaultUiActionsLoadFromTheInputSystemPackage() {
            var inputPackage = PackageInfo.FindForAssembly(typeof(InputActionAsset).Assembly);
            var matches = AssetDatabase.FindAssets("DefaultInputActions t:InputActionAsset", new[] { inputPackage.assetPath });
            Assert.That(matches, Has.Length.EqualTo(1), "Input System 패키지의 기본 입력 에셋을 찾을 수 있어야 합니다.");
            var path = AssetDatabase.GUIDToAssetPath(matches[0]);
            var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(path);
            Assert.That(actions, Is.Not.Null, path);
            Assert.That(actions.FindAction("UI/Click"), Is.Not.Null);
        }
    }
}
