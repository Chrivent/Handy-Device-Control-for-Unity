using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace HandyDeviceControl {
    public sealed class HandyBleDevice {
        internal readonly ulong Address;
        internal readonly int AddressType;
        public string DisplayName { get; }

        internal HandyBleDevice(ulong address, int addressType, string name) {
            Address = address;
            AddressType = addressType;
            DisplayName = $"{(string.IsNullOrWhiteSpace(name) ? "Handy BLE" : name)}  ({address:X12})";
        }
    }

    internal interface IHandyBleLink : IDisposable {
        bool IsLegacy { get; }
        bool Lost { get; }
        void Write(byte[] packet);
        int Read(byte[] buffer, int timeoutMs);
    }

    internal static class HandyBleNative {
        private static PlatformNotSupportedException UnsupportedPlatform() =>
            new("블루투스 직접 연결은 Windows x64에서 사용할 수 있습니다.");

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
        private static readonly Guid ServiceId = new("77834d26-40f7-11ee-be56-0242ac120002");
        private static readonly Guid WriteId = new("77835032-40f7-11ee-be56-0242ac120002");
        private static readonly Guid NotifyId = new("77835410-40f7-11ee-be56-0242ac120002");
        private static readonly Guid LegacyServiceId = new("1775244d-6b43-439b-877c-060f2d9bed07");
        private static readonly Guid LegacySessionId = new("1775ff51-6b43-439b-877c-060f2d9bed07");
        private static readonly Guid LegacyCommandId = new("1775ff55-6b43-439b-877c-060f2d9bed07");
        private static readonly Guid AdapterStatics = new("8b02fb6a-ac4c-4741-8661-8eab7d17ea9f");
        private static readonly Guid WatcherInterface = new("a6ac336f-f3d3-4297-8d6c-c81ea6623f40");
        private static readonly Guid ReceivedArgs2 = new("12d9c87b-0399-5f0e-a348-53b02b6b162e");
        private static readonly Guid DeviceStatics2 = new("5f12c06b-3bac-43e8-ad16-563271bd41c2");
        private static readonly Guid Device3 = new("aee9e493-44ac-40dc-af33-b2c13c01ca46");
        private static readonly Guid Service3 = new("b293a950-0c53-437c-a9b3-5c3210c6e569");
        private const string Bluetooth = "Windows.Devices.Bluetooth.";
        [Flags]
        private enum CharacteristicProperties { Read = 2, WriteWithoutResponse = 4, Write = 8, Notify = 16, Indicate = 32 }

        internal static bool Supported => RuntimeInformation.ProcessArchitecture == Architecture.X64;

        internal static HandyBleDevice[] Scan(CancellationToken cancellation) {
            RequirePlatform();
            cancellation.ThrowIfCancellationRequested();
            using var apartment = WindowsRuntime.Apartment.Enter();
            using var factory = WindowsRuntime.Factory(Bluetooth + "BluetoothAdapter", AdapterStatics);
            using var getAdapter = factory.Object(8); // IBluetoothAdapterStatics.GetDefaultAsync
            using var adapter = getAdapter.Result(cancellation);
            if (adapter.Pointer == IntPtr.Zero || !adapter.Boolean(11)) throw WindowsRuntime.RadioUnavailable();

            using var watcher = WindowsRuntime.Activate(Bluetooth + "Advertisement.BluetoothLEAdvertisementWatcher", WatcherInterface);
            watcher.Set(12, 1); // IBluetoothLEAdvertisementWatcher.ScanningMode = Active
            var scanFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = new Dictionary<(ulong Address, int Type), (string Name, bool Matches)>();

            void End(Exception error) => scanFailure.TrySetResult(error);
            void Advertisement(IntPtr sender, IntPtr args) {
                var address = WindowsRuntime.Address(args, 7);
                using var extended = WindowsRuntime.Query(args, ReceivedArgs2);
                var type = extended.Integer(6); // BluetoothAddressType
                using var advertisement = WindowsRuntime.Object(args, 10);
                var name = advertisement.Text(8); // LocalName
                using var services = advertisement.Object(10); // ServiceUuids
                var matches = name.StartsWith("ohd_hw", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("The Handy", StringComparison.OrdinalIgnoreCase);
                var count = services.Integer(7);
                for (var i = 0; !matches && i < count; i++) {
                    var uuid = services.GuidAt(i);
                    matches = uuid == ServiceId || uuid == LegacyServiceId;
                }
                lock (seen) {
                    // 이름과 서비스 UUID가 서로 다른 광고 패킷에 도착할 수 있다.
                    seen.TryGetValue((address, type), out var previous);
                    seen[(address, type)] = (name.Length > 0 ? name : previous.Name, matches || previous.Matches);
                }
            }
            using var received = new WindowsBluetoothEvent(watcher, 19, typeof(IAdvertisementReceived), Advertisement, End);
            using var ended = new WindowsBluetoothEvent(watcher, 21, typeof(IWatcherStopped),
                (_, args) => End(WindowsRuntime.Integer(args, 6) is 5 or 8
                    ? WindowsRuntime.AccessDenied() : WindowsRuntime.RadioUnavailable()), End);
            watcher.Run(17); // Start
            try {
                var endedEarly = scanFailure.Task.Wait(4500, cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (endedEarly) throw scanFailure.Task.GetAwaiter().GetResult();
                lock (seen) {
                    return seen.Where(entry => entry.Value.Matches)
                        .OrderBy(entry => entry.Key.Address).ThenBy(entry => entry.Key.Type)
                        .Select(entry => new HandyBleDevice(entry.Key.Address, entry.Key.Type, entry.Value.Name)).ToArray();
                }
            } finally { watcher.Run(18); } // Stop
        }

        internal static IHandyBleLink Open(HandyBleDevice selected) {
            RequirePlatform();
            using var apartment = WindowsRuntime.Apartment.Enter();
            return new Link(selected);
        }

        private static void RequirePlatform() {
            if (!Supported) throw UnsupportedPlatform();
        }
        private static InvalidOperationException UnsupportedService() =>
            new("선택한 기기에서 핸디 제어용 BLE 서비스 또는 특성을 찾을 수 없습니다. 연결 모드를 확인하세요.");
        private static void CheckGatt(int status) {
            if (status == 0) return;
            throw status switch {
                1 => WindowsRuntime.Disconnected(),
                3 => WindowsRuntime.AccessDenied(),
                _ => new InvalidOperationException($"블루투스 GATT 요청에 실패했습니다 (상태 {status}).")
            };
        }

        private sealed class Link : IHandyBleLink {
            private readonly Queue<byte[]> _packets = new();
            private Exception _failure;
            private IntPtr _apartment;
            private readonly WindowsRuntime _device;
            private readonly WindowsRuntime _service;
            private readonly WindowsRuntime _writer;
            private readonly WindowsRuntime _notifier;
            private readonly WindowsBluetoothEvent _received;
            private readonly WindowsBluetoothEvent _statusChanged;
            private readonly int _writeOption;
            public bool IsLegacy { get; }
            public bool Lost { get { lock (_packets) return _failure != null; } }

            internal Link(HandyBleDevice selected) {
                _apartment = WindowsRuntime.KeepApartment();
                try {
                    using var factory = WindowsRuntime.Factory(Bluetooth + "BluetoothLEDevice", DeviceStatics2);
                    using var getDevice = factory.FromAddress(selected.Address, selected.AddressType);
                    _device = getDevice.Result();
                    if (_device.Pointer == IntPtr.Zero) throw WindowsRuntime.Disconnected();
                    using var device = _device.As(Device3);
                    // 광고는 후보 검색에만 사용하고 연결한 기기의 실제 GATT 서비스로 통신 방식을 정한다.
                    _service = FindIfPresent(device, 11, ServiceId); // GetGattServicesForUuidWithCacheModeAsync
                    IsLegacy = _service == null;
                    if (IsLegacy) _service = Find(device, 11, LegacyServiceId);
                    if (_service != null) {
                        using var service = _service.As(Service3);
                        _writer = Find(service, 14, IsLegacy ? LegacyCommandId : WriteId); // GetCharacteristicsForUuidWithCacheModeAsync
                        var write = (CharacteristicProperties)_writer.Integer(7);
                        if (!write.HasFlag(CharacteristicProperties.Write)) {
                            if (!write.HasFlag(CharacteristicProperties.WriteWithoutResponse)) throw UnsupportedService();
                            _writeOption = 1; // GattWriteOption.WriteWithoutResponse
                        }
                        _statusChanged = new WindowsBluetoothEvent(_device, 16, typeof(IConnectionStatusChanged),
                            (sender, _) => { if (WindowsRuntime.Integer(sender, 9) == 0) Fail(WindowsRuntime.Disconnected()); }, Fail);
                        if (IsLegacy) {
                            if (!write.HasFlag(CharacteristicProperties.Read)) throw UnsupportedService();
                            using var session = FindIfPresent(service, 14, LegacySessionId);
                            if (session != null) {
                                WriteValue(session, HandyLegacyProtocol.OpenSession, 0);
                                HandyLegacyProtocol.CheckSession(ReadValue(session));
                            }
                        } else {
                            _notifier = Find(service, 14, NotifyId);
                            var notify = (CharacteristicProperties)_notifier.Integer(7);
                            var subscription = 1; // GattClientCharacteristicConfigurationDescriptorValue.Notify
                            if (!notify.HasFlag(CharacteristicProperties.Notify)) {
                                if (!notify.HasFlag(CharacteristicProperties.Indicate)) throw UnsupportedService();
                                subscription = 2; // Indicate
                            }
                            _received = new WindowsBluetoothEvent(_notifier, 20, typeof(ICharacteristicValueChanged), Receive, Fail);
                            using var subscribe = _notifier.Object(19, subscription); // WriteClientCharacteristicConfigurationDescriptorAsync
                            CheckGatt(subscribe.StatusResult());
                        }
                    }

                    if (_device.Integer(9) != 1 || Lost) throw WindowsRuntime.Disconnected();
                } catch { Dispose(); throw; }
            }

            private static WindowsRuntime Find(WindowsRuntime parent, int method, Guid uuid) =>
                FindIfPresent(parent, method, uuid) ?? throw UnsupportedService();

            private static WindowsRuntime FindIfPresent(WindowsRuntime parent, int method, Guid uuid) {
                using var operation = parent.FindUncached(method, uuid);
                using var result = operation.Result();
                CheckGatt(result.Integer(6)); // 서비스/특성 검색 결과의 Status
                using var items = result.Object(8);
                return items.Integer(7) == 0 ? null : items.Object(6, 0); // IVectorView.GetAt(0)
            }

            private void Receive(IntPtr sender, IntPtr args) {
                using var buffer = WindowsRuntime.Object(args, 6); // GattValueChangedEventArgs.CharacteristicValue
                var packet = buffer.CopyBuffer();
                lock (_packets) {
                    if (_failure != null) return;
                    if (_packets.Count == 64) _packets.Dequeue();
                    _packets.Enqueue(packet);
                    Monitor.PulseAll(_packets);
                }
            }

            private void Fail(Exception error) {
                lock (_packets) { _failure ??= error; Monitor.PulseAll(_packets); }
            }

            public void Write(byte[] packet) {
                lock (_packets) { if (_failure != null) throw _failure; }
                using var apartment = WindowsRuntime.Apartment.Enter();
                WriteValue(_writer, packet, _writeOption);
            }

            private void WriteValue(WindowsRuntime characteristic, byte[] packet, int option) {
                using var buffer = WindowsRuntime.MakeBuffer(packet);
                using var operation = characteristic.WriteValue(buffer, option);
                var status = operation.StatusResult();
                if (status == 1) Fail(WindowsRuntime.Disconnected());
                CheckGatt(status);
            }

            private byte[] ReadValue(WindowsRuntime characteristic) {
                using var operation = characteristic.Object(15, 1); // ReadValueWithCacheModeAsync(Uncached)
                using var result = operation.Result();
                var status = result.Integer(6); // IGattReadResult.Status
                if (status == 1) Fail(WindowsRuntime.Disconnected());
                CheckGatt(status);
                using var value = result.Object(7); // IGattReadResult.Value
                return value.CopyBuffer();
            }

            public int Read(byte[] buffer, int timeoutMs) {
                if (IsLegacy) {
                    using var apartment = WindowsRuntime.Apartment.Enter();
                    var reply = ReadValue(_writer);
                    reply.CopyTo(buffer, 0);
                    return reply.Length;
                }
                var timer = Stopwatch.StartNew();
                lock (_packets) {
                    while (_failure == null && _packets.Count == 0) {
                        var remaining = timeoutMs - (int)timer.ElapsedMilliseconds;
                        if (remaining <= 0) throw WindowsRuntime.Timeout();
                        Monitor.Wait(_packets, remaining);
                    }
                    if (_failure != null) throw _failure;
                    var packet = _packets.Dequeue();
                    packet.CopyTo(buffer, 0);
                    return packet.Length;
                }
            }

            public void Dispose() {
                Fail(WindowsRuntime.Disconnected());
                _received?.Dispose();
                _statusChanged?.Dispose();
                _notifier?.Dispose();
                _writer?.Dispose();
                _service?.Close();
                _service?.Dispose();
                _device?.Close();
                _device?.Dispose();
                WindowsRuntime.ReleaseApartment(Interlocked.Exchange(ref _apartment, IntPtr.Zero));
            }
        }
#else
        internal static bool Supported => false;
        internal static HandyBleDevice[] Scan(CancellationToken cancellation) => throw UnsupportedPlatform();
        internal static IHandyBleLink Open(HandyBleDevice selected) => throw UnsupportedPlatform();
#endif
    }
}
