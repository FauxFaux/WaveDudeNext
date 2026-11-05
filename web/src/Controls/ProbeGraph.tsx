import { useLayoutEffect, useRef } from 'preact/hooks'
import type { Probe } from '../Api/Project.generated'
import type { ProbeGraphData } from '../Api/Probes'
import { layoutGraph } from '../Rendering/ProbeGraph'

interface ProbeGraphProps {
  probe: Probe
  data: ProbeGraphData
  totalSteps: number
  readout: string
}

export function ProbeGraph({ probe, data, totalSteps, readout }: ProbeGraphProps) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const latest = useRef({ probe, data, totalSteps })
  latest.current = { probe, data, totalSteps }
  const redraw = useRef<() => void>(() => {})

  useLayoutEffect(() => {
    const element = canvas.current!
    const context = element.getContext('2d')
    if (!context) return
    let scheduled = 0
    const draw = () => {
      scheduled = 0
      const { width, height } = element.getBoundingClientRect()
      if (width <= 0 || height <= 0) return
      const ratio = window.devicePixelRatio || 1
      element.width = Math.round(width * ratio)
      element.height = Math.round(height * ratio)
      context.setTransform(element.width / width, 0, 0, element.height / height, 0, 0)
      context.font = '11px "Segoe UI", system-ui, sans-serif'
      const { probe, data, totalSteps } = latest.current
      const layout = layoutGraph(probe, data, totalSteps, width, height, text => context.measureText(text).width)
      const { x, y, width: w, height: h } = layout
      const hairline = Math.max(1, Math.round(ratio)) / ratio
      const snap = (at: number) => Math.round(at * ratio) / ratio + hairline / 2
      context.lineWidth = hairline
      context.textBaseline = 'middle'
      for (const tick of layout.yTicks) {
        context.strokeStyle = tick.zero ? '#ffffff78' : '#ffffff2e'
        context.beginPath(); context.moveTo(x, snap(tick.position)); context.lineTo(x + w, snap(tick.position)); context.stroke()
        context.fillStyle = '#e1e1e6'
        context.textAlign = 'right'
        context.fillText(tick.label, x - 4, tick.position)
      }
      for (const tick of layout.xTicks) {
        context.strokeStyle = '#ffffff2e'
        context.beginPath(); context.moveTo(snap(tick.position), y); context.lineTo(snap(tick.position), y + h); context.stroke()
        context.fillStyle = '#e1e1e6'
        context.textAlign = 'center'
        const half = context.measureText(tick.label).width / 2
        context.fillText(tick.label, Math.max(half + 3, Math.min(width - half - 3, tick.position)), y + h + 11)
      }
      context.strokeStyle = '#c8c8d282'
      context.strokeRect(snap(x), snap(y), w, h)
      context.save()
      context.beginPath(); context.rect(x, y, w, h); context.clip()
      context.strokeStyle = context.fillStyle = '#ffd040'
      context.lineWidth = Math.max(1.2, (height + 27) / 110)
      context.lineJoin = 'round'
      const points = layout.points
      if (points.length === 1) {
        context.beginPath(); context.arc(points[0].x, points[0].y, 2, 0, 2 * Math.PI); context.fill()
      } else if (points.length > 1) {
        context.beginPath(); context.moveTo(points[0].x, points[0].y)
        for (let i = 1; i < points.length; i++) {
          if (points[i].start) context.moveTo(points[i].x, points[i].y)
          else context.lineTo(points[i].x, points[i].y)
        }
        context.stroke()
      }
      context.restore()
    }
    const schedule = () => { if (!scheduled) scheduled = requestAnimationFrame(draw) }
    redraw.current = schedule
    const observer = new ResizeObserver(schedule)
    try { observer.observe(element, { box: 'device-pixel-content-box' }) }
    catch { observer.observe(element) }
    window.addEventListener('resize', schedule)
    // Re-arm when browser zoom or moving between monitors changes device-pixel density.
    let density: MediaQueryList
    const watchDensity = () => {
      density?.removeEventListener('change', watchDensity)
      density = matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`)
      density.addEventListener('change', watchDensity)
      schedule()
    }
    watchDensity()
    return () => {
      observer.disconnect()
      window.removeEventListener('resize', schedule)
      density.removeEventListener('change', watchDensity)
      cancelAnimationFrame(scheduled)
      redraw.current = () => {}
    }
  }, [])
  useLayoutEffect(() => redraw.current(), [probe, data, totalSteps])

  const hasData = data.count > 0 || (data.profile?.length ?? 0) > 0
  return <>
    <canvas ref={canvas} class="probe-graph-canvas" role="img" aria-label={`${probe.name}: ${probe.kind === 'Line' ? 'position along probe in pixels' : 'simulation step'} against ${probe.measurement === 'Amplitude' ? 'amplitude' : 'intensity'}${probe.yScale === 'Logarithmic' && probe.layer === 'Intensity' ? ', logarithmic scale' : ''}. ${readout || 'Waiting for probe data.'}`} />
    {!hasData && <span class="probe-graph-empty">Waiting for probe data</span>}
  </>
}
