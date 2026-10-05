"""Meta XR Simulator v205 session-capture adapter (NON-PUBLIC gRPC, recording automation only).

This is not a Meta public API. Meta documents recording through the Simulator UI ("Record session") and automated
replay of a VRS in a standalone app through persistent_data.json "session_capture". Recording automation here uses the
SessionCapture gRPC service that the v205 runtime (SIMULATOR.dll, loaded in the app process) exposes on a loopback port;
its messages were read from the descriptor embedded in that DLL:

    package openxr_simulator.rpc.proto  (sim_session_capture.proto)
    GetStateRequest {}                              -> SessionCaptureStateResponse { Result result = 1; SessionCaptureState state = 2; }
    GotoRecordRequest { string recordPath = 2; }    -> SessionCaptureResponse { Result result = 1; }
    GotoIdleRequest {}                              -> SessionCaptureResponse
    enum SessionCaptureState { IDLE = 0; RECORD = 1; REPLAY = 2; }

Everything fails closed: the Simulator version must be 205.x (from the runtime log of the target process and the
installed MetaXRSimulator.exe), the service must answer on one of the process's loopback ports, a Result with a
non-empty code or message is an error, and every command must reach its target state within the timeout.

usage:
  xrsim_v205_session_capture.py check  --pid PID --simulator-dir DIR --log-dir DIR
  xrsim_v205_session_capture.py record --pid PID --simulator-dir DIR --log-dir DIR --path FILE.vrs
  xrsim_v205_session_capture.py stop   --pid PID --simulator-dir DIR --log-dir DIR

Prints one JSON object; exit 0 ok, 3 unsupported version, 4 no connection, 5 command or state transition failed.
"""
import argparse
import ctypes
import json
import os
import re
import subprocess
import sys
import time
from ctypes import wintypes

import grpc

SERVICE = '/openxr_simulator.rpc.proto.SessionCapture/'
STATES = {0: 'IDLE', 1: 'RECORD', 2: 'REPLAY'}
SUPPORTED_MAJOR = '205'


class AdapterError(Exception):
    def __init__(self, code, message, **details):
        super().__init__(message)
        self.code = code
        self.details = details


def varint(value):
    out = bytearray()
    while True:
        byte = value & 0x7F
        value >>= 7
        out.append(byte | 0x80 if value else byte)
        if not value:
            return bytes(out)


def string_field(number, text):
    data = text.encode('utf-8')
    return varint((number << 3) | 2) + varint(len(data)) + data


def read_varint(data, i):
    value = shift = 0
    while True:
        byte = data[i]
        i += 1
        value |= (byte & 0x7F) << shift
        shift += 7
        if not byte & 0x80:
            return value, i


def decode(data):
    """Generic protobuf decode: {field: [values]} with nested messages decoded when possible."""
    fields = {}
    i = 0
    while i < len(data):
        key, i = read_varint(data, i)
        number, wire = key >> 3, key & 7
        if wire == 0:
            value, i = read_varint(data, i)
        elif wire == 2:
            length, i = read_varint(data, i)
            chunk = data[i:i + length]
            i += length
            try:
                value = chunk.decode('utf-8')
                if not all(32 <= ord(c) < 127 for c in value):
                    raise ValueError
            except ValueError:
                try:
                    value = decode(chunk)
                except (IndexError, ValueError):
                    value = chunk.hex()
        elif wire == 5:
            value, i = data[i:i + 4].hex(), i + 4
        elif wire == 1:
            value, i = data[i:i + 8].hex(), i + 8
        else:
            raise ValueError('unsupported wire type %d' % wire)
        fields.setdefault(number, []).append(value)
    return fields


def result_error(fields):
    """The Result message (field 1): empty on success; code / message fields otherwise."""
    result = fields.get(1, [{}])[0]
    if isinstance(result, dict) and result:
        return {'code': result.get(1, [None])[0], 'message': result.get(2, [None])[0]}
    return None


def file_version(path):
    size = ctypes.windll.version.GetFileVersionInfoSizeW(path, None)
    if not size:
        return None
    buffer = ctypes.create_string_buffer(size)
    ctypes.windll.version.GetFileVersionInfoW(path, 0, size, buffer)
    fixed = ctypes.c_void_p()
    length = wintypes.UINT()
    if not ctypes.windll.version.VerQueryValueW(buffer, '\\', ctypes.byref(fixed), ctypes.byref(length)):
        return None
    info = ctypes.cast(fixed, ctypes.POINTER(ctypes.c_uint32 * 13)).contents
    ms, ls = info[2], info[3]
    return '%d.%d.%d.%d' % (ms >> 16, ms & 0xFFFF, ls >> 16, ls & 0xFFFF)


def check_version(pid, simulator_dir, log_dir):
    exe_version = file_version(os.path.join(simulator_dir, 'MetaXRSimulator.exe'))
    if not exe_version or exe_version.split('.')[0] != SUPPORTED_MAJOR:
        raise AdapterError(3, 'installed MetaXRSimulator.exe is %s, adapter supports %s.x only' % (exe_version, SUPPORTED_MAJOR), exe_version=exe_version)
    runtime_version = None
    logs = [f for f in os.listdir(log_dir) if re.match(r'meta_xrsim_.*_%d\.log$' % pid, f)] if os.path.isdir(log_dir) else []
    for name in logs:
        with open(os.path.join(log_dir, name), encoding='utf-8', errors='replace') as handle:
            for line in handle:
                match = re.search(r'Set up XrApiLayers \(.*Version (\d+\.\d+\.\d+\.\d+\.\d+)', line)
                if match:
                    runtime_version = match.group(1)
                    break
        if runtime_version:
            break
    if not runtime_version or runtime_version.split('.')[0] != SUPPORTED_MAJOR:
        raise AdapterError(3, 'runtime in process %d reports version %s, adapter supports %s.x only' % (pid, runtime_version, SUPPORTED_MAJOR),
                           exe_version=exe_version, runtime_version=runtime_version)
    return {'exe_version': exe_version, 'runtime_version': runtime_version}


def loopback_ports(pid):
    ports = []
    for proto in ('TCP', 'TCPv6'):
        output = subprocess.run(['netstat', '-ano', '-p', proto], capture_output=True, text=True).stdout
        for line in output.splitlines():
            parts = line.split()
            if len(parts) >= 5 and parts[3] == 'LISTENING' and parts[4] == str(pid):
                match = re.match(r'^\[::1\]:(\d+)$', parts[1]) or re.match(r'^127\.0\.0\.1:(\d+)$', parts[1])
                if match:
                    ports.append(('ipv6:[::1]:' if parts[1].startswith('[') else '127.0.0.1:') + match.group(1))
    return ports


def call(channel, method, request, timeout=5):
    stub = channel.unary_unary(SERVICE + method, request_serializer=None, response_deserializer=None)
    return decode(stub(request, timeout=timeout))


def get_state(channel):
    fields = call(channel, 'GetState', b'')
    error = result_error(fields)
    if error:
        raise AdapterError(5, 'GetState returned an error', error=error)
    return STATES.get(fields.get(2, [0])[0], 'UNKNOWN(%s)' % fields.get(2, [0])[0])


def connect(pid):
    tried = []
    for target in loopback_ports(pid):
        channel = grpc.insecure_channel(target)
        try:
            state = get_state(channel)
            return channel, target, state, tried
        except grpc.RpcError as error:
            tried.append({'target': target, 'error': '%s %s' % (error.code(), error.details())})
            channel.close()
    raise AdapterError(4, 'no SessionCapture service answered on the loopback ports of process %d' % pid, tried=tried)


def wait_state(channel, wanted, timeout):
    deadline = time.time() + timeout
    state = get_state(channel)
    while state != wanted and time.time() < deadline:
        time.sleep(0.1)
        state = get_state(channel)
    return state


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('command', choices=['check', 'record', 'stop'])
    parser.add_argument('--pid', type=int, required=True)
    parser.add_argument('--simulator-dir', required=True)
    parser.add_argument('--log-dir', required=True)
    parser.add_argument('--path')
    parser.add_argument('--timeout', type=float, default=5.0)
    args = parser.parse_args()
    report = {'adapter': 'meta-xr-simulator-v205-session-capture-grpc', 'public_api': False, 'command': args.command, 'pid': args.pid}
    try:
        report['version'] = check_version(args.pid, args.simulator_dir, args.log_dir)
        channel, target, state, tried = connect(args.pid)
        report.update({'target': target, 'state_before': state, 'unanswered_ports': tried})
        if args.command == 'record':
            if not args.path:
                raise AdapterError(5, '--path is required for record')
            if state != 'IDLE':
                raise AdapterError(5, 'record needs state IDLE, was ' + state)
            error = result_error(call(channel, 'GotoRecord', string_field(2, os.path.abspath(args.path))))
            if error:
                raise AdapterError(5, 'GotoRecord returned an error', error=error)
            report['state_after'] = wait_state(channel, 'RECORD', args.timeout)
            if report['state_after'] != 'RECORD':
                raise AdapterError(5, 'state did not become RECORD', state_after=report['state_after'])
        elif args.command == 'stop':
            if state != 'RECORD':
                raise AdapterError(5, 'stop needs state RECORD, was ' + state)
            error = result_error(call(channel, 'GotoIdle', b''))
            if error:
                raise AdapterError(5, 'GotoIdle returned an error', error=error)
            report['state_after'] = wait_state(channel, 'IDLE', args.timeout)
            if report['state_after'] != 'IDLE':
                raise AdapterError(5, 'state did not become IDLE', state_after=report['state_after'])
        channel.close()
        report['ok'] = True
        print(json.dumps(report))
        return 0
    except AdapterError as error:
        report.update({'ok': False, 'error': str(error), 'exit_code': error.code}, **error.details)
        print(json.dumps(report))
        return error.code
    except grpc.RpcError as error:
        report.update({'ok': False, 'error': 'gRPC %s %s' % (error.code(), error.details()), 'exit_code': 5})
        print(json.dumps(report))
        return 5


if __name__ == '__main__':
    sys.exit(main())
