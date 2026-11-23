using System.Threading;
using System.Threading.Tasks;

namespace HandyDeviceControl {
    internal interface IHandyPositionClient {
        // 개별 위치를 전송하는 연결에서는 진행 중인 동작을 반환한다. HAMP 연결은 자체 왕복 동작을 사용한다.
        Task PositionMotionCompletion { get; }
    }

    public interface IHandyClient {
        const int TestDurationMs = 2000;

        /// <summary>기기 응답을 확인하고 펌웨어 버전 또는 레거시 BLE 연결 정보를 돌려준다.</summary>
        Task<string> CheckConnectionAsync(CancellationToken cancellation);

        /// <summary>지정한 구간에서 동작을 시작한다. HandySession이 시간 만료와 수동 정지를 처리한다.</summary>
        Task StartStrokeAsync(HandyStroke stroke, CancellationToken cancellation);

        /// <summary>정지 상태를 조회한다. 레거시 BLE는 마지막 위치 이동 시간을 기다린 뒤 연결을 확인한다.</summary>
        Task StopAsync();
    }
}
