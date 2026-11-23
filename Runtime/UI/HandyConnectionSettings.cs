using UnityEngine;

namespace HandyDeviceControl.UI {
    [CreateAssetMenu(menuName = "Handy Device Control/Connection Settings")]
    public sealed class HandyConnectionSettings : ScriptableObject {
        [SerializeField, Tooltip("이 작품에 발급된 Handy REST API v3 Application ID")]
        private string applicationId;
        public string ApplicationId => applicationId;
    }
}
