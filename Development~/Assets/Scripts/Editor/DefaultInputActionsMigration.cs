using UnityEditor;
using UnityEngine.InputSystem;

namespace HandyDeviceControl.Development.Editor {
    internal sealed class DefaultInputActionsMigration : AssetPostprocessor {
        private const string DefaultActionsPath = "Packages/com.unity.inputsystem/InputSystem/Runtime/Plugins/PlayerInput/DefaultInputActions.inputactions";

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths, bool didDomainReload) {
            if (!didDomainReload || !AssetDatabase.AssetPathExists(DefaultActionsPath)) return;

            // 이전 폴더 구조에서 실패한 입력 파일은 원본을 옮긴 뒤에도 캐시에 남는다.
            if (!AssetDatabase.LoadAssetAtPath<InputActionAsset>(DefaultActionsPath))
                AssetDatabase.ImportAsset(DefaultActionsPath, ImportAssetOptions.ForceUpdate);
        }
    }
}
