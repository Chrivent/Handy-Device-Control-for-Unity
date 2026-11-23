using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HandyDeviceControl {
    // handy-public-rpc의 RpcMessage / Request / Response와 이 시험에서 쓰는 필드만 처리한다.
    internal static class HandyRpc {
        internal enum Method {
            DeviceInfo = 601, ModeSet = 701, PublicInfo = 718, Start = 720, Stop = 721, Velocity = 723, State = 724, Zone = 725,
            StreamSetup = 860, StreamAdd = 861, StreamPlay = 863, StreamTime = 868
        }

        internal static byte[] Request(uint id, Method method, params float[] settings) {
            var payload = new List<byte>();
            for (var i = 0; i < settings.Length; i++) WriteFloat(payload, (uint)i + 1, settings[i]);
            return PackRequest(id, method, payload);
        }

        internal static void WriteFloat(List<byte> target, uint field, float value) {
            WriteVariableLengthInteger(target, (field << 3) | 5);
            var bits = BitConverter.SingleToInt32Bits(value);
            for (var shift = 0; shift < 32; shift += 8) target.Add((byte)(bits >> shift));
        }

        internal static void WriteUnsigned(List<byte> target, uint field, uint value) {
            WriteVariableLengthInteger(target, field << 3);
            WriteVariableLengthInteger(target, value);
        }

        internal static byte[] PackRequest(uint id, Method method, List<byte> payload) {
            var request = new List<byte> { 0x10 }; // Request.id = 2
            WriteVariableLengthInteger(request, id);
            WriteMessage(request, (uint)method, payload);
            var envelope = new List<byte> { 0x08, 0x01 }; // MESSAGE_TYPE_REQUEST
            WriteMessage(envelope, 2, request);
            return envelope.ToArray();
        }

        internal static void WriteMessage(List<byte> target, uint field, List<byte> value) {
            WriteVariableLengthInteger(target, (field << 3) | 2);
            WriteVariableLengthInteger(target, (uint)value.Count);
            target.AddRange(value);
        }

        internal static void WriteVariableLengthInteger(List<byte> target, uint value) {
            while (value >= 128) {
                target.Add((byte)(value | 128));
                value >>= 7;
            }
            target.Add((byte)value);
        }

        internal static bool TryResponse(ReadOnlyMemory<byte> packet, uint id, Method method, out Fields result) {
            result = default;
            var envelope = new Fields(packet);
            if (envelope.UInt(1) != 3) return false; // 별도 알림은 명령의 응답이 아니다.
            var response = envelope.Message(4);
            if (response.UInt(1) != id) return false;
            if (response.TryBytes(2, 2, out var error)) throw new DeviceError(unchecked((int)new Fields(error).Number(1)));
            result = response.Message((int)method);
            return true;
        }

        internal sealed class DeviceError : Exception {
            private int Code { get; }
            internal bool UnknownMethod => Code is 100000 or 100001 or 100002 or 100003 or 100017;
            internal DeviceError(int code) : base($"핸디 BLE 기기 오류 {code}") => Code = code;
        }

        internal readonly struct Fields {
            private readonly ReadOnlyMemory<byte> _data;
            internal Fields(ReadOnlyMemory<byte> data) => _data = data;

            internal Fields Message(int field) => TryBytes(field, 2, out var value)
                ? new Fields(value) : throw InvalidPacket();

            internal string Text(int field) => TryBytes(field, 2, out var value) ? Encoding.UTF8.GetString(value.Span) : "";

            // proto3은 기본값 0인 스칼라를 생략한다. 감싸는 상태 메시지는 반드시 있어야 한다.
            internal uint UInt(int field) {
                var number = Number(field);
                return number <= uint.MaxValue ? (uint)number : throw InvalidPacket();
            }

            internal ulong Number(int field) {
                if (!TryBytes(field, 0, out var value)) return 0;
                var offset = 0;
                return ReadVariableLengthInteger(value.Span, ref offset);
            }

            internal float Float(int field) => TryBytes(field, 5, out var value) ? BitConverter.ToSingle(value.Span) : 0;

            internal bool TryBytes(int field, int wireType, out ReadOnlyMemory<byte> value) {
                var pos = 0;
                return TryBytes(field, wireType, ref pos, out value);
            }

            internal bool TryBytes(int field, int wireType, ref int pos, out ReadOnlyMemory<byte> value) {
                var data = _data.Span;
                while (pos < data.Length) {
                    var tag = ReadVariableLengthInteger(data, ref pos);
                    if ((tag >> 3) == 0 || tag > uint.MaxValue) throw InvalidPacket();
                    var wire = (int)(tag & 7);
                    var start = pos;
                    switch (wire) {
                        case 0: ReadVariableLengthInteger(data, ref pos); break;
                        case 1: pos += 8; break;
                        case 2:
                            var length = ReadVariableLengthInteger(data, ref pos);
                            if (length > (ulong)(data.Length - pos)) throw InvalidPacket();
                            start = pos;
                            pos += (int)length;
                            break;
                        case 5: pos += 4; break;
                        default: throw InvalidPacket();
                    }
                    if (pos > data.Length) throw InvalidPacket();
                    if ((int)(tag >> 3) != field) continue;
                    if (wire != wireType) throw InvalidPacket();
                    value = _data.Slice(start, pos - start);
                    return true;
                }
                value = default;
                return false;
            }

            private static ulong ReadVariableLengthInteger(ReadOnlySpan<byte> bytes, ref int pos) {
                ulong value = 0;
                for (var shift = 0; shift < 64 && pos < bytes.Length; shift += 7) {
                    var next = bytes[pos++];
                    if (shift == 63 && next > 1) throw InvalidPacket();
                    value |= (ulong)(next & 127) << shift;
                    if ((next & 128) == 0) return value;
                }
                throw InvalidPacket();
            }

            private static InvalidDataException InvalidPacket() => new("핸디 BLE 응답 형식을 확인할 수 없습니다.");
        }
    }
}
