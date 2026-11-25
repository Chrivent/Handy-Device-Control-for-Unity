using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace HandyDeviceControl.UI {
    public abstract class HandyConnectionBehaviour : MonoBehaviour {
        [SerializeField] private HandyConnectionSettings settings;

        public HandySession Session { get; private set; }
        public event Action Changed;
        public event Action StopRequested;

        private CancellationTokenSource _viewLifetime, _scanCancellation;
        private bool _useBluetooth, _destroyed;
        private HandyBleDevice[] _found = Array.Empty<HandyBleDevice>();
        private int _selected = -1;
        private string _scanDetail;
        private float _nextLinkCheck;
        private string ApplicationId => settings ? settings.ApplicationId : string.Empty;

        protected bool CanConnect => _useBluetooth
            ? HandyBleClient.IsSupported && _selected >= 0 && _selected < _found.Length
            : !string.IsNullOrWhiteSpace(ConnectionKey);

        protected abstract string ConnectionKey { get; }
        protected bool UsesBluetooth => _useBluetooth;
        protected int SelectedDevice => _selected;
        protected bool HasDevices => _found.Length > 0;
        protected abstract void BindControls();
        protected abstract void UnbindControls();
        protected abstract void SelectTransport(bool bluetooth);
        protected abstract void SelectDevice(int index);
        protected abstract void SetDevices(HandyBleDevice[] devices, int selected);
        protected abstract void DrawState(HandySessionState state, bool editable, bool scanning, string label, string detail, string profile, string latency);

        protected void OnEnable() {
            _viewLifetime = new CancellationTokenSource();
            if (Session?.State == HandySessionState.Checking) ReleaseSession();
            BindControls();
            SelectTransport(_useBluetooth);
            SetDevices(_found, _selected);
            Render();
        }

        protected void OnDisable() {
            UnbindControls();
            var lifetime = _viewLifetime;
            _viewLifetime = null;
            CancelScan();
            lifetime.Cancel();
            lifetime.Dispose();
            if (Session?.CanConfigure == true) ReleaseSession();
            else StopIfActive("씬 비활성화");
        }

        protected void OnDestroy() {
            _destroyed = true;
            if (Session?.State == HandySessionState.StopUnconfirmed) ReleaseSession();
        }

        protected void Update() {
            if (!_useBluetooth || Session == null || Time.unscaledTime < _nextLinkCheck) return;
            _nextLinkCheck = Time.unscaledTime + 0.4f;
            Session.CheckBluetoothConnection();
        }

        protected void OnApplicationPause(bool paused) {
            if (paused) StopIfActive("앱 일시 정지");
        }

        protected void OnApplicationFocus(bool focused) {
            if (!focused) StopIfActive("창 비활성화");
        }

        private void StopIfActive(string reason) {
            if (Session?.MayBeMoving == true) _ = Session.StopAsync(reason);
        }

        protected void ChangeTransport(bool bluetooth) {
            if (Session is { CanConfigure: false }) {
                SelectTransport(_useBluetooth);
                return;
            }
            if (_useBluetooth == bluetooth) return;
            _useBluetooth = bluetooth;
            CancelScan();
            ClearSearch();
        }

        protected void ChangeDevice(int index) {
            if (Session is { CanConfigure: false }) {
                SelectDevice(_selected);
                return;
            }
            if (_selected == index) return;
            _selected = index;
            ResetConnection();
        }

        private void ClearSearch() {
            _found = Array.Empty<HandyBleDevice>();
            _selected = -1;
            _scanDetail = null;
            SetDevices(_found, _selected);
            ResetConnection();
        }

        private void CancelScan() {
            _scanCancellation?.Cancel();
            _scanCancellation = null;
        }

        protected void Scan() => _ = ScanDevicesAsync();

        private async Task ScanDevicesAsync() {
            try {
                if (!_useBluetooth || _scanCancellation != null || Session is { CanConfigure: false }) return;
                using var search = new CancellationTokenSource();
                _scanCancellation = search;
                try {
                    ClearSearch();
                    var devices = await HandyBleClient.ScanAsync(search.Token);
                    if (search.IsCancellationRequested) return;
                    _found = devices;
                    _scanDetail = devices.Length == 0
                        ? "핸디를 찾지 못했습니다. 핸디의 블루투스 모드를 켠 뒤 다시 검색하세요."
                        : "목록에서 내 핸디를 선택하고 연결을 확인하세요.";
                    SetDevices(_found, _selected);
                } catch (OperationCanceledException) when (search.IsCancellationRequested) {
                    // 연결 방식이 바뀌거나 씬이 닫힌 검색은 새 화면의 상태를 갱신하지 않는다.
                } catch (Exception error) {
                    if (search.IsCancellationRequested) return;
                    _scanDetail = error switch {
                        DllNotFoundException or BadImageFormatException or EntryPointNotFoundException =>
                            "Windows 블루투스 API를 불러올 수 없습니다. Windows x64 환경을 확인하세요.",
                        TimeoutException => "블루투스 어댑터가 응답하지 않았습니다. 잠시 뒤 다시 검색하세요.",
                        InvalidOperationException or PlatformNotSupportedException => error.Message,
                        _ => "기기 검색에 실패했습니다. 다시 검색하세요."
                    };
                    if (error is not (InvalidOperationException or PlatformNotSupportedException or TimeoutException)) {
                        Debug.LogException(error, this);
                    }
                } finally {
                    if (_scanCancellation == search) {
                        _scanCancellation = null;
                        Render();
                    }
                }
            } catch (Exception error) {
                Debug.LogException(error, this);
                if (_viewLifetime == null || !_useBluetooth || _scanCancellation != null) return;
                _scanDetail = "기기 검색 화면을 갱신하지 못했습니다. 기기 설정을 다시 열어 주세요.";
                Render();
            }
        }

        protected void ResetConnection() {
            if (Session is { CanConfigure: false }) return;
            ReleaseSession();
            Render();
        }

        private void ReleaseSession() {
            if (Session == null) return;
            Session.Changed -= SessionChanged;
            Session.Dispose();
            Session = null;
        }

        // 다음 씬의 소유자에게 준비된 연결을 넘긴 뒤에는 이 화면이 해당 연결을 정리하지 않는다.
        public HandySession TakeReadySession() {
            if (Session?.State != HandySessionState.Ready) return null;
            var ready = Session;
            ready.Changed -= SessionChanged;
            Session = null;
            return ready;
        }

        private void SessionChanged() {
            if (_viewLifetime != null) Render();
            else if (Session.CanConfigure || (_destroyed && Session.State == HandySessionState.StopUnconfirmed)) ReleaseSession();
        }

        protected void CheckConnection() {
            if (Session is { CanConfigure: false } || _scanCancellation != null || !CanConnect) return;
            ReleaseSession();
            IHandyClient client = _useBluetooth
                ? new HandyBleClient(_found[_selected])
                : new HandyApiClient(ApplicationId, ConnectionKey);
            Session = new HandySession(client);
            Session.Changed += SessionChanged;
            _ = Session.CheckAsync(_viewLifetime.Token);
        }

        protected void StartTest() {
            if (Session != null) _ = Session.RunTestAsync();
        }

        protected void StopPlayback() {
            StopRequested?.Invoke();
            if (Session != null) _ = Session.StopAsync();
        }

        private void Render() {
            var state = Session?.State ?? HandySessionState.Idle;
            var editable = Session?.CanConfigure ?? true;
            var scanning = _scanCancellation != null;
            var legacy = Session?.UsesLegacyBluetooth == true;
            var profile = Session?.UsesPositionTest == true
                ? "위치 이동 40% → 60% → 40% · 이동당 0.5초"
                : state is HandySessionState.Idle or HandySessionState.Checking
                    ? "지원 기기 · 펌웨어 3 및 4"
                    : "시험 속도 10% · 설정된 이동 범위의 40~60% 구간";
            var label = state switch {
                HandySessionState.Checking => "연결 확인 중",
                HandySessionState.Ready => "준비 완료",
                HandySessionState.Starting => "시작 요청 중",
                HandySessionState.Running => "동작 중",
                HandySessionState.Stopping when legacy => "위치 전송 종료 중",
                HandySessionState.Stopping => "정지 확인 중",
                HandySessionState.StopUnconfirmed => "정지 미확인",
                _ => scanning ? "기기 검색 중" : "연결 대기"
            };
            var prompt = _useBluetooth switch {
                true when !HandyBleClient.IsSupported => "블루투스 직접 연결은 Windows x64에서 사용할 수 있습니다.",
                true when scanning => "주변의 핸디를 찾고 있습니다.",
                true => _scanDetail ?? "핸디의 블루투스 모드를 켜고 기기 검색을 누르세요.",
                false when string.IsNullOrWhiteSpace(ApplicationId) => "펌웨어 3은 Connection Key로 연결하세요. 펌웨어 4에는 게임의 Application ID도 필요합니다.",
                _ => "핸디를 Wi-Fi에 연결한 뒤 기기의 Connection Key를 입력하세요."
            };
            var latency = Session?.LastRequestMs is { } ms ? $"최근 명령 처리 시간  {ms} ms" : "최근 명령 처리 시간  —";
            DrawState(state, editable, scanning, label, Session?.Detail ?? prompt, profile, latency);
            Changed?.Invoke();
        }
    }
}
