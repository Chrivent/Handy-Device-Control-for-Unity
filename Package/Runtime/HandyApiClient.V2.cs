using System;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HandyDeviceControl {
    public sealed partial class HandyApiClient {
        private volatile Task _positions = Task.CompletedTask;

        Task IHandyPositionClient.PositionMotionCompletion => _api == Api.V2 ? _positions : null;

        private async Task<string> CheckV2Async(CancellationToken cancellation, ApiAccessException v3AccessError = null) {
            var connection = await RequestV2Async(HttpMethod.Get, "connected", cancellation).ConfigureAwait(false);
            if (connection.Connected == null) throw InvalidReply();
            if (!connection.Connected.Value) {
                if (v3AccessError != null) throw v3AccessError;
                if (_api == Api.Unknown && string.IsNullOrEmpty(_applicationId))
                    throw new InvalidOperationException("온라인 상태의 펌웨어 3 기기를 찾지 못했습니다. Wi-Fi와 Connection Key를 확인하세요. 펌웨어 4에는 게임의 Application ID도 필요합니다.");
                throw Offline();
            }

            var info = await RequestV2Async(HttpMethod.Get, "info", cancellation).ConfigureAwait(false);
            var major = Major(info.FirmwareVersion);
            if (major >= 4) {
                if (v3AccessError != null) throw v3AccessError;
                throw new InvalidOperationException("펌웨어 4 이상은 REST API v3를 사용합니다. 게임의 Application ID와 기기의 온라인 상태를 확인한 뒤 다시 연결하세요.");
            }
            if (info.FirmwareStatus is not (0 or 1 or 2)) throw InvalidReply();
            // v2의 /info는 1이 필수 업데이트, 2가 선택 업데이트다. v3는 두 값의 의미가 반대다.
            if (major != 3 || info.FirmwareStatus == 1) throw UpdateRequired(info.FirmwareVersion);
            cancellation.ThrowIfCancellationRequested();
            _api = Api.V2;
            return info.FirmwareVersion;
        }

        private async Task StartV2Async(HandyStroke stroke, CancellationToken motion) {
            // 목표 위치는 기기에 설정된 이동 범위의 백분율이다.
            await SetV2ModeAsync(2, motion).ConfigureAwait(false);
            motion.ThrowIfCancellationRequested();
            var window = Stopwatch.StartNew();
            await MoveV2Async(stroke.MinPercent, motion, stroke.MoveDurationMs).ConfigureAwait(false);
            _positions = stroke.FinishPositionsAsync(window, position => WithCommandsAsync(async () => {
                if (window.ElapsedMilliseconds + stroke.MoveDurationMs <= stroke.DurationMs)
                    await MoveV2Async(position, motion, stroke.MoveDurationMs).ConfigureAwait(false);
            }, motion), motion);
        }

        private async Task MoveV2Async(int position, CancellationToken cancellation, int durationMs) {
            if (position is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(position));
            if (durationMs <= 0) throw new ArgumentOutOfRangeException(nameof(durationMs));
            cancellation.ThrowIfCancellationRequested();
            var body = FormattableString.Invariant(
                $"{{\"position\":{position},\"duration\":{durationMs},\"stopOnTarget\":true,\"immediateResponse\":false}}");
            var reply = await RequestV2Async(HttpMethod.Put, "hdsp/xpt", CancellationToken.None, body).ConfigureAwait(false);
            // 0은 목표 도달, 2는 이미 목표 위치다. 서버 접수만으로 이동을 확인하지 않는다.
            if (reply.Result is not (0 or 2)) throw new InvalidOperationException("핸디가 요청한 위치에 도달했는지 확인할 수 없습니다.");
        }

        private async Task SetV2ModeAsync(int mode, CancellationToken cancellation) {
            cancellation.ThrowIfCancellationRequested();
            var body = FormattableString.Invariant($"{{\"mode\":{mode}}}");
            var reply = await RequestV2Async(HttpMethod.Put, "mode", CancellationToken.None, body).ConfigureAwait(false);
            if (reply.Result is not (0 or 1)) throw new InvalidOperationException("핸디가 요청한 동작 모드로 전환되지 않았습니다.");
        }

        private async Task StopV2Async() {
            // 위치 제어 모드에는 정지 상태 조회가 없다. 같은 기기를 왕복 모드로 전환해 정지시키고 상태를 읽는다.
            await SetV2ModeAsync(0, CancellationToken.None).ConfigureAwait(false);
            var stop = await RequestV2Async(HttpMethod.Put, "hamp/stop", CancellationToken.None).ConfigureAwait(false);
            if (stop.Result is not (0 or 1)) throw new InvalidOperationException("핸디가 정지 명령을 확인해 주지 않았습니다.");
            var state = await RequestV2Async(HttpMethod.Get, "hamp/state", CancellationToken.None).ConfigureAwait(false);
            if (state.Result != 0 || state.State != 1) throw new InvalidOperationException("핸디의 정지 상태를 확인할 수 없습니다.");
        }

        private Task<V2Reply> RequestV2Async(HttpMethod method, string path, CancellationToken cancellation, string body = null) =>
            RequestAsync<V2Reply>(Api.V2, method, path, cancellation, body);

        [DataContract]
        private sealed class V2Reply : Reply {
            [DataMember(Name = "connected")] public bool? Connected { get; set; }
            [DataMember(Name = "fwVersion")] public string FirmwareVersion { get; set; }
            [DataMember(Name = "fwStatus")] public int? FirmwareStatus { get; set; }
            [DataMember(Name = "result")] public int? Result { get; set; }
            [DataMember(Name = "state")] public int? State { get; set; }
        }
    }
}
