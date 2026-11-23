using System;
using System.Collections.Generic;
using System.IO;
using static HandyDeviceControl.HandyRpc;

namespace HandyDeviceControl {
    // 제조사의 레거시 통신 규격에 따라 요청을 쓴 GATT 특성에서 응답을 읽는다.
    internal static class HandyLegacyProtocol {
        internal enum Command : uint { Ping = 102, ServerInfo = 200, Linear = 403 }

        // Espressif SessionData.sec0.sc (Security 0). 세션을 여는 동안에는 이동 명령이 없다.
        internal static readonly byte[] OpenSession = { 0x52, 0x03, 0xa2, 0x01, 0x00 };

        internal static void CheckSession(byte[] response) {
            var session = new Fields(response);
            var security = session.Message(10);
            if (session.UInt(2) != 0 || security.UInt(1) != 1 || security.Message(21).UInt(1) != 0)
                throw new InvalidDataException("핸디가 레거시 BLE 세션을 열지 못했습니다.");
        }

        internal static Fields Exchange(IHandyBleLink link, uint id, Command command, double position = 0, uint durationMs = 0) {
            var body = new List<byte> { 0x08 }; // Id = 1
            WriteVariableLengthInteger(body, id);
            if (command == Command.Linear) {
                var vector = new List<byte> { 0x10 }; // Duration = 2
                WriteVariableLengthInteger(vector, durationMs);
                vector.Add(0x19); // Position = 3, protobuf double
                var bits = BitConverter.DoubleToInt64Bits(position);
                for (var shift = 0; shift < 64; shift += 8) vector.Add((byte)(bits >> shift));
                WriteMessage(body, 3, vector); // DeviceIndex와 Vector.Index는 기본값 0
            }
            var message = new List<byte>();
            WriteMessage(message, (uint)command, body);
            var payload = new List<byte>();
            WriteMessage(payload, 1, message);
            link.Write(payload.ToArray());

            var buffer = new byte[512];
            var length = link.Read(buffer, 8000);
            var response = new Fields(buffer.AsMemory(0, length));
            var expected = command == Command.ServerInfo ? 201 : 100; // ServerInfo 또는 Ok
            var offset = 0;
            while (response.TryBytes(1, 2, ref offset, out var entry)) {
                var item = new Fields(entry);
                if (item.TryBytes(101, 2, out var error)) {
                    var failure = new Fields(error);
                    if (failure.UInt(1) == id) throw new InvalidOperationException($"레거시 핸디 오류 {failure.UInt(3)}");
                }
                if (!item.TryBytes(expected, 2, out var bytes)) continue;
                var reply = new Fields(bytes);
                if (reply.UInt(1) == id) return reply;
            }
            throw new InvalidDataException("레거시 핸디에서 보낸 명령과 일치하는 응답을 받지 못했습니다.");
        }
    }
}
