import type { Probe } from '../Api/Project.generated'
import { frameProbeData } from '../Api/Probes'
import type { FrameProbe } from '../Api/Frames'
import { graphReadout, shownProbeData } from '../Rendering/ProbeGraph'
import { FloatingWidget } from './FloatingWidget'
import { ProbeGraph } from './ProbeGraph'

interface ProbeGraphsProps {
  probes: readonly Probe[]
  recordings?: readonly FrameProbe[]
  totalSteps: number
  orientation: 'stacked' | 'side-by-side'
  selectedId?: string
  onSelect: (id: string) => void
}

const boxSizes = { Tiny: [200, 100], Small: [300, 150], Medium: [400, 200], Large: [500, 250], ExtraLarge: [700, 350] } as const

export function ProbeGraphs({ probes, recordings, totalSteps, orientation, selectedId, onSelect }: ProbeGraphsProps) {
  const byId = new Map(recordings?.map(recording => [recording.probeId, recording]))
  return <div class="probe-graphs" aria-label="Probe graphs">
    {probes.filter(probe => probe.active).map((probe, index) => {
      const data = shownProbeData(probe, frameProbeData(byId.get(probe.id)))
      const readout = graphReadout(probe, data)
      const [width, height] = probe.boxSize === 'Custom' ? [probe.boxWidth, probe.boxHeight] : boxSizes[probe.boxSize]
      // Start below the corresponding preview's header; afterwards the widget can cross either picture.
      // Positions and sizes are CSS pixels, independent of frame resolution and export box coordinates.
      const offset = index * 24
      const intensity = probe.layer === 'Intensity'
      return <FloatingWidget key={probe.id} title={probe.name} description={probe.layer} selected={probe.id === selectedId} onSelect={() => onSelect(probe.id)}
        initialBounds={{ x: 16 + offset, y: 44 + offset, width, height }} initialAnchor={{ x: intensity && orientation === 'side-by-side' ? .5 : 0, y: intensity && orientation === 'stacked' ? .5 : 0 }}
        headerExtra={<span class="floating-widget-readout">{readout}</span>}>
        <ProbeGraph probe={probe} data={data} totalSteps={totalSteps} readout={readout} />
      </FloatingWidget>
    })}
  </div>
}
