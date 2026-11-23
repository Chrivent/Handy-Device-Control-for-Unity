using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace HandyDeviceControl {
    public enum HandySessionState { Idle, Checking, Ready, Starting, Running, Stopping, StopUnconfirmed }

    public sealed class HandySession : IDisposable {
        private readonly IHandyClient _client;
        private Task PositionMotionCompletion => (_client as IHandyPositionClient)?.PositionMotionCompletion;
        public bool UsesLegacyBluetooth => _client is HandyBleClient { UsesLegacy: true };
        public bool UsesPositionTest => PositionMotionCompletion != null;
        private CancellationTokenSource _motionCancellation;
        private Task _motionRequest = Task.CompletedTask;
        private TaskCompletionSource<bool> _stopCompletion;

        public event Action Changed;
        public HandySessionState State { get; private set; }
        public string Detail { get; private set; }
        public long? LastRequestMs { get; private set; }
        public bool CanConfigure => State is HandySessionState.Idle or HandySessionState.Ready;
        public bool MayBeMoving => State is HandySessionState.Starting or HandySessionState.Running or HandySessionState.StopUnconfirmed;
        public bool CanStop => State == HandySessionState.Ready || MayBeMoving;

        public HandySession(IHandyClient client) {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public void Dispose() => (_client as IDisposable)?.Dispose();

        public void CheckBluetoothConnection() {
            if (_client is HandyBleClient { ConnectionLost: true }) ReportDisconnected();
        }

        private void ReportDisconnected() {
            switch (State) {
                case HandySessionState.Ready:
                    SetState(HandySessionState.Idle, "선택한 기기와 연결이 끊겼습니다. 연결을 다시 확인하세요.");
                    break;
                case HandySessionState.Starting or HandySessionState.Running:
                    _motionCancellation?.Cancel();
                    SetState(HandySessionState.StopUnconfirmed, "연결이 끊겨 정지를 확인할 수 없습니다. 기기 버튼으로 멈추고 연결 복구 후 정지를 다시 누르세요.");
                    break;
            }
        }

        public async Task CheckAsync(CancellationToken cancellation) {
            if (!CanConfigure) return;
            SetState(HandySessionState.Checking, "기기 연결을 확인하고 있습니다.");
            var timer = Stopwatch.StartNew();
            try {
                var info = await _client.CheckConnectionAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
                SetState(HandySessionState.Ready, UsesLegacyBluetooth ? $"연결 확인 · {info}" : $"연결 확인 · 펌웨어 {info}", timer);
            } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
                SetState(HandySessionState.Idle, "연결 확인을 취소했습니다.");
            } catch (Exception error) {
                SetState(HandySessionState.Idle, error.Message, timer);
            }
        }

        public Task RunTestAsync() => RunStrokeAsync(HandyStroke.Test);

        /// <summary>지정한 구간을 왕복하고 시간이 지나면 같은 기기에 정지를 요청한다. Unity 메인 스레드에서 호출한다.</summary>
        public Task RunStrokeAsync(int minPercent, int maxPercent, int durationMs) =>
            RunStrokeAsync(new HandyStroke(minPercent, maxPercent, durationMs));

        private async Task RunStrokeAsync(HandyStroke stroke) {
            if (State != HandySessionState.Ready) return;
            using var motion = new CancellationTokenSource();
            _motionCancellation = motion;
            var timer = Stopwatch.StartNew();
            var failure = "시작 확인 실패";
            try {
                _motionRequest = _client.StartStrokeAsync(stroke, motion.Token);
                SetState(HandySessionState.Starting, "이동 구간을 설정하고 시작 응답을 기다립니다.");
                await _motionRequest;
                if (motion.IsCancellationRequested) return;
                var positions = PositionMotionCompletion;
                failure = "동작 중 통신 실패";
                SetState(HandySessionState.Running, $"시작 확인 · {stroke.MinPercent}–{stroke.MaxPercent}% 구간을 최대 {stroke.DurationMs / 1000.0:g}초 동안 왕복합니다.", timer);
                if (positions != null) await positions;
                else await Task.Delay(stroke.DurationMs, motion.Token);
                if (!motion.IsCancellationRequested) await StopAsync(stroke == HandyStroke.Test ? "2초 테스트 종료" : "지정 시간 종료");
            } catch (OperationCanceledException) when (motion.IsCancellationRequested) {
                // 수동 정지나 연결 끊김에서 이미 상태를 전환했다.
            } catch (Exception error) {
                if (motion.IsCancellationRequested) return;
                if (_client is HandyBleClient { UsesLegacy: true, ConnectionLost: true }) ReportDisconnected();
                else await StopAsync(failure + " · " + error.Message);
            } finally {
                if (_motionCancellation == motion) _motionCancellation = null;
            }
        }

        /// <summary>기기가 준비됐을 때 Unity 스레드에서 onStarted를 호출하고, 전송이 끝나면 정지한다.</summary>
        public async Task RunMotionAsync(Func<IHandyClient, Action, CancellationToken, Task> send, Action onStarted = null) {
            if (send == null) throw new ArgumentNullException(nameof(send));
            if (State != HandySessionState.Ready) return;
            using var playback = new CancellationTokenSource();
            _motionCancellation = playback;
            try {
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _motionRequest = send(_client, () => ready.TrySetResult(true), playback.Token);
                SetState(HandySessionState.Starting, "핸디가 위치 전송을 준비하고 있습니다.");
                await Task.WhenAny(ready.Task, _motionRequest);
                if (playback.IsCancellationRequested) return;
                if (ready.Task.IsCompleted) {
                    onStarted?.Invoke();
                    if (playback.IsCancellationRequested) return;
                    SetState(HandySessionState.Running, "기기에 위치를 전송하고 있습니다.");
                }
                await _motionRequest;
                if (!playback.IsCancellationRequested) await StopAsync("위치 전송 종료");
            } catch (OperationCanceledException) when (playback.IsCancellationRequested) {
                // 정지와 연결 해제는 각 호출자가 처리한다.
            } catch (Exception error) {
                if (!playback.IsCancellationRequested) await StopAsync("위치 전송 중단 · " + error.Message);
            } finally {
                if (_motionCancellation == playback) _motionCancellation = null;
            }
        }

        public async Task StopAsync(string reason = "수동 정지") {
            if (State == HandySessionState.Stopping) {
                await _stopCompletion.Task;
                return;
            }
            if (!CanStop) return;
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopCompletion = stopped;
            var legacy = UsesLegacyBluetooth;
            var timer = Stopwatch.StartNew();
            try {
                SetState(HandySessionState.Stopping, reason + (legacy ? " · 위치 명령을 끝내고 기기 연결을 확인합니다." : " · 기기의 정지 응답을 기다립니다."));
                _motionCancellation?.Cancel();
                await StopAfterMotionRequestAsync();
                SetState(HandySessionState.Ready, reason + (legacy ? " · 위치 명령 전송 종료. 실제 정지는 기기에서 확인하세요." : " · 정지 확인"), timer);
            } catch (Exception error) {
                SetState(HandySessionState.StopUnconfirmed, error.Message + " 기기 버튼으로 멈추고 연결 복구 후 정지를 다시 누르세요.", timer);
            } finally {
                if (_stopCompletion == stopped) _stopCompletion = null;
                stopped.TrySetResult(true);
            }
        }

        private async Task StopAfterMotionRequestAsync() {
            // 씬이 비활성화돼도 전송 중인 시작 요청 뒤에 정지 명령이 도착해야 한다.
            try {
                await _motionRequest.ConfigureAwait(false);
            } catch (Exception) {
                // 시작 응답이 유실돼도 기기는 이미 움직이고 있을 수 있다.
            }
            await _client.StopAsync().ConfigureAwait(false);
        }

        private void SetState(HandySessionState state, string detail, Stopwatch timer = null) {
            State = state;
            Detail = detail;
            if (timer != null) LastRequestMs = timer.ElapsedMilliseconds;
            Changed?.Invoke();
        }
    }
}
