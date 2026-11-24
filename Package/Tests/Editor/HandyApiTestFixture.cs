using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace HandyDeviceControl.Tests.Editor {
    public abstract class HandyApiTestFixture {
        protected const string V2 = "/api/handy/v2/";
        protected const string V3 = "/api/handy-rest/v3/";
        protected const string Success = "{\"result\":0}";
        protected const string Online = "{\"connected\":true}";
        protected const string V3Online = "{\"result\":{\"connected\":true}}";
        private const string Firmware3 = "{\"fwVersion\":\"3.2.0\",\"fwStatus\":2}";
        protected ScriptedServer Server { get; private set; }
        protected HttpClient Http { get; private set; }

        [SetUp] public void SetUp() {
            Server = new ScriptedServer();
            Http = new HttpClient(Server);
        }

        [TearDown] public void TearDown() => Http.Dispose();

        protected void ExpectFirmware3() {
            Server.Expect("GET", V2 + "connected", Online);
            Server.Expect("GET", V2 + "info", Firmware3);
        }

        protected void ExpectV2Stop(string state = "{\"result\":0,\"state\":1}") {
            Server.Expect("PUT", V2 + "mode", "{\"result\":1}", "{\"mode\":0}");
            Server.Expect("PUT", V2 + "hamp/stop", "{\"result\":1}");
            Server.Expect("GET", V2 + "hamp/state", state);
        }

        protected sealed class ScriptedServer : HttpMessageHandler {
            private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _expected = new();
            private int _unexpected;

            public Task Expect(string method, string path, string response, string body = null, Task wait = null,
                HttpStatusCode status = HttpStatusCode.OK, Action<string> checkBody = null) {
                var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _expected.Enqueue(async request => {
                    Assert.That(request.RequestUri.AbsoluteUri, Is.EqualTo("https://www.handyfeeling.com" + path));
                    Assert.That(request.Method.Method, Is.EqualTo(method));
                    Assert.That(request.Headers.GetValues("X-Connection-Key").Single(), Is.EqualTo("device"));
                    var hasAppId = request.Headers.TryGetValues("X-Api-Key", out var ids);
                    Assert.That(hasAppId, Is.EqualTo(path.StartsWith(V3, StringComparison.Ordinal)));
                    if (hasAppId) Assert.That(ids.Single(), Is.EqualTo("app"));
                    var actual = request.Content == null ? null : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (checkBody != null) checkBody(actual);
                    else Assert.That(actual, Is.EqualTo(body));
                    sent.TrySetResult(true);
                    if (wait != null) await wait.ConfigureAwait(false);
                    return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
                });
                return sent.Task;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) {
                if (_expected.Count != 0) return _expected.Dequeue()(request);
                Interlocked.Increment(ref _unexpected);
                throw new AssertionException("Unexpected API request: " + request.RequestUri.AbsolutePath);
            }

            public void Verify() {
                Assert.That(_expected, Is.Empty);
                Assert.That(_unexpected, Is.Zero);
            }
        }
    }
}
