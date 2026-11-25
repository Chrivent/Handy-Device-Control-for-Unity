using UnityEngine;
using UnityEngine.UI;

namespace HandyDeviceControl.UI {
    public sealed class HandyConnectionStatus : MonoBehaviour {
        public Text state, detail;
        public Button stop;
        public Color warningColor = new(1f, 0.71f, 0.53f);
    }
}
