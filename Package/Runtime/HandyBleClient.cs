using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using static HandyDeviceControl.HandyRpc;

namespace HandyDeviceControl {
    public sealed class HandyBleClient : IHandyClient, IHandyStreamingClient, IHandyPositionClient, IDisposable {
        private readonly Func<IHandyBleLink> _openSelected;
        private readonly object _gate = new();
        private readonly HandyMotionStream _stream = new();
        private IHandyBleLink _link;
        private int _disposed;
        private static int _nextId;
        private readonly Stopwatch _lastLegacyMove = new();
        private int _lastMoveDurationMs;
        private bool _streamMode;
        private bool? _selectedLegacy;
        private string _legacyName;
        private Timer _heartbeat;
        private bool _connectionFailed;
        private CancellationTokenSource _legacyMotion;

        internal bool UsesLegacy => _selectedLegacy == true;
        private Task LegacyMotionCompletion { get; set; } = Task.CompletedTask;
        Task IHandyPositionClient.PositionMotionCompletion => UsesLegacy ? LegacyMotionCompletion : null;

        public static bool IsSupported => HandyBleNative.Supported;
        public static Task<HandyBleDevice[]> ScanAsync(CancellationToken cancellation) =>
            Task.Run(() => HandyBleNative.Scan(cancellation), cancellation);

        public HandyBleClient(HandyBleDevice device) : this(() => HandyBleNative.Open(device)) {
            if (device == null) throw new ArgumentNullException(nameof(device));
        }

        private HandyBleClient(Func<IHandyBleLink> openSelected) => _openSelected = openSelected;

        internal bool ConnectionLost {
            get {
                if (!Monitor.TryEnter(_gate)) return false;
                try { return _connectionFailed || _link?.Lost == true; } finally { Monitor.Exit(_gate); }
            }
        }

        public Task<string> CheckConnectionAsync(CancellationToken cancellation) => OnLink(() => {
            if (UsesLegacy) {
                Legacy(HandyLegacyProtocol.Command.Ping);
                return $"{_legacyName} · 레거시 BLE (펌웨어 버전 미제공)";
            }
            uint model;
            string version;
            try {
                var info = Call(Method.PublicInfo, cancellation);
                model = info.UInt(1);
                version = info.Text(3);
            } catch (Exception error) when (error is DeviceError { UnknownMethod: true } || error is TimeoutException) {
                // 4.4 이전에는 제조사 온보딩 앱의 로컬 BLE 조회에서 모델과 버전을 읽는다.
                var info = Call(Method.DeviceInfo, cancellation);
                model = info.UInt(7);
                version = info.Text(5);
            }
            var major = version.TrimStart('v', 'V').Split('.')[0];
            if (!uint.TryParse(major, out var number) || number < 4)
                throw new InvalidOperationException("기기에서 펌웨어 4 이상을 확인할 수 없습니다.");
            var name = model switch {
                0 => "The Handy", 2 => "The Handy 1.2", 3 => "The Handy 2", 4 => "The Handy 2 Pro",
                5 => "The Handy 2 Eco", 6 => "The Handy 2 SM",
                _ => throw new InvalidOperationException($"이 기기 모델(hw{model})은 핸디 왕복 동작을 지원하지 않습니다.")
            };
            return $"{version} · {name}";
        }, true, cancellation);

        public Task StartStrokeAsync(HandyStroke stroke, CancellationToken cancellation) => OnLink(() => {
            if (stroke == null) throw new ArgumentNullException(nameof(stroke));
            if (UsesLegacy) {
                CancelLegacyMotion();
                var window = Stopwatch.StartNew();
                MoveLegacy(stroke.MinPercent, cancellation, stroke.MoveDurationMs);
                _legacyMotion = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                var token = _legacyMotion.Token;
                LegacyMotionCompletion = stroke.FinishPositionsAsync(window, pos => OnLink(() => {
                    if (window.ElapsedMilliseconds + stroke.MoveDurationMs <= stroke.DurationMs)
                        MoveLegacy(pos, token, stroke.MoveDurationMs);
                    return true;
                }, false, token), token);
                return true;
            }
            // MODE_HAMP는 proto3의 기본값(0)이다. 구간을 지원하지 않는 기기에는 시작을 보내지 않는다.
            if (Call(Method.ModeSet, cancellation).UInt(1) != 0)
                throw new InvalidOperationException("핸디가 HAMP 모드로 전환되지 않았습니다.");
            var min = stroke.MinPercent / 100f;
            var max = stroke.MaxPercent / 100f;
            try {
                Call(Method.Zone, cancellation, min, max);
            } catch (DeviceError error) when (error.UnknownMethod) {
                throw new InvalidOperationException("일반 BLE 왕복 동작에는 펌웨어 4.0.15 이상이 필요합니다. 이 버전에서는 레거시 BLE 모드로 다시 검색하세요.");
            }
            Call(Method.Velocity, cancellation, 0.1f);
            var settings = Call(Method.State, cancellation).Message(1);
            if (!Matches(settings.Float(2), 0.1f) || !Matches(settings.Float(4), min) || !Matches(settings.Float(5), max))
                throw new InvalidOperationException("기기에서 속도 또는 이동 구간을 확인할 수 없습니다.");
            RequirePlayState(Call(Method.Start, cancellation), 1);
            return true;
        }, false, cancellation);

        public Task StreamAsync(IHandyMotionSource source, CancellationToken cancellation) =>
            _stream.RunAsync(token => SendMotionAsync(source, token), cancellation);

        private async Task SendMotionAsync(IHandyMotionSource source, CancellationToken cancellation) {
            if (source == null) throw new ArgumentNullException(nameof(source));
            cancellation.ThrowIfCancellationRequested();
            if (UsesLegacy) {
                await source.SendPositionsAsync((pos, ms) => OnLink(() => {
                    MoveLegacy(pos, cancellation, ms);
                    return true;
                }, false, cancellation), cancellation).ConfigureAwait(false);
                return;
            }
            await OnLink(() => {
                _streamMode = true;
                var mode = new List<byte>();
                WriteUnsigned(mode, 1, 4);
                return CallFields(Method.ModeSet, cancellation, mode).UInt(1) != 4 ? throw new InvalidOperationException("핸디가 위치 버퍼 모드로 전환되지 않았습니다.") : Call(Method.StreamSetup, cancellation).Message(1);
            }, false, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            await source.SendBufferedAsync(points => AddPointsAsync(points, cancellation),
                (ms, start) => SetStreamTimeAsync(ms, start, cancellation), cancellation).ConfigureAwait(false);
        }

        private Task AddPointsAsync(IReadOnlyList<HandyMotionPoint> points, CancellationToken cancellation) => OnLink(() => {
            var payload = new List<byte>();
            foreach (var point in points) {
                var entry = new List<byte>();
                WriteUnsigned(entry, 1, (uint)point.TimeMs);
                WriteUnsigned(entry, 2, (uint)point.Position);
                WriteMessage(payload, 1, entry);
            }
            return CallFields(Method.StreamAdd, cancellation, payload).Message(1);
        }, false, cancellation);

        private Task SetStreamTimeAsync(int timeMs, bool start, CancellationToken cancellation) => OnLink(() => {
            var payload = new List<byte>();
            WriteUnsigned(payload, 1, (uint)timeMs);
            WriteFloat(payload, 3, start ? 1f : 0.5f);
            if (start) {
                RequirePlayState(CallFields(Method.StreamPlay, cancellation, payload), 1);
            } else {
                CallFields(Method.StreamTime, cancellation, payload).Message(1);
            }
            return true;
        }, false, cancellation);

        public Task StopAsync() {
            _stream.Cancel();
            return OnLink(StopOnLink, true, CancellationToken.None);
        }

        private bool StopOnLink() {
            if (UsesLegacy) {
                // 레거시에는 정지/상태 명령이 없다. 추가 위치를 보내지 않고 마지막 유한 이동의 시간을 보장한다.
                CancelLegacyMotion();
                var remaining = _lastMoveDurationMs - _lastLegacyMove.ElapsedMilliseconds;
                if (_lastLegacyMove.IsRunning && remaining > 0) Thread.Sleep((int)remaining);
                Legacy(HandyLegacyProtocol.Command.Ping);
                return true;
            }
            if (_streamMode && Call(Method.ModeSet, CancellationToken.None).UInt(1) != 0)
                throw new InvalidOperationException("핸디가 위치 버퍼 모드를 끝내지 못했습니다.");
            Call(Method.Stop, CancellationToken.None);
            RequirePlayState(Call(Method.State, CancellationToken.None), 0);
            _streamMode = false;
            return true;
        }

        private void MoveLegacy(int position, CancellationToken cancellation, int durationMs) {
            if (position is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(position));
            if (durationMs <= 0) throw new ArgumentOutOfRangeException(nameof(durationMs));
            cancellation.ThrowIfCancellationRequested();
            _lastMoveDurationMs = durationMs;
            try { Legacy(HandyLegacyProtocol.Command.Linear, position / 100.0, (uint)durationMs); }
            finally { _lastLegacyMove.Restart(); } // 응답이 유실돼도 이미 전송한 이동은 끝날 때까지 고려한다.
        }

        private void CancelLegacyMotion() {
            _legacyMotion?.Cancel();
            _legacyMotion?.Dispose();
            _legacyMotion = null;
        }

        private Fields Legacy(HandyLegacyProtocol.Command command, double position = 0, uint durationMs = 0) =>
            HandyLegacyProtocol.Exchange(_link, NextId(), command, position, durationMs);

        private static uint NextId() => unchecked((uint)Interlocked.Increment(ref _nextId));

        private static bool Matches(float actual, float expected) => Math.Abs(actual - expected) < 0.001f;

        private static void RequirePlayState(Fields result, uint expected) {
            if (result.Message(1).UInt(1) != expected)
                throw new InvalidOperationException("기기가 요청한 동작 상태를 확인해 주지 않았습니다.");
        }

        private Fields Call(Method method, CancellationToken cancellation, params float[] settings) {
            var id = NextId();
            return Exchange(id, method, Request(id, method, settings), cancellation);
        }

        private Fields CallFields(Method method, CancellationToken cancellation, List<byte> fields) {
            var id = NextId();
            return Exchange(id, method, PackRequest(id, method, fields), cancellation);
        }

        private Fields Exchange(uint id, Method method, byte[] request, CancellationToken cancellation) {
            cancellation.ThrowIfCancellationRequested();
            _link.Write(request);
            // 전송한 명령은 취소되어도 응답까지 기다린다. 뒤늦은 응답이나 알림은 정지 확인에 쓰지 않는다.
            var timer = Stopwatch.StartNew();
            var buffer = new byte[512];
            while (timer.ElapsedMilliseconds < 8000) {
                var length = _link.Read(buffer, Math.Max(1, 8000 - (int)timer.ElapsedMilliseconds));
                if (TryResponse(buffer.AsMemory(0, length), id, method, out var result)) return result;
            }
            throw new TimeoutException("블루투스 기기 응답 시간이 초과되었습니다.");
        }

        private Task<T> OnLink<T>(Func<T> action, bool allowOpen, CancellationToken cancellation) => Task.Run(() => {
            lock (_gate) {
                if (_disposed != 0) throw new ObjectDisposedException(nameof(HandyBleClient));
                cancellation.ThrowIfCancellationRequested();
                try {
                    if (_link?.Lost == true) {
                        _connectionFailed = true;
                        CloseLink();
                    }
                    if (_link != null) return action();
                    if (!allowOpen) throw new InvalidOperationException("선택한 기기의 블루투스 연결을 다시 확인하세요.");
                    OpenSelected();
                    cancellation.ThrowIfCancellationRequested();
                    return action();
                } catch {
                    // 다음 명시적인 연결 확인이나 정지에서 같은 주소만 다시 열 수 있다.
                    if (allowOpen) CloseLink();
                    throw;
                }
            }
        }, cancellation);

        private void OpenSelected() {
            _link = _openSelected();
            if (_selectedLegacy.HasValue && _selectedLegacy.Value != _link.IsLegacy)
                throw new InvalidOperationException("선택한 핸디의 BLE 모드가 바뀌었습니다. 원래 연결 모드를 확인하세요.");
            _selectedLegacy = _link.IsLegacy;
            if (UsesLegacy) {
                var server = Legacy(HandyLegacyProtocol.Command.ServerInfo);
                _legacyName = server.Text(2);
                if (string.IsNullOrWhiteSpace(_legacyName)) _legacyName = "The Handy";
                var maxPingMs = server.UInt(4);
                if (maxPingMs > 0) {
                    var interval = Math.Max(1, (int)(maxPingMs / 2));
                    _heartbeat = new Timer(PingLegacy, _link, interval, interval);
                }
            }
            _connectionFailed = false;
        }

        private void PingLegacy(object link) {
            if (!Monitor.TryEnter(_gate)) return;
            try {
                if (_disposed != 0 || !ReferenceEquals(_link, link)) return;
                Legacy(HandyLegacyProtocol.Command.Ping);
            } catch (Exception) {
                _connectionFailed = true;
                CloseLink();
            } finally { Monitor.Exit(_gate); }
        }

        private void CloseLink() {
            CancelLegacyMotion();
            _heartbeat?.Dispose();
            _heartbeat = null;
            _link?.Dispose();
            _link = null;
        }

        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stream.Cancel();
            // 씬을 떠나는 동안 진행 중인 Windows GATT 호출이 Unity 스레드를 막지 않게 한다.
            if (Monitor.TryEnter(_gate)) {
                try { CloseLink(); } finally { Monitor.Exit(_gate); }
            } else {
                _ = Task.Run(() => { lock (_gate) CloseLink(); });
            }
        }
    }
}
