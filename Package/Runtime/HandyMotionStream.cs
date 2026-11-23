using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HandyDeviceControl {
    /// <summary>선택한 기기가 준비된 뒤 위치를 공급하는 선택적 저수준 인터페이스다.</summary>
    public interface IHandyMotionSource {
        Task SendPositionsAsync(Func<int, int, Task> move, CancellationToken cancellation);
        Task SendBufferedAsync(Func<IReadOnlyList<HandyMotionPoint>, Task> append,
            Func<int, bool, Task> setTime, CancellationToken cancellation);
    }

    public readonly struct HandyMotionPoint {
        public int TimeMs { get; }
        public int Position { get; }

        public HandyMotionPoint(int timeMs, int position) {
            if (timeMs < 0) throw new ArgumentOutOfRangeException(nameof(timeMs));
            if (position is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(position));
            TimeMs = timeMs;
            Position = position;
        }
    }

    public interface IHandyStreamingClient {
        Task StreamAsync(IHandyMotionSource source, CancellationToken cancellation);
    }

    internal sealed class HandyMotionStream {
        private readonly object _gate = new();
        private CancellationTokenSource _active;

        internal async Task RunAsync(Func<CancellationToken, Task> send, CancellationToken cancellation) {
            using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            lock (_gate) {
                if (_active != null) throw new InvalidOperationException("위치 전송이 이미 진행 중입니다.");
                _active = run;
            }
            try { await send(run.Token).ConfigureAwait(false); }
            finally {
                lock (_gate) {
                    _active = null;
                    run.Cancel();
                }
            }
        }

        internal void Cancel() {
            lock (_gate) _active?.Cancel();
        }
    }
}
