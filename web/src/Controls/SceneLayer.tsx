import { useEffect, useRef } from 'preact/hooks'
import type { ScenePreview } from '../Wasm/Scene'

// Match WPF SceneBitmaps.Overlay: normalised edge colour, with strength × gain in alpha.
export function edgeOverlay(edges: ImageData, gain: number): ImageData {
  const pixels = new Uint8ClampedArray(edges.data.length)
  for (let i = 0; i < pixels.length; i += 4) {
    const max = Math.max(edges.data[i], edges.data[i + 1], edges.data[i + 2])
    if (!max) continue
    pixels[i] = Math.floor(edges.data[i] * 255 / max)
    pixels[i + 1] = Math.floor(edges.data[i + 1] * 255 / max)
    pixels[i + 2] = Math.floor(edges.data[i + 2] * 255 / max)
    pixels[i + 3] = Math.floor(Math.min(255, max * gain))
  }
  return new ImageData(pixels, edges.width, edges.height)
}

export function SceneLayer({ scene, layer, gain = 1 }: { scene: ScenePreview, layer: 'image' | 'edges', gain?: number }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  useEffect(() => {
    const context = canvas.current?.getContext('2d')
    if (!context) return
    context.putImageData(layer === 'image' ? scene.image : edgeOverlay(scene.edges, gain), 0, 0)
  }, [scene, layer, gain])
  return <canvas ref={canvas} class={`scene-${layer}`} width={scene.width} height={scene.height} aria-hidden="true" />
}
