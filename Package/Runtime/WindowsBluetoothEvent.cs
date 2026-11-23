#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
using System;
using System.Runtime.InteropServices;
using UnityEngine.Scripting;

namespace HandyDeviceControl {
    // Windows SDK에 정의된 네 종류의 TypedEventHandler는 모두 IUnknown과 Invoke 한 개로 구성된다.
    [ComImport, Guid("90eb4eca-d465-5ea0-a61c-033c8c5ecef2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAdvertisementReceived { [PreserveSig] int Invoke(IntPtr sender, IntPtr args); }
    [ComImport, Guid("9936a4db-dc99-55c3-9e9b-bf4854bd9eab"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IWatcherStopped { [PreserveSig] int Invoke(IntPtr sender, IntPtr args); }
    [ComImport, Guid("a90661e2-372e-5d1e-bbbb-b8a2ce0e7c4d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IConnectionStatusChanged { [PreserveSig] int Invoke(IntPtr sender, IntPtr args); }
    [ComImport, Guid("c1f420f6-6292-5760-a2c9-9ddf98683cfc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICharacteristicValueChanged { [PreserveSig] int Invoke(IntPtr sender, IntPtr args); }
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None), Preserve]
    internal sealed class WindowsBluetoothEvent : IDisposable, IAdvertisementReceived, IWatcherStopped,
        IConnectionStatusChanged, ICharacteristicValueChanged, IAgileObject {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Add(IntPtr source, IntPtr handler, out long token);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Remove(IntPtr source, long token);

        private readonly object _gate = new();
        private readonly WindowsRuntime _source;
        private readonly int _removeSlot;
        private readonly Action<Exception> _failed;
        private readonly long _token;
        private Action<IntPtr, IntPtr> _callback;

        internal WindowsBluetoothEvent(WindowsRuntime source, int addSlot, Type handlerType,
            Action<IntPtr, IntPtr> callback, Action<Exception> failed) {
            _source = source;
            _removeSlot = addSlot + 1;
            _callback = callback;
            _failed = failed;
            var handler = Marshal.GetComInterfaceForObject(this, handlerType);
            try {
                WindowsRuntime.Check(WindowsRuntime.Method<Add>(source.Pointer, addSlot)(source.Pointer, handler, out _token));
            } finally { Marshal.Release(handler); }
        }

        [Preserve]
        public int Invoke(IntPtr sender, IntPtr args) {
            lock (_gate) {
                try { _callback?.Invoke(sender, args); }
                catch (Exception error) { _failed(error); }
            }
            return 0;
        }

        public void Dispose() {
            lock (_gate) {
                if (_callback == null) return;
                _callback = null;
            }
            // 진행 중인 콜백이 끝난 뒤 Windows 구독을 해제한다. 이후 도착한 콜백은 아무 작업도 하지 않는다.
            WindowsRuntime.Method<Remove>(_source.Pointer, _removeSlot)(_source.Pointer, _token);
        }
    }
}
#endif
