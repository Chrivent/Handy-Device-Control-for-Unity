using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HandyDeviceControl {
    /// <summary>한 Connection Key의 펌웨어를 확인한 뒤 해당 REST API로 동작과 정지를 처리한다.</summary>
    public sealed partial class HandyApiClient : IHandyClient, IHandyStreamingClient, IHandyPositionClient, IDisposable {
        private enum Api { Unknown, V2, V3 }
        private const string V3Url = "https://www.handyfeeling.com/api/handy-rest/v3/";
        private const string V2Url = "https://www.handyfeeling.com/api/handy/v2/";
        private static readonly HttpClient SharedHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private readonly HttpClient _http;
        private readonly string _applicationId;
        private readonly string _connectionKey;
        private readonly SemaphoreSlim _commands = new(1, 1);
        private readonly HandyMotionStream _stream = new();
        private readonly object _motionLock = new();
        private CancellationTokenSource _strokeCancellation;
        private volatile Api _api;
        private bool _streamMode;
        private volatile bool _disposed;

        public HandyApiClient(string applicationId, string connectionKey, HttpClient http = null) {
            _applicationId = applicationId?.Trim();
            _connectionKey = connectionKey?.Trim();
            _http = http ?? SharedHttp;
        }

        public async Task<string> CheckConnectionAsync(CancellationToken cancellation) {
            await _commands.WaitAsync(cancellation).ConfigureAwait(false);
            try { return await IdentifyAsync(cancellation).ConfigureAwait(false); }
            finally { _commands.Release(); }
        }

        private async Task<string> IdentifyAsync(CancellationToken cancellation) {
            if (_api == Api.V2 || string.IsNullOrEmpty(_applicationId))
                return await CheckV2Async(cancellation).ConfigureAwait(false);

            DeviceResult info = null;
            try {
                var connection = await RequestV3Async(HttpMethod.Get, "connected", cancellation).ConfigureAwait(false);
                if (connection.Connected == null) throw InvalidReply();
                if (connection.Connected.Value)
                    info = await RequestV3Async(HttpMethod.Get, "info", cancellation).ConfigureAwait(false);
            } catch (Exception error) when (_api == Api.Unknown && error is ApiAccessException or DeviceOfflineException) {
                // 펌웨어 3은 Application ID를 사용하지 않는다. v2에서 실제 3.x를 확인해야 선택할 수 있다.
                return await CheckV2Async(cancellation, error as ApiAccessException).ConfigureAwait(false);
            }
            if (_api == Api.Unknown && (info == null || Major(info.FirmwareVersion) == 3))
                return await CheckV2Async(cancellation).ConfigureAwait(false);
            if (info == null) throw Offline();
            var major = Major(info.FirmwareVersion);
            if (info.FirmwareStatus is not (0 or 1 or 2)) throw InvalidReply();
            if (major < 4 || info.FirmwareStatus == 2) throw UpdateRequired(info.FirmwareVersion);
            cancellation.ThrowIfCancellationRequested();
            _api = Api.V3;
            return info.FirmwareVersion;
        }

        public Task StartTestAsync(CancellationToken cancellation) => StartStrokeAsync(HandyStroke.Test, cancellation);

        public async Task StartStrokeAsync(HandyStroke stroke, CancellationToken cancellation) {
            if (stroke == null) throw new ArgumentNullException(nameof(stroke));
            CancellationToken motion;
            lock (_motionLock) {
                ThrowIfDisposed();
                cancellation.ThrowIfCancellationRequested();
                CancelStroke();
                _strokeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                motion = _strokeCancellation.Token;
            }
            await WithCommandsAsync(async () => {
                if (_api == Api.Unknown) await IdentifyAsync(motion).ConfigureAwait(false);
                if (_api == Api.V2) {
                    await StartV2Async(stroke, motion).ConfigureAwait(false);
                    return;
                }
                // 이미 보낸 설정과 시작의 응답은 정지보다 먼저 처리한다.
                await RequestV3Async(HttpMethod.Get, "hamp/state", motion).ConfigureAwait(false);
                motion.ThrowIfCancellationRequested();
                var settings = FormattableString.Invariant($"{{\"velocity\":0.1,\"stroke\":{{\"min\":{stroke.MinPercent / 100m},\"max\":{stroke.MaxPercent / 100m}}}}}");
                await RequestV3Async(HttpMethod.Put, "hamp/state", CancellationToken.None, settings).ConfigureAwait(false);
                motion.ThrowIfCancellationRequested();
                var start = await RequestV3Async(HttpMethod.Put, "hamp/start", CancellationToken.None).ConfigureAwait(false);
                RequirePlayState(start, 1);
            }, motion).ConfigureAwait(false);
        }

        public Task StreamAsync(IHandyMotionSource source, CancellationToken cancellation) =>
            _stream.RunAsync(token => SendMotionAsync(source, token), cancellation);

        private async Task SendMotionAsync(IHandyMotionSource source, CancellationToken cancellation) {
            if (source == null) throw new ArgumentNullException(nameof(source));
            await WithCommandsAsync(async () => {
                switch (_api) {
                    case Api.V2:
                        await SetV2ModeAsync(2, cancellation).ConfigureAwait(false);
                        break;
                    case Api.V3:
                        _streamMode = true;
                        await RequestV3Async(HttpMethod.Put, "hsp/setup", CancellationToken.None, "{}").ConfigureAwait(false);
                        break;
                    case Api.Unknown:
                    default:
                        throw new InvalidOperationException("먼저 기기 연결을 확인하세요.");
                }
            }, cancellation).ConfigureAwait(false);

            cancellation.ThrowIfCancellationRequested();
            if (_api == Api.V2) {
                await source.SendPositionsAsync((pos, ms) =>
                    WithCommandsAsync(() => MoveV2Async(pos, cancellation, ms), cancellation), cancellation).ConfigureAwait(false);
            } else {
                await source.SendBufferedAsync(points => AddPointsAsync(points, cancellation),
                    (ms, start) => SetStreamTimeAsync(ms, start, cancellation), cancellation).ConfigureAwait(false);
            }
        }

        private Task AddPointsAsync(IReadOnlyList<HandyMotionPoint> points, CancellationToken cancellation) {
            var body = new StringBuilder("{\"points\":[");
            for (var i = 0; i < points.Count; i++) {
                if (i > 0) body.Append(',');
                body.AppendFormat(CultureInfo.InvariantCulture, "{{\"t\":{0},\"x\":{1}}}", points[i].TimeMs, points[i].Position);
            }
            body.Append("]}");
            return WithCommandsAsync(() => RequestV3Async(HttpMethod.Put, "hsp/add", CancellationToken.None, body.ToString()), cancellation);
        }

        private Task SetStreamTimeAsync(int timeMs, bool start, CancellationToken cancellation) => WithCommandsAsync(async () => {
            var path = start ? "hsp/play" : "hsp/synctime";
            var body = start ? FormattableString.Invariant($"{{\"start_time\":{timeMs},\"playback_rate\":1,\"loop\":false}}")
                : FormattableString.Invariant($"{{\"current_time\":{timeMs},\"filter\":0.5}}");
            var reply = await RequestV3Async(HttpMethod.Put, path, CancellationToken.None, body).ConfigureAwait(false);
            if (start) RequirePlayState(reply, 1);
        }, cancellation);

        public Task StopAsync() {
            _stream.Cancel();
            lock (_motionLock) CancelStroke();
            return WithCommandsAsync(async () => {
                if (_api == Api.Unknown) await IdentifyAsync(CancellationToken.None).ConfigureAwait(false);
                if (_api == Api.V2) {
                    await StopV2Async().ConfigureAwait(false);
                    return;
                }
                if (_streamMode) {
                    // 스트림을 벗어나 버퍼에 남은 위치를 무효화하고, 기존 시험과 같은 방식으로 정지를 확인한다.
                    var mode = await RequestAsync<ModeReply>(Api.V3, HttpMethod.Put, "mode", CancellationToken.None, "{\"mode\":0}").ConfigureAwait(false);
                    if (mode.Result != "ok") throw InvalidReply();
                }
                await RequestV3Async(HttpMethod.Put, "hamp/stop", CancellationToken.None).ConfigureAwait(false);
                var state = await RequestV3Async(HttpMethod.Get, "hamp/state", CancellationToken.None).ConfigureAwait(false);
                RequirePlayState(state, 0);
                _streamMode = false;
            }, CancellationToken.None);
        }

        private static void RequirePlayState(DeviceResult result, int expected) {
            if (result.PlayState != expected) {
                throw new InvalidOperationException("기기가 요청한 동작 상태를 확인해 주지 않았습니다.");
            }
        }

        private async Task WithCommandsAsync(Func<Task> action, CancellationToken cancellation) {
            await _commands.WaitAsync(cancellation).ConfigureAwait(false);
            try {
                cancellation.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                await action().ConfigureAwait(false);
            } finally { _commands.Release(); }
        }

        private static int Major(string version) =>
            int.TryParse(version?.Trim().TrimStart('v', 'V').Split('.')[0], out var major) && major > 0
                ? major : throw InvalidReply();

        private static InvalidOperationException InvalidReply() => new("기기 응답 형식을 확인할 수 없습니다.");
        private static DeviceOfflineException Offline() => new();
        private static InvalidOperationException UpdateRequired(string version) =>
            new($"펌웨어 {version} · 기기 업데이트 후 다시 연결하세요.");

        private async Task<DeviceResult> RequestV3Async(HttpMethod method, string path, CancellationToken cancellation, string body = null) {
            var reply = await RequestAsync<DeviceReply>(Api.V3, method, path, cancellation, body).ConfigureAwait(false);
            return reply.Result ?? throw InvalidReply();
        }

        private async Task<T> RequestAsync<T>(Api api, HttpMethod method, string path, CancellationToken cancellation, string body = null)
            where T : Reply {
            cancellation.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            using var request = new HttpRequestMessage(method, (api == Api.V2 ? V2Url : V3Url) + path);
            if (string.IsNullOrEmpty(_connectionKey)) {
                throw new InvalidOperationException("기기의 Connection Key를 입력하세요.");
            }
            try {
                if (api == Api.V3) request.Headers.Add("X-Api-Key", _applicationId);
                request.Headers.Add("X-Connection-Key", _connectionKey);
            } catch (FormatException) {
                throw new InvalidOperationException("핸디 연결 정보의 형식이 올바르지 않습니다.");
            }
            if (body != null) {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            try {
                using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) {
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        throw new ApiAccessException(response.StatusCode);
                    var hint = response.StatusCode switch {
                        (HttpStatusCode)429 => "잠시 뒤 다시 시도하세요.",
                        _ => "서버 요청에 실패했습니다."
                    };
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode} · {hint}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var serializer = new DataContractJsonSerializer(typeof(T));
                var reply = (T)serializer.ReadObject(stream);
                // HTTP 200에서도 장치 오류가 반환된다. 키가 포함될 수 있는 응답 원문은 표시하지 않는다.
                if (reply?.Error == null) return reply ?? throw InvalidReply();
                if (reply.Error.Connected == false) throw Offline();
                throw new InvalidOperationException($"핸디 기기 오류 {reply.Error.Code}");
            } catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) {
                throw new TimeoutException("기기 응답 시간이 초과되었습니다.");
            } catch (HttpRequestException) {
                throw new InvalidOperationException("핸디 서버에 연결할 수 없습니다.");
            } catch (SerializationException) {
                throw new InvalidOperationException("기기 응답 형식을 읽을 수 없습니다.");
            }
        }

        public void Dispose() {
            _stream.Cancel();
            lock (_motionLock) {
                _disposed = true;
                CancelStroke();
            }
        }

        private void CancelStroke() {
            var active = _strokeCancellation;
            _strokeCancellation = null;
            active?.Cancel();
            active?.Dispose();
        }

        private void ThrowIfDisposed() {
            if (_disposed) throw new ObjectDisposedException(nameof(HandyApiClient));
        }

        private sealed class ApiAccessException : InvalidOperationException {
            internal ApiAccessException(HttpStatusCode status) : base($"HTTP {(int)status} · 핸디 서버가 이 요청을 승인하지 않았습니다.") { }
        }

        private sealed class DeviceOfflineException : InvalidOperationException {
            internal DeviceOfflineException() : base("기기가 오프라인입니다. Wi-Fi 연결과 Connection Key를 확인하세요.") { }
        }

        [DataContract]
        private abstract class Reply {
            [DataMember(Name = "error")] public DeviceError Error { get; set; }
        }

        [DataContract]
        private sealed class DeviceReply : Reply {
            [DataMember(Name = "result")] public DeviceResult Result { get; set; }
        }

        [DataContract]
        private sealed class ModeReply : Reply {
            [DataMember(Name = "result")] public string Result { get; set; }
        }

        [DataContract]
        private sealed class DeviceError {
            [DataMember(Name = "code")] public int Code { get; set; }
            [DataMember(Name = "connected")] public bool? Connected { get; set; }
        }

        [DataContract]
        private sealed class DeviceResult {
            [DataMember(Name = "connected")] public bool? Connected { get; set; }
            [DataMember(Name = "fw_version")] public string FirmwareVersion { get; set; }
            [DataMember(Name = "fw_status")] public int? FirmwareStatus { get; set; }
            // 필드가 누락된 응답을 정지 상태(0)와 구별한다.
            [DataMember(Name = "play_state")] public int? PlayState { get; set; }
        }
    }
}
