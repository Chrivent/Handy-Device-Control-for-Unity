#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace HandyDeviceControl {
    // Windows SDK의 WinRT ABI를 사용한다. IInspectable의 0~5번 슬롯 다음부터 각 인터페이스의 메서드가 시작된다.
    internal sealed class WindowsRuntime : IDisposable {
        private static readonly Guid AsyncInfo = new("00000036-0000-0000-c000-000000000046");
        private static readonly Guid Closable = new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
        private static readonly Guid BufferFactory = new("71af914d-c10f-484b-bc50-14bc623b3a27");
        private static readonly Guid BufferByteAccess = new("905a0fef-bc53-11df-8c49-001e4fc686da");
        private IntPtr _value;
        internal IntPtr Pointer => _value;

        private WindowsRuntime(IntPtr value) => _value = value;

        internal readonly struct Apartment : IDisposable {
            internal static Apartment Enter() { Check(RoInitialize(1)); return default; }
            public void Dispose() => RoUninitialize();
        }

        // GATT 객체는 검색/쓰기 작업의 스레드가 끝난 뒤에도 같은 MTA에서 알림을 받는다.
        internal static IntPtr KeepApartment() { Check(CoIncrementMTAUsage(out var cookie)); return cookie; }
        internal static void ReleaseApartment(IntPtr cookie) { if (cookie != IntPtr.Zero) CoDecrementMTAUsage(cookie); }

        internal static WindowsRuntime Factory(string name, Guid iid) {
            Check(WindowsCreateString(name, name.Length, out var text));
            try {
                Check(RoGetActivationFactory(text, ref iid, out var factory));
                return new WindowsRuntime(factory);
            } finally { WindowsDeleteString(text); }
        }

        internal static WindowsRuntime Activate(string name, Guid iid) {
            // IActivationFactory.ActivateInstance가 돌려준 IInspectable에서 BLE 인터페이스를 얻는다.
            using var factory = Factory(name, new Guid("00000035-0000-0000-c000-000000000046"));
            using var instance = factory.Object(6);
            return instance.As(iid);
        }

        internal WindowsRuntime As(Guid iid) => Query(_value, iid);
        internal static WindowsRuntime Query(IntPtr value, Guid iid) {
            Check(Marshal.QueryInterface(value, ref iid, out var result));
            return new WindowsRuntime(result);
        }

        internal static T Method<T>(IntPtr value, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(value), slot * IntPtr.Size));

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Call(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPointer(IntPtr self, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInteger(IntPtr self, out int result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetByte(IntPtr self, out byte result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAddress(IntPtr self, out ulong result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetInteger(IntPtr self, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IntegerObject(IntPtr self, int value, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IndexGuid(IntPtr self, int index, out Guid result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddressObject(IntPtr self, ulong address, int type, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int UuidObject(IntPtr self, Guid uuid, int cache, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int BufferOperation(IntPtr self, IntPtr buffer, int option, out IntPtr result);

        internal void Run(int slot) => Check(Method<Call>(_value, slot)(_value));
        internal void Set(int slot, int value) => Check(Method<SetInteger>(_value, slot)(_value, value));
        internal int Integer(int slot) => Integer(_value, slot);
        internal static int Integer(IntPtr self, int slot) {
            Check(Method<GetInteger>(self, slot)(self, out var value));
            return value;
        }
        internal bool Boolean(int slot) {
            Check(Method<GetByte>(_value, slot)(_value, out var value));
            return value != 0;
        }
        internal static ulong Address(IntPtr self, int slot) {
            Check(Method<GetAddress>(self, slot)(self, out var value));
            return value;
        }
        private static IntPtr Get(IntPtr self, int slot) {
            Check(Method<GetPointer>(self, slot)(self, out var value));
            return value;
        }
        internal WindowsRuntime Object(int slot) => Object(_value, slot);
        internal static WindowsRuntime Object(IntPtr self, int slot) => new(Get(self, slot));
        internal WindowsRuntime Object(int slot, int argument) {
            Check(Method<IntegerObject>(_value, slot)(_value, argument, out var result));
            return new WindowsRuntime(result);
        }
        internal WindowsRuntime FromAddress(ulong address, int type) {
            // IBluetoothLEDeviceStatics2.FromBluetoothAddressWithBluetoothAddressTypeAsync
            Check(Method<AddressObject>(_value, 12)(_value, address, type, out var operation));
            return new WindowsRuntime(operation);
        }
        internal WindowsRuntime FindUncached(int slot, Guid uuid) {
            Check(Method<UuidObject>(_value, slot)(_value, uuid, 1, out var operation));
            return new WindowsRuntime(operation);
        }
        internal WindowsRuntime WriteValue(WindowsRuntime buffer, int option) {
            // IGattCharacteristic.WriteValueWithOptionAsync
            Check(Method<BufferOperation>(_value, 17)(_value, buffer.Pointer, option, out var operation));
            return new WindowsRuntime(operation);
        }
        internal Guid GuidAt(int index) {
            Check(Method<IndexGuid>(_value, 6)(_value, index, out var guid));
            return guid;
        }
        internal string Text(int slot) {
            var text = Get(_value, slot);
            try {
                var chars = WindowsGetStringRawBuffer(text, out var length);
                return length == 0 ? string.Empty : Marshal.PtrToStringUni(chars, length);
            } finally { WindowsDeleteString(text); }
        }

        private void Wait(CancellationToken cancellation) {
            using var info = As(AsyncInfo);
            var timer = Stopwatch.StartNew();
            while (info.Integer(7) == 0) { // IAsyncInfo.Status == Started
                if (cancellation.IsCancellationRequested || timer.ElapsedMilliseconds >= 8000) {
                    Method<Call>(info.Pointer, 9)(info.Pointer); // Cancel
                    cancellation.ThrowIfCancellationRequested();
                    throw Timeout();
                }
                Thread.Sleep(15);
            }
            cancellation.ThrowIfCancellationRequested();
        }
        internal WindowsRuntime Result(CancellationToken cancellation = default) { Wait(cancellation); return Object(8); }
        internal int StatusResult() { Wait(CancellationToken.None); return Integer(8); }

        internal static WindowsRuntime MakeBuffer(byte[] bytes) {
            using var factory = Factory("Windows.Storage.Streams.Buffer", BufferFactory);
            var buffer = factory.Object(6, bytes.Length);
            try {
                if (bytes.Length > 0) {
                    using var access = buffer.As(BufferByteAccess);
                    Marshal.Copy(bytes, 0, Get(access.Pointer, 3), bytes.Length);
                }
                buffer.Set(8, bytes.Length); // IBuffer.Length
                return buffer;
            } catch { buffer.Dispose(); throw; }
        }
        internal byte[] CopyBuffer() {
            var length = Integer(7); // IBuffer.Length
            if ((uint)length > 512) throw new InvalidOperationException("블루투스 알림이 최대 GATT 값 길이를 초과했습니다.");
            if (length == 0) return Array.Empty<byte>();
            var bytes = new byte[length];
            using var access = As(BufferByteAccess);
            Marshal.Copy(Get(access.Pointer, 3), bytes, 0, length);
            return bytes;
        }

        internal void Close() {
            if (_value == IntPtr.Zero) return;
            // IClosable.Close는 이미 끊긴 기기에서도 호출할 수 있다. 각 COM 참조는 별도로 해제한다.
            var iid = Closable;
            if (Marshal.QueryInterface(_value, ref iid, out var close) < 0) return;
            try { Method<Call>(close, 6)(close); } finally { Marshal.Release(close); }
        }
        public void Dispose() {
            var value = Interlocked.Exchange(ref _value, IntPtr.Zero);
            if (value != IntPtr.Zero) Marshal.Release(value);
        }

        internal static TimeoutException Timeout() => new("블루투스 기기 응답 시간이 초과되었습니다.");
        internal static InvalidOperationException Disconnected() => new("선택한 블루투스 기기와 연결이 끊겼습니다.");
        internal static InvalidOperationException RadioUnavailable() => new("PC의 블루투스 LE 어댑터를 확인하고 블루투스를 켜세요.");
        internal static InvalidOperationException AccessDenied() => new("Windows에서 블루투스 접근을 허용하세요.");
        internal static void Check(int result) {
            if (result >= 0) return;
            throw result switch {
                unchecked((int)0x800705B4) => Timeout(),
                unchecked((int)0x80070005) => AccessDenied(),
                unchecked((int)0x8007048F) => Disconnected(),
                unchecked((int)0x80070015) => RadioUnavailable(),
                unchecked((int)0x80040154) or unchecked((int)0x80004002) =>
                    new PlatformNotSupportedException("이 Windows 환경에서 필요한 블루투스 LE API를 사용할 수 없습니다."),
                _ => new InvalidOperationException($"Windows 블루투스 통신 오류 (0x{result:X8})")
            };
        }

        [DllImport("combase.dll")] private static extern int RoInitialize(int type);
        [DllImport("combase.dll")] private static extern void RoUninitialize();
        [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int WindowsCreateString(string text, int length, out IntPtr value);
        [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr text);
        [DllImport("combase.dll")] private static extern IntPtr WindowsGetStringRawBuffer(IntPtr text, out int length);
        [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr factory);
        [DllImport("ole32.dll")] private static extern int CoIncrementMTAUsage(out IntPtr cookie);
        [DllImport("ole32.dll")] private static extern int CoDecrementMTAUsage(IntPtr cookie);
    }
}
#endif
