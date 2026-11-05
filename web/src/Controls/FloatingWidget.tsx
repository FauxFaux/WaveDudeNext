import type { ComponentChildren, JSX } from 'preact'
import { useLayoutEffect, useRef, useState } from 'preact/hooks'
import './FloatingWidget.css'

export interface WidgetBounds { x: number, y: number, width: number, height: number }
type Gesture = {
  pointerId: number
  mode: 'move' | 'resize'
  x: number
  y: number
  bounds: WidgetBounds
}

interface FloatingWidgetProps {
  title: string
  description?: string
  initialBounds: WidgetBounds
  initialAnchor?: { x: number, y: number }
  selected?: boolean
  onSelect?: () => void
  headerExtra?: ComponentChildren
  children: ComponentChildren
}

/** Local CSS-pixel layout: moving a preview widget does not edit the simulation or its export geometry. */
export function FloatingWidget({ title, description, initialBounds, initialAnchor, selected, onSelect, headerExtra, children }: FloatingWidgetProps) {
  const host = useRef<HTMLElement>(null)
  const gesture = useRef<Gesture | null>(null)
  const [bounds, setBounds] = useState(initialBounds)
  const current = useRef(bounds)
  current.current = bounds

  function fit(next: WidgetBounds): WidgetBounds {
    const parent = host.current?.parentElement
    const roomWidth = parent?.clientWidth ?? next.width
    const roomHeight = parent?.clientHeight ?? next.height
    const width = Math.min(roomWidth, Math.max(220, next.width))
    const height = Math.min(roomHeight, Math.max(120, next.height))
    return {
      x: Math.round(Math.max(0, Math.min(roomWidth - width, next.x))),
      y: Math.round(Math.max(0, Math.min(roomHeight - height, next.y))),
      width: Math.round(width), height: Math.round(height),
    }
  }

  function update(next: WidgetBounds) {
    const fitted = fit(next)
    current.current = fitted
    setBounds(fitted)
  }

  useLayoutEffect(() => {
    const parent = host.current?.parentElement
    if (!parent) return
    const observer = new ResizeObserver(() => {
      if (parent.clientWidth > 0 && parent.clientHeight > 0) update(current.current)
    })
    observer.observe(parent)
    update({ ...initialBounds, x: initialBounds.x + parent.clientWidth * (initialAnchor?.x ?? 0), y: initialBounds.y + parent.clientHeight * (initialAnchor?.y ?? 0) })
    return () => observer.disconnect()
  }, [])

  function begin(event: JSX.TargetedPointerEvent<HTMLButtonElement>, mode: Gesture['mode']) {
    if (event.button !== 0 || !event.isPrimary || gesture.current) return
    event.preventDefault()
    event.currentTarget.focus()
    event.currentTarget.setPointerCapture(event.pointerId)
    gesture.current = { pointerId: event.pointerId, mode, x: event.clientX, y: event.clientY, bounds: current.current }
  }

  function move(event: JSX.TargetedPointerEvent<HTMLButtonElement>) {
    const start = gesture.current
    if (!start || start.pointerId !== event.pointerId) return
    const dx = event.clientX - start.x
    const dy = event.clientY - start.y
    const parent = host.current!.parentElement!
    update(start.mode === 'move'
      ? { ...start.bounds, x: start.bounds.x + dx, y: start.bounds.y + dy }
      : { ...start.bounds, width: Math.min(parent.clientWidth - start.bounds.x, start.bounds.width + dx), height: Math.min(parent.clientHeight - start.bounds.y, start.bounds.height + dy) })
  }

  function end(event: JSX.TargetedPointerEvent<HTMLButtonElement>, cancel = false) {
    const start = gesture.current
    if (!start || start.pointerId !== event.pointerId) return
    gesture.current = null
    if (cancel) update(start.bounds)
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId)
  }

  function key(event: JSX.TargetedKeyboardEvent<HTMLButtonElement>, mode: Gesture['mode']) {
    if (event.altKey || event.ctrlKey || event.metaKey) return
    if (event.key === 'Escape' && gesture.current) {
      const start = gesture.current
      gesture.current = null
      update(start.bounds)
      if (event.currentTarget.hasPointerCapture(start.pointerId)) event.currentTarget.releasePointerCapture(start.pointerId)
      event.preventDefault()
      event.stopPropagation()
      return
    }
    const step = event.shiftKey ? 10 : 1
    const dx = event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0
    const dy = event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0
    if (!dx && !dy) return
    event.preventDefault()
    event.stopPropagation()
    const next = current.current
    const parent = host.current!.parentElement!
    update(mode === 'move' ? { ...next, x: next.x + dx, y: next.y + dy }
      : { ...next, width: Math.min(parent.clientWidth - next.x, next.width + dx), height: Math.min(parent.clientHeight - next.y, next.height + dy) })
  }

  return (
    <section ref={host} class={`floating-widget${selected ? ' selected' : ''}`} aria-label={`${title} graph`} style={{
      transform: `translate(${bounds.x}px, ${bounds.y}px)`, width: bounds.width, height: bounds.height,
    }} onPointerDownCapture={onSelect} onFocusCapture={onSelect}>
      <header class="floating-widget-header">
        <button type="button" class="floating-widget-move" aria-label={`Move ${title} graph`} title="Drag to move. Arrow keys move; Shift moves by 10 pixels. Escape cancels a drag."
          onPointerDown={event => begin(event, 'move')} onPointerMove={move} onPointerUp={event => end(event)}
          onPointerCancel={event => end(event, true)} onLostPointerCapture={event => end(event, true)} onKeyDown={event => key(event, 'move')}>
          <span>{title}</span>{description && <small>{description}</small>}
        </button>
        {headerExtra}
      </header>
      <div class="floating-widget-content">{children}</div>
      <button type="button" class="floating-widget-resize" aria-label={`Resize ${title} graph`} title="Drag to resize. Arrow keys resize; Shift resizes by 10 pixels. Escape cancels a drag."
        onPointerDown={event => begin(event, 'resize')} onPointerMove={move} onPointerUp={event => end(event)}
        onPointerCancel={event => end(event, true)} onLostPointerCapture={event => end(event, true)} onKeyDown={event => key(event, 'resize')}>
        <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3 14 14 3M8 14l6-6M13 14l1-1" /></svg>
      </button>
    </section>
  )
}
