using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace HandyDeviceControl.Tests.Editor {
    public sealed class HandyApiClientTests : HandyApiTestFixture {
        [TestCase(true)]
        [TestCase(false)]
        public void FindsFirmware3BeforeSelectingTheControlApi(bool visibleToV3) {
            Server.Expect("GET", V3 + "connected", visibleToV3 ? V3Online : "{\"result\":{\"connected\":false}}");
            if (visibleToV3)
                Server.Expect("GET", V3 + "info", "{\"result\":{\"fw_version\":\"3.2.0\",\"fw_status\":2}}");
            ExpectFirmware3();
            using var client = new HandyApiClient("app", "device", Http);
            Assert.That(client.CheckConnectionAsync(CancellationToken.None).GetAwaiter().GetResult(), Is.EqualTo("3.2.0"));
            // 이후 연결 확인도 같은 API만 사용한다.
            ExpectFirmware3();
            Assert.That(client.CheckConnectionAsync(CancellationToken.None).GetAwaiter().GetResult(), Is.EqualTo("3.2.0"));
            Server.Verify();
        }

        [TestCase(HttpStatusCode.Unauthorized, "{}")]
        [TestCase(HttpStatusCode.OK, "{\"error\":{\"code\":1002,\"connected\":false}}")]
        public void ChecksV2FirmwareWhenV3CannotIdentifyTheDevice(HttpStatusCode status, string response) {
            Server.Expect("GET", V3 + "connected", response, status: status);
            ExpectFirmware3();
            using var client = new HandyApiClient("app", "device", Http);
            Assert.That(client.CheckConnectionAsync(CancellationToken.None).GetAwaiter().GetResult(), Is.EqualTo("3.2.0"));
            Server.Verify();
        }

        [TestCase("{\"fwVersion\":\"3.2.0\",\"fwStatus\":1}")]
        [TestCase("{\"fwVersion\":\"2.13.0\",\"fwStatus\":2}")]
        [TestCase("{\"fwVersion\":\"3.2.0\"}")]
        public void RejectsUnsupportedOrIncompleteV2Info(string info) {
            Server.Expect("GET", V2 + "connected", Online);
            Server.Expect("GET", V2 + "info", info);
            using var client = new HandyApiClient("", "device", Http);
            Assert.Throws<InvalidOperationException>(() => client.CheckConnectionAsync(CancellationToken.None).GetAwaiter().GetResult());
            Server.Verify();
        }

        [Test] public void PositionTestUsesPercentOfStrokeAndStopsOnTarget() {
            ExpectFirmware3();
            Server.Expect("PUT", V2 + "mode", Success, "{\"mode\":2}");
            ExpectMove(40);
            ExpectMove(60);
            var last = ExpectMove(40);
            ExpectV2Stop();
            using var client = new HandyApiClient("", "device", Http);
            client.StartTestAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.That(last.Wait(4000), Is.True, "The return move was not issued.");
            client.StopAsync().GetAwaiter().GetResult();
            Server.Verify();
        }

        [Test] public void StopWaitsForTheSentMoveAndCancelsTheReturnMove() {
            ExpectFirmware3();
            Server.Expect("PUT", V2 + "mode", Success, "{\"mode\":2}");
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = ExpectMove(40, release.Task);
            ExpectV2Stop();
            using var client = new HandyApiClient("", "device", Http);
            var start = client.StartTestAsync(CancellationToken.None);
            try {
                Assert.That(sent.Wait(2000), Is.True);
                var stop = client.StopAsync();
                Assert.That(stop.IsCompleted, Is.False, "Stop overtook an outstanding position request.");
                release.TrySetResult(true);
                Task.WhenAll(start, stop).GetAwaiter().GetResult();
            } finally { release.TrySetResult(true); }
            Server.Verify();
        }

        [Test] public void StopDuringModeChangePreventsTheFirstMove() {
            ExpectFirmware3();
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = Server.Expect("PUT", V2 + "mode", Success, "{\"mode\":2}", release.Task);
            ExpectV2Stop();
            using var client = new HandyApiClient(null, "device", Http);
            var start = client.StartTestAsync(CancellationToken.None);
            try {
                Assert.That(sent.Wait(2000), Is.True);
                var stop = client.StopAsync();
                Assert.That(stop.IsCompleted, Is.False);
                release.TrySetResult(true);
                Assert.Catch<OperationCanceledException>(() => start.GetAwaiter().GetResult());
                stop.GetAwaiter().GetResult();
            } finally { release.TrySetResult(true); }
            Server.Verify();
        }

        [TestCase("{\"result\":1}")]
        [TestCase("{\"result\":3}")]
        public void UnfinishedPositionFailsAndStopUsesTheSameApi(string reply) {
            ExpectFirmware3();
            Server.Expect("PUT", V2 + "mode", Success, "{\"mode\":2}");
            ExpectMove(40, response: reply);
            ExpectV2Stop();
            using var client = new HandyApiClient(null, "device", Http);
            Assert.Throws<InvalidOperationException>(() => client.StartTestAsync(CancellationToken.None).GetAwaiter().GetResult());
            client.StopAsync().GetAwaiter().GetResult();
            Server.Verify();
        }

        [TestCase("{\"state\":1}")]
        [TestCase("{\"result\":0,\"state\":2}")]
        [TestCase("{\"result\":0,\"state\":0}")]
        [TestCase("{\"error\":{\"code\":3000}}")]
        public void RetryStopOnTheSameApiAfterMissingOrIncorrectConfirmation(string state) {
            ExpectFirmware3();
            ExpectV2Stop(state);
            ExpectV2Stop();
            using var client = new HandyApiClient(null, "device", Http);
            client.CheckConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.Throws<InvalidOperationException>(() => client.StopAsync().GetAwaiter().GetResult());
            client.StopAsync().GetAwaiter().GetResult();
            Server.Verify();
        }

        [Test] public void Firmware4KeepsItsV3HampCommandsAndCredentials() {
            Server.Expect("GET", V3 + "connected", V3Online);
            Server.Expect("GET", V3 + "info", "{\"result\":{\"fw_version\":\"4.4.0\",\"fw_status\":1}}");
            Server.Expect("GET", V3 + "hamp/state", "{\"result\":{}}");
            Server.Expect("PUT", V3 + "hamp/state", "{\"result\":{}}", "{\"velocity\":0.1,\"stroke\":{\"min\":0.4,\"max\":0.6}}");
            Server.Expect("PUT", V3 + "hamp/start", "{\"result\":{\"play_state\":1}}");
            Server.Expect("PUT", V3 + "hamp/stop", "{\"result\":{}}");
            Server.Expect("GET", V3 + "hamp/state", "{\"result\":{\"play_state\":0}}");
            using var client = new HandyApiClient("app", "device", Http);
            Assert.That(client.CheckConnectionAsync(CancellationToken.None).GetAwaiter().GetResult(), Is.EqualTo("4.4.0"));
            client.StartTestAsync(CancellationToken.None).GetAwaiter().GetResult();
            client.StopAsync().GetAwaiter().GetResult();
            Server.Verify();
        }

        [TestCase(4)]
        [TestCase(3)]
        public void GameEventUsesItsOwnRangeAndStopsOnTheSameConnection(int firmware) {
            const int durationMs = 2000;
            if (firmware == 4) {
                Server.Expect("GET", V3 + "connected", V3Online);
                Server.Expect("GET", V3 + "info", "{\"result\":{\"fw_version\":\"4.4.0\",\"fw_status\":1}}");
                Server.Expect("GET", V3 + "hamp/state", "{\"result\":{}}");
                Server.Expect("PUT", V3 + "hamp/state", "{\"result\":{}}", "{\"velocity\":0.1,\"stroke\":{\"min\":0.25,\"max\":0.75}}");
                Server.Expect("PUT", V3 + "hamp/start", "{\"result\":{\"play_state\":1}}");
                Server.Expect("PUT", V3 + "hamp/stop", "{\"result\":{}}");
                Server.Expect("GET", V3 + "hamp/state", "{\"result\":{\"play_state\":0}}");
            } else {
                ExpectFirmware3();
                Server.Expect("PUT", V2 + "mode", Success, "{\"mode\":2}");
                ExpectMove(25);
                ExpectMove(75);
                ExpectMove(25);
                ExpectV2Stop();
            }
            Task.Run(async () => {
                using var session = new HandySession(new HandyApiClient(firmware == 4 ? "app" : "", "device", Http));
                await session.CheckAsync(CancellationToken.None);
                Assert.That(session.State, Is.EqualTo(HandySessionState.Ready));
                await session.RunStrokeAsync(25, 75, durationMs);
                Assert.That(session.State, Is.EqualTo(HandySessionState.Ready), session.Detail);
                Assert.That(session.Detail, Does.Contain("지정 시간 종료"));
            }).GetAwaiter().GetResult();
            Server.Verify();
        }

        [TestCase(-1, 75, 500)]
        [TestCase(50, 50, 500)]
        [TestCase(20, 101, 500)]
        [TestCase(20, 80, 0)]
        public void RejectsInvalidGameMotionBeforeSending(int min, int max, int durationMs) {
            using var session = new HandySession(new HandyApiClient("app", "device", Http));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.RunStrokeAsync(min, max, durationMs).GetAwaiter().GetResult());
            Server.Verify();
        }

        [Test] public void TwoGameEventsAwaitTheSamePendingStop() {
            ExpectFirmware3();
            var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sent = Server.Expect("PUT", V2 + "mode", Success, "{\"mode\":0}", reply.Task);
            Server.Expect("PUT", V2 + "hamp/stop", Success);
            Server.Expect("GET", V2 + "hamp/state", "{\"result\":0,\"state\":1}");
            Task.Run(async () => {
                using var session = new HandySession(new HandyApiClient("", "device", Http));
                await session.CheckAsync(CancellationToken.None);
                var first = session.StopAsync();
                try {
                    Assert.That(sent.IsCompleted, Is.True);
                    var second = session.StopAsync();
                    Assert.That(second.IsCompleted, Is.False);
                    reply.TrySetResult(true);
                    await Task.WhenAll(first, second);
                    Assert.That(session.State, Is.EqualTo(HandySessionState.Ready));
                } finally { reply.TrySetResult(true); }
            }).GetAwaiter().GetResult();
            Server.Verify();
        }

        private Task ExpectMove(int percent, Task wait = null, string response = Success) => Server.Expect("PUT", V2 + "hdsp/xpt", response,
            $"{{\"position\":{percent},\"duration\":500,\"stopOnTarget\":true,\"immediateResponse\":false}}", wait);
    }
}
