using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace HandyDeviceControl {
    /// <summary>게임 이벤트에서 요청할 왕복 구간(기기 이동 범위의 %)과 동작 시간이다.</summary>
    public sealed class HandyStroke {
        public int MinPercent { get; }
        public int MaxPercent { get; }
        public int DurationMs { get; }
        internal int MoveDurationMs => Math.Min(500, DurationMs);
        internal static readonly HandyStroke Test = new(40, 60, IHandyClient.TestDurationMs);

        public HandyStroke(int minPercent, int maxPercent, int durationMs) {
            if (minPercent is < 0 or >= 100) throw new ArgumentOutOfRangeException(nameof(minPercent));
            if (maxPercent > 100 || maxPercent <= minPercent) throw new ArgumentOutOfRangeException(nameof(maxPercent));
            if (durationMs <= 0) throw new ArgumentOutOfRangeException(nameof(durationMs));
            MinPercent = minPercent;
            MaxPercent = maxPercent;
            DurationMs = durationMs;
        }

        // 위치 명령만 쓰는 기기는 첫 이동 후 두 끝점을 번갈아 방문한다. 지연된 명령을 몰아서 보내지 않는다.
        internal async Task FinishPositionsAsync(Stopwatch window, Func<int, Task> move, CancellationToken cancellation) {
            long due = MoveDurationMs;
            var position = MaxPercent;
            while (due + MoveDurationMs < DurationMs) {
                var delay = due - window.ElapsedMilliseconds;
                if (delay > 0) await Task.Delay((int)delay, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var now = window.ElapsedMilliseconds;
                if (now + MoveDurationMs > DurationMs) break;
                due = now + MoveDurationMs;
                await move(position).ConfigureAwait(false);
                position = position == MaxPercent ? MinPercent : MaxPercent;
            }
            var remaining = DurationMs - window.ElapsedMilliseconds;
            if (remaining > 0) await Task.Delay((int)remaining, cancellation).ConfigureAwait(false);
        }
    }
}
