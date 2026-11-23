using UnityEditor;
using UnityEngine.InputSystem;

namespace HandyDeviceControl.Development.Editor {
    internal sealed class DefaultInputActionsMigration : AssetPostprocessor {
        private const string DefaultInputActionsGuid = "ca9f5fa95ffab41fb9a615ab714db018";

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths, bool didDomainReload) {
            if (!didDomainReload) return;
            var path = AssetDatabase.GUIDToAssetPath(DefaultInputActionsGuid);
            if (string.IsNullOrEmpty(path)) return;

            // 이전 폴더 구조에서 실패한 입력 파일은 원본을 옮긴 뒤에도 캐시에 남는다.
            if (!AssetDatabase.LoadAssetAtPath<InputActionAsset>(path))
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }
    }
}
