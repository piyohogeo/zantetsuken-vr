"""DIAGNOSIS (SKIN-BENCH-2 section 4): the GPU time of a RenderDoc capture by marker. Run inside RenderDoc's own Python
(the renderdoc module of the install; its embedded 3.6):

  "C:\\Program Files\\RenderDocForMetaQuest\\qrenderdoc.exe" --python Analyze-RenderDocCapture.py

with the environment RD_CAPTURE = <file.rdc> and RD_OUT = <output .md>. Inside the UI the capture is opened through the UI's
context (pyrenderdoc) and the work is done on its replay thread; standalone (python with the renderdoc module) the capture is
opened directly. The EventGPUDuration counter is fetched for every event and the durations summed over the marker tree
(Unity's command-buffer samples: ProfilingScope names, URP's pass names): one line a marker path with its GPU time, the draws
and dispatches beneath it and their instance and index totals. The counter is the GPU's own timestamp pair of each event as
RenderDoc replays it: a replay measurement, not the live frame, and never a CPU dispatch time. A marker whose events the
counter does not cover is written with its measured/all event counts."""
import os, sys, io, traceback

import renderdoc as rd

capture_path = os.environ.get('RD_CAPTURE')
out_path = os.environ.get('RD_OUT') or ((capture_path or 'capture') + '.gpu.md')
log_path = out_path + '.log'


def log(text):
    with io.open(log_path, 'a', encoding='utf-8') as f:
        f.write(text + '\n')


def analyze(controller, out):
    counters = controller.EnumerateCounters()
    if rd.GPUCounter.EventGPUDuration not in counters:
        out.write('EventGPUDuration is not available on this replay: GPU times not measured\n')
        return
    desc = controller.DescribeCounter(rd.GPUCounter.EventGPUDuration)
    results = controller.FetchCounters([rd.GPUCounter.EventGPUDuration])
    duration = {}
    for r in results:
        if desc.resultType == rd.CompType.Float and desc.resultByteWidth == 8:
            v = r.value.d
        elif desc.resultType == rd.CompType.Float:
            v = r.value.f
        else:
            v = r.value.u64 if desc.resultByteWidth == 8 else r.value.u32
        duration[r.eventId] = v
    scale = 1e3 if desc.unit == rd.CounterUnit.Seconds else 1.0   # ms
    unit = 'ms' if desc.unit == rd.CounterUnit.Seconds else str(desc.unit)
    sdfile = controller.GetStructuredFile()
    rows = []

    def walk(action, path, depth):
        name = (action.GetName(sdfile) if hasattr(action, 'GetName') else action.customName) or ''
        name = name.strip()
        total = 0.0
        draws = dispatches = instances = indices = measured = events = 0
        if action.children:
            for child in action.children:
                t, d, di, ins, ind, m, e = walk(child, path + [name], depth + 1)
                total += t; draws += d; dispatches += di; instances += ins; indices += ind; measured += m; events += e
        else:
            events = 1
            if action.eventId in duration:
                total = duration[action.eventId] * scale
                measured = 1
            flags = action.flags
            if flags & rd.ActionFlags.Drawcall:
                draws = 1
                instances = max(1, action.numInstances)
                indices = action.numIndices
            if flags & rd.ActionFlags.Dispatch:
                dispatches = 1
        rows.append((depth, name, total, draws, dispatches, instances, indices, measured, events))
        return total, draws, dispatches, instances, indices, measured, events

    for root in controller.GetRootActions():
        walk(root, [], 0)
    out.write('| marker | GPU %s | draws | dispatches | instances | indices | events measured / all |\n| --- | --- | --- | --- | --- | --- | --- |\n' % unit)
    for depth, name, total, draws, dispatches, instances, indices, measured, events in rows:
        if depth > 3 and draws + dispatches <= 1:
            continue   # leaves beneath depth 3 are summed into their parents
        label = ('&nbsp;' * 2 * depth) + (name if name else '(unnamed)')
        out.write('| %s | %.3f | %d | %d | %d | %d | %d / %d |\n' % (label.replace('|', '/'), total, draws, dispatches, instances, indices, measured, events))
    out.write('\ncounter: EventGPUDuration, unit %s, replayed by RenderDoc (not the live frame; not a CPU dispatch time)\n' % unit)


def write(controller):
    with io.open(out_path, 'w', encoding='utf-8', newline='\n') as out:
        out.write('# GPU durations by marker: %s\n\n' % os.path.basename(capture_path))
        props = controller.GetAPIProperties()
        out.write('API %s, vendor %s\n\n' % (props.pipelineType, props.vendor))
        analyze(controller, out)


def main():
    if not capture_path or not os.path.exists(capture_path):
        raise SystemExit('RD_CAPTURE must name an existing .rdc')
    inside = 'pyrenderdoc' in globals()
    if inside:
        # Inside the UI: the capture is loaded through the UI's context once the window has settled, the work is done on
        # the UI's replay thread (BlockInvoke), and the process ends when the file is written.
        import time
        ctx = globals()['pyrenderdoc']
        time.sleep(8)
        log('ui: loading ' + capture_path)
        ctx.LoadCapture(capture_path, rd.ReplayOptions(), capture_path, False, True)
        for _ in range(120):
            if ctx.IsCaptureLoaded():
                break
            time.sleep(1)
        if not ctx.IsCaptureLoaded():
            log('ui: the capture did not load')
            os._exit(2)
        done = []

        def job(controller):
            try:
                write(controller)
                done.append(True)
            except Exception:  # noqa: BLE001
                log(traceback.format_exc())
                done.append(False)

        ctx.Replay().BlockInvoke(job)
        log('ui: ' + ('written ' + out_path if done and done[0] else 'FAILED'))
        os._exit(0 if done and done[0] else 3)
    else:
        rd.InitialiseReplay(rd.GlobalEnvironment(), [])
    if True:
        cap = rd.OpenCaptureFile()
        result = cap.OpenFile(capture_path, '', None)
        if result != rd.ResultCode.Succeeded:
            raise SystemExit('cannot open ' + capture_path + ': ' + str(result))
        result, controller = cap.OpenCapture(rd.ReplayOptions(), None)
        if result != rd.ResultCode.Succeeded:
            raise SystemExit('cannot replay ' + capture_path + ': ' + str(result))
        write(controller)
        log('written ' + out_path)
        controller.Shutdown()
        cap.Shutdown()
        if inside:
            os._exit(0)
        rd.ShutdownReplay()


try:
    main()
except SystemExit:
    raise
except Exception:  # noqa: BLE001
    log(traceback.format_exc())
    if 'pyrenderdoc' in globals():
        os._exit(4)
    raise
