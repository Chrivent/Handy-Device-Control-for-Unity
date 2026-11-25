using UnityEngine;

namespace HandyDeviceControl.UI {
    [DefaultExecutionOrder(90)]
    public sealed class HandyConnectionCanvas : HandyConnectionBehaviour {
        [SerializeField] private HandyConnectionForm form;
        [SerializeField] private HandyConnectionStatus status;
        private Color _normalColor;
        protected override string ConnectionKey => form.connectionKey.text;

        private void Awake() => _normalColor = status.state.color;

        protected override void BindControls() {
            form.wifi.onValueChanged.AddListener(WifiChanged);
            form.bluetooth.onValueChanged.AddListener(BluetoothChanged);
            form.connectionKey.onValueChanged.AddListener(KeyChanged);
            form.check.onClick.AddListener(CheckConnection);
            form.scan.onClick.AddListener(Scan);
            form.startTest.onClick.AddListener(StartTest);
            status.stop.onClick.AddListener(StopPlayback);
        }

        protected override void UnbindControls() {
            form.wifi.onValueChanged.RemoveListener(WifiChanged);
            form.bluetooth.onValueChanged.RemoveListener(BluetoothChanged);
            form.connectionKey.onValueChanged.RemoveListener(KeyChanged);
            form.check.onClick.RemoveListener(CheckConnection);
            form.scan.onClick.RemoveListener(Scan);
            form.startTest.onClick.RemoveListener(StartTest);
            status.stop.onClick.RemoveListener(StopPlayback);
            form.ClearDevices();
        }

        private void WifiChanged(bool on) { if (on) ChangeTransport(false); }
        private void BluetoothChanged(bool on) { if (on) ChangeTransport(true); }
        private void KeyChanged(string _) => ResetConnection();
        private void DeviceChanged(int index, bool on) {
            if (on) ChangeDevice(index);
            else if (index == SelectedDevice) ChangeDevice(-1);
        }

        protected override void SelectTransport(bool bluetooth) {
            (bluetooth ? form.bluetooth : form.wifi).SetIsOnWithoutNotify(true);
            (bluetooth ? form.wifi : form.bluetooth).SetIsOnWithoutNotify(false);
        }

        protected override void SelectDevice(int index) => form.SelectDevice(index);
        protected override void SetDevices(HandyBleDevice[] devices, int selected) => form.ListDevices(devices, selected, DeviceChanged);

        protected override void DrawState(HandySessionState state, bool editable, bool scanning, string label, string detail, string profile, string latency) {
            form.wifi.interactable = form.bluetooth.interactable = form.connectionKey.interactable = editable;
            form.credentials.SetActive(!UsesBluetooth);
            form.discovery.SetActive(UsesBluetooth);
            form.deviceList.gameObject.SetActive(HasDevices);
            form.EnableDevices(editable);
            form.scan.interactable = editable && HandyBleClient.IsSupported && !scanning;
            form.check.interactable = editable && !scanning && CanConnect;
            form.startTest.interactable = state == HandySessionState.Ready;
            status.stop.interactable = Session?.CanStop ?? false;
            status.state.text = label;
            status.state.color = state == HandySessionState.StopUnconfirmed ? status.warningColor : _normalColor;
            status.detail.text = detail;
            form.profile.text = Session?.UsesLegacyBluetooth == true
                ? profile + "\n정지 시 다음 위치 전송을 취소합니다. 이미 보낸 이동은 끝까지 진행될 수 있습니다."
                : profile;
            form.latency.text = latency;
        }
    }
}
