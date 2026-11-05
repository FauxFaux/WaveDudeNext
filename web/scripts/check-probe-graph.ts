import assert from 'node:assert/strict'
import type { Probe } from '../src/Api/Project.generated.ts'
import { frameProbeData } from '../src/Api/Probes.ts'
import { graphRange, graphReadout, layoutGraph, niceTicks, shownProbeData } from '../src/Rendering/ProbeGraph.ts'

const probe: Probe = {
  id: '00000000-0000-4000-8000-000000000001', name: 'Probe', active: true,
  layer: 'Field', measurement: 'Amplitude', kind: 'Point', ax: 0, ay: 0, bx: 100, by: 0,
  boxSize: 'Medium', boxX: 0, boxY: 0, boxWidth: 400, boxHeight: 200,
  scaleMode: 'Running', autoScaleX: true, autoScaleY: true, yMin: -1, yMax: 1,
  yScale: 'Linear', decades: 6, window: 10, startAfter: 0,
}
const recording = { probeId: probe.id, name: probe.name, kind: probe.kind, measurement: probe.measurement, ax: 0, ay: 0, bx: 100, by: 0, startAfter: 0, values: [0, 2, -3], profile: null, profileLength: 0, stepsSampled: 3 }
const data = frameProbeData(recording)
assert.equal(data.count, 3, 'The graph uses the exact frame recording supplied by the server')
assert.equal(data.values, recording.values, 'Histories are read without another full copy')
assert.equal(graphReadout(probe, data), '= -3')
assert.deepEqual(graphRange(probe, data, 100), { xMin: 0, xMax: 3, yMin: -3.24, yMax: 3.24, logarithmic: false })
assert.deepEqual(graphRange(probe, frameProbeData(undefined), 100), { xMin: 0, xMax: 1, yMin: -1, yMax: 1, logarithmic: false }, 'Empty amplitude axes retain the fixed range')

const history = { ...data, values: Float32Array.from({ length: 100 }, (_, i) => i), count: 100 }
const running = graphRange({ ...probe, startAfter: 20 }, history, 1000)
assert.equal(running.xMin, 90)
assert.equal(running.xMax, 100)
const full = graphRange({ ...probe, startAfter: 20, scaleMode: 'Full', autoScaleX: false }, history, 1000)
assert.equal(full.xMin, 20)
assert.equal(full.xMax, 1000)
const line = { ...probe, layer: 'Intensity', measurement: 'Value', kind: 'Line' } as Probe
const profileData = frameProbeData({ ...recording, kind: 'Line', values: [], profile: [1, 4, 2], profileLength: 100 })
assert.equal(graphReadout(line, profileData), 'max 4')
assert.equal(graphRange(line, profileData, 100).yMin, 0)
assert.equal(graphRange(line, profileData, 100).xMax, 100)
assert.equal(graphRange({ ...line, yScale: 'Logarithmic', autoScaleY: false, yMin: 0, yMax: 1 }, data, 100).yMin, 1e-6)
assert.equal(graphRange({ ...probe, yScale: 'Logarithmic' }, data, 100).logarithmic, false, 'Field graphs remain linear')
assert.equal(shownProbeData({ ...line, startAfter: 5 }, profileData).profile, undefined, 'Line profiles wait for StartAfter')
assert.equal(shownProbeData({ ...probe, startAfter: 5 }, data).count, 3, 'StartAfter trims point axes without hiding their readout')
assert.deepEqual(niceTicks(0, .06, 4), [0, .02, .04, .06])

const values = new Float32Array(100000)
values[50123] = 100
values[50124] = -80
values[70000] = NaN
const layout = layoutGraph({ ...probe, scaleMode: 'Full', autoScaleX: false, autoScaleY: false, yMin: -100, yMax: 100 }, { ...data, values, count: values.length }, values.length, 400, 170, text => text.length * 6)
assert.ok(layout.points.length <= 2 * (Math.ceil(layout.width) + 2), 'Dense histories are bounded by plot columns')
assert.ok(layout.points.some(point => point.y === layout.y), 'A one-sample positive spike survives reduction')
assert.ok(layout.points.some(point => Math.abs(point.y - (layout.y + layout.height * .9)) < 1e-8), 'The negative spike in the same column survives too')
assert.ok(layout.points.every(point => Number.isFinite(point.x) && Number.isFinite(point.y)), 'Invalid samples never reach the renderer')
assert.ok(layout.points.every((point, i) => !i || point.x >= layout.points[i - 1].x), 'Reduced extrema remain in time order')
const log = layoutGraph({ ...line, yScale: 'Logarithmic', autoScaleY: false, yMin: .000001, yMax: 1 }, { ...data, profile: new Float32Array([0, -1, .01, 1]) }, 100, 400, 170, text => text.length * 6)
assert.equal(log.points[0].y, log.y + log.height)
assert.equal(log.points[1].y, log.y + log.height, 'Nonpositive log samples land on the bottom axis')
const gaps = layoutGraph(probe, { ...data, values: [1, null, 2, NaN, 3], count: 5 }, 100, 400, 170, text => text.length * 6)
assert.equal(gaps.points.length, 3)
assert.ok(gaps.points.every(point => point.start), 'Missing samples create gaps instead of connecting across them')
assert.ok(graphRange(probe, { ...data, values: [null, null], count: 2 }, 100).yMax === 1, 'Nulls never count as zero-valued measurements')
console.log('Probe graph checks passed: frame recordings, ranges, gates, ticks, logarithmic values, gaps and spike-preserving reduction.')
