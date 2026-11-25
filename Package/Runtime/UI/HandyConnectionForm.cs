using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HandyDeviceControl.UI {
    public sealed class HandyConnectionForm : MonoBehaviour {
        public Toggle wifi, bluetooth;
        public GameObject credentials, discovery;
        public InputField connectionKey;
        public ToggleGroup deviceList;
        public HandyDeviceOption deviceOption;
        public Button check, scan, startTest;
        public Text profile, latency;
        private readonly List<HandyDeviceOption> _options = new();

        internal void ClearDevices() {
            foreach (var option in _options) {
                option.gameObject.SetActive(false);
                Destroy(option.gameObject);
            }
            _options.Clear();
        }

        internal void ListDevices(HandyBleDevice[] devices, int selected, Action<int, bool> changed) {
            ClearDevices();
            for (var index = 0; index < devices.Length; index++) {
                var option = Instantiate(deviceOption, deviceList.transform, false);
                var position = index;
                option.caption.text = devices[index].DisplayName;
                option.toggle.group = deviceList;
                option.toggle.SetIsOnWithoutNotify(index == selected);
                option.toggle.onValueChanged.AddListener(on => changed(position, on));
                _options.Add(option);
            }
        }

        internal void SelectDevice(int index) {
            for (var i = 0; i < _options.Count; i++) _options[i].toggle.SetIsOnWithoutNotify(i == index);
        }

        internal void EnableDevices(bool editable) {
            foreach (var option in _options) option.toggle.interactable = editable;
        }
    }
}
