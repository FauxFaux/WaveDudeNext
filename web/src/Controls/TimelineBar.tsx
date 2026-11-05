interface TimelineBarProps {
  index: number
  frameCount: number
  visualizeEvery: number
  onSeek: (index: number) => void
}

export function TimelineBar({ index, frameCount, visualizeEvery, onSeek }: TimelineBarProps) {
  const last = Math.max(0, frameCount - 1)
  return (
    <div class="timeline-bar">
      <div class="timeline-heading"><span>Timeline</span><span>{frameCount ? `${frameCount} frames available` : 'No frames available'}</span></div>
      <div class="timeline-axis" aria-hidden="true">{[0, 0.2, 0.4, 0.6, 0.8, 1].map(fraction => <span key={fraction}>{Math.round(last * fraction) * visualizeEvery}</span>)}</div>
      <input id="PreviewTimeline" class="timeline-scrubber" type="range" aria-label="Preview timeline" min="0" max={last} value={index} disabled={frameCount < 2} onInput={event => onSeek(event.currentTarget.valueAsNumber)} />
    </div>
  )
}
