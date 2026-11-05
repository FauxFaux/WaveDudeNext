export function TimelineBar() {
  return (
    <div class="timeline-bar">
      <div class="timeline-heading"><span>Timeline</span><span>No frames available</span></div>
      <div class="timeline-axis" aria-hidden="true"><span>0</span><span>250</span><span>500</span><span>750</span><span>1000</span><span>1250</span></div>
      <input id="PreviewTimeline" class="timeline-scrubber" type="range" aria-label="Preview timeline" min="0" max="1250" value="0" disabled />
    </div>
  )
}
