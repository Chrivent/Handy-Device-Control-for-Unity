using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace HandyDeviceControl.UI {
    [RequireComponent(typeof(UIDocument)), DefaultExecutionOrder(90)]
    public sealed class HandyConnectionPanel : HandyConnectionBehaviour {
        private VisualElement _credentials, _bluetooth;
        private RadioButtonGroup _transport, _devices;
        private TextField _connectionKey;
        private Label _state, _detail, _latency, _profile;
        private Button _check, _start, _stop, _scan;
        private VisualElement Root => GetComponent<UIDocument>().rootVisualElement;
        protected override string ConnectionKey => _connectionKey.value;

        protected override void BindControls() {
            var root = Root;
            _credentials = root.Q("credentials");
            _bluetooth = root.Q("bluetooth");
            _transport = root.Q<RadioButtonGroup>("transport");
            _devices = root.Q<RadioButtonGroup>("devices");
            _connectionKey = root.Q<TextField>("connection-key");
            _state = root.Q<Label>("state");
            _detail = root.Q<Label>("detail");
            _latency = root.Q<Label>("latency");
            _profile = root.Q<Label>("profile");
            _check = root.Q<Button>("check");
            _start = root.Q<Button>("start");
            _stop = root.Q<Button>("stop");
            _scan = root.Q<Button>("scan");
            _transport.RegisterValueChangedCallback(OnTransport);
            _devices.RegisterValueChangedCallback(OnDevice);
            _connectionKey.RegisterValueChangedCallback(OnKey);
            _check.clicked += CheckConnection;
            _start.clicked += StartTest;
            _stop.clicked += StopPlayback;
            _scan.clicked += Scan;
        }

        protected override void UnbindControls() {
            _transport.UnregisterValueChangedCallback(OnTransport);
            _devices.UnregisterValueChangedCallback(OnDevice);
            _connectionKey.UnregisterValueChangedCallback(OnKey);
            _check.clicked -= CheckConnection;
            _start.clicked -= StartTest;
            _stop.clicked -= StopPlayback;
            _scan.clicked -= Scan;
        }

        private void OnTransport(ChangeEvent<int> evt) => ChangeTransport(evt.newValue == 1);
        private void OnDevice(ChangeEvent<int> evt) => ChangeDevice(evt.newValue);
        private void OnKey(ChangeEvent<string> evt) => ResetConnection();
        protected override void SelectTransport(bool bluetooth) => _transport.SetValueWithoutNotify(bluetooth ? 1 : 0);
        protected override void SelectDevice(int index) => _devices.SetValueWithoutNotify(index);
        protected override void SetDevices(HandyBleDevice[] devices, int selected) {
            _devices.choices = devices.Select(device => device.DisplayName).ToArray();
            SelectDevice(selected);
        }

        protected override void DrawState(HandySessionState state, bool editable, bool scanning, string label, string detail, string profile, string latency) {
            _transport.SetEnabled(editable);
            _credentials.style.display = UsesBluetooth ? DisplayStyle.None : DisplayStyle.Flex;
            _credentials.SetEnabled(editable);
            _bluetooth.style.display = UsesBluetooth ? DisplayStyle.Flex : DisplayStyle.None;
            _bluetooth.SetEnabled(editable);
            _scan.SetEnabled(HandyBleClient.IsSupported && !scanning);
            _devices.style.display = !HasDevices ? DisplayStyle.None : DisplayStyle.Flex;
            _check.SetEnabled(editable && !scanning && CanConnect);
            _start.SetEnabled(state == HandySessionState.Ready);
            _stop.SetEnabled(Session?.CanStop ?? false);
            var legacy = Session?.UsesLegacyBluetooth == true;
            _stop.tooltip = legacy ? "다음 위치 전송을 취소합니다. 이미 보낸 이동은 끝까지 진행될 수 있습니다." : null;
            _state.text = label;
            _state.EnableInClassList("warning", state == HandySessionState.StopUnconfirmed);
            _detail.text = detail;
            _profile.text = profile;
            _latency.text = latency;
        }
    }
}
