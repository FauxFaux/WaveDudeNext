const paths = {
  wave: 'M2 12c3-12 5-12 8 0s5 12 8 0 3-8 4-8',
  source: 'M12 2v3m0 14v3M2 12h3m14 0h3M5 5l2 2m10 10 2 2M5 19l2-2M17 7l2-2M16 12a4 4 0 1 1-8 0 4 4 0 0 1 8 0Z',
  scene: 'M3 4h18v16H3ZM3 16l5-5 4 4 3-3 6 6M15 8h.01',
  simulation: 'm13 2-9 12h7l-1 8 10-13h-8Z',
  display: 'M3 3h18v14H3Zm5 18h8m-4-4v4',
  part: 'M6 3c8 3 8 15 0 18m12-18c-8 3-8 15 0 18',
  probe: 'M12 3v18M3 12h18M7 7h10v10H7Z',
  colours: 'M4 4h16v16H4Zm0 8h16M12 4v16',
  play: 'm8 5 11 7-11 7Z', pause: 'M8 5v14M16 5v14', stop: 'M6 6h12v12H6Z',
  first: 'M3 5v14m16-14-9 7 9 7Zm-9 0v14', previous: 'M5 5v14m13-14-9 7 9 7Z',
  next: 'M19 5v14M6 5l9 7-9 7Z', last: 'M21 5v14M5 5l9 7-9 7Zm9 0v14',
  loop: 'M5 7h12l3 3M17 4l3 3-3 3M19 17H7l-3-3m3 6-3-3 3-3',
  stacked: 'M3 3h18v18H3Zm0 9h18', sideBySide: 'M3 3h18v18H3Zm9-9v18',
} as const

export type IconName = keyof typeof paths

export function IconView({ name }: { name: IconName }) {
  return (
    <svg class="icon" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
      <path d={paths[name]} />
    </svg>
  )
}
