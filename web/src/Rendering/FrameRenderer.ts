import type { FramePlane } from '../Api/Frames'
import { greyStops, halfColourTable } from './FrameColours'
import type { FrameDisplay, GradientStop } from './FrameColours'

export interface FrameRenderer {
  setFrame: (frame: FramePlane) => void
  setDisplay: (display: FrameDisplay) => void
  dispose: () => void
}

const vertexSource = `#version 300 es
precision highp float;
void main() {
  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}`

const fragmentSource = `#version 300 es
precision highp float;
precision highp int;
uniform highp sampler2D samples;
uniform highp sampler2D gradient;
uniform int stopCount;
uniform int kind;
uniform float brightness;
uniform float blackLevel;
out vec4 colour;
vec4 stopAt(int i) { return texelFetch(gradient, ivec2(i, 0), 0); }
vec3 gradientAt(float p) {
  vec4 first = stopAt(0);
  vec4 last = stopAt(stopCount - 1);
  if (p <= first.a) return first.rgb;
  if (p >= last.a) return last.rgb;
  int low = 1;
  int high = stopCount - 1;
  while (low < high) {
    int middle = (low + high) / 2;
    if (stopAt(middle).a < p) low = middle + 1;
    else high = middle;
  }
  vec4 a = stopAt(low - 1);
  vec4 b = stopAt(low);
  return mix(a.rgb, b.rgb, (p - a.a) / (b.a - a.a));
}
void main() {
  // Network rows start at the top; fragment coordinates start at the bottom.
  ivec2 size = textureSize(samples, 0);
  ivec2 at = ivec2(int(gl_FragCoord.x), size.y - 1 - int(gl_FragCoord.y));
  float value = texelFetch(samples, at, 0).r;
  float p;
  if (kind == 0) {
    float scaled = value * brightness;
    p = isnan(scaled) ? 0.5 : (clamp(scaled, -1.0, 1.0) + 1.0) * 0.5;
  } else {
    float root = kind == 1 ? sqrt(max(0.0, value)) : value;
    p = root > 0.0 ? clamp(root * brightness, 0.0, 1.0) : 0.0;
  }
  vec3 level = clamp(gradientAt(p) * (1.0 - blackLevel) + blackLevel, 0.0, 1.0);
  // Match ColorLut.HalfTable's rounding at native resolution.
  colour = vec4(floor(level * 255.0 + 0.5) / 255.0, 1.0);
}`

function webglRenderer(canvas: HTMLCanvasElement, gl: WebGL2RenderingContext): FrameRenderer {
  const shaders: WebGLShader[] = []
  const program = gl.createProgram()
  const plane = gl.createTexture()
  const gradient = gl.createTexture()
  const vao = gl.createVertexArray()
  const dispose = () => {
    shaders.forEach(shader => gl.deleteShader(shader))
    gl.deleteProgram(program)
    gl.deleteTexture(plane)
    gl.deleteTexture(gradient)
    gl.deleteVertexArray(vao)
  }
  try {
    if (!program || !plane || !gradient || !vao) throw new Error('Could not create WebGL resources.')
    for (const [type, source] of [[gl.VERTEX_SHADER, vertexSource], [gl.FRAGMENT_SHADER, fragmentSource]] as const) {
      const shader = gl.createShader(type)
      if (!shader) throw new Error('Could not create WebGL shader.')
      shaders.push(shader)
      gl.shaderSource(shader, source)
      gl.compileShader(shader)
      if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader) ?? 'Shader compilation failed.')
      gl.attachShader(program, shader)
    }
    gl.linkProgram(program)
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program) ?? 'Shader linking failed.')
    gl.useProgram(program)
    gl.bindVertexArray(vao)
    gl.disable(gl.DITHER)
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1) // Odd-width R16F rows have no padding.
    for (const [unit, texture] of [[0, plane], [1, gradient]] as const) {
      gl.activeTexture(gl.TEXTURE0 + unit)
      gl.bindTexture(gl.TEXTURE_2D, texture)
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST)
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST)
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE)
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE)
    }
    const uniform = (name: string) => gl.getUniformLocation(program, name)
    gl.uniform1i(uniform('samples'), 0)
    gl.uniform1i(uniform('gradient'), 1)
    let hasFrame = false
    let width = 0
    let height = 0
    let previousStops: readonly GradientStop[] | undefined
    const check = () => {
      if (!gl.isContextLost() && gl.getError() !== gl.NO_ERROR) throw new Error('WebGL frame upload or presentation failed.')
    }
    const draw = () => {
      if (!hasFrame || gl.isContextLost()) return
      gl.viewport(0, 0, canvas.width, canvas.height)
      gl.drawArrays(gl.TRIANGLES, 0, 3)
      check()
    }
    const maximum = gl.getParameter(gl.MAX_TEXTURE_SIZE) as number
    const viewport = gl.getParameter(gl.MAX_VIEWPORT_DIMS) as Int32Array
    return {
      setFrame(frame) {
        if (frame.width > Math.min(maximum, viewport[0]) || frame.height > Math.min(maximum, viewport[1])) throw new Error('Frame exceeds WebGL size limits.')
        gl.activeTexture(gl.TEXTURE0)
        gl.bindTexture(gl.TEXTURE_2D, plane)
        if (width !== frame.width || height !== frame.height) {
          canvas.width = width = frame.width
          canvas.height = height = frame.height
          gl.texImage2D(gl.TEXTURE_2D, 0, gl.R16F, width, height, 0, gl.RED, gl.HALF_FLOAT, frame.samples)
        } else {
          gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, width, height, gl.RED, gl.HALF_FLOAT, frame.samples)
        }
        check()
        gl.uniform1i(uniform('kind'), frame.kind === 'field' ? 0 : frame.kind === 'intensity' ? 1 : 2)
        hasFrame = true
        draw()
      },
      setDisplay(display) {
        const stops = display.stops.length ? display.stops : greyStops
        if (stops.length > maximum) throw new Error('Gradient exceeds WebGL size limits.')
        gl.uniform1f(uniform('brightness'), display.brightness)
        gl.uniform1f(uniform('blackLevel'), display.blackLevel)
        gl.uniform1i(uniform('stopCount'), stops.length)
        if (stops !== previousStops) {
          gl.activeTexture(gl.TEXTURE1)
          gl.bindTexture(gl.TEXTURE_2D, gradient)
          gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA32F, stops.length, 1, 0, gl.RGBA, gl.FLOAT,
            new Float32Array(stops.flatMap(stop => [...stop.colour.map(channel => channel / 255), stop.position])))
          previousStops = stops
        }
        check()
        draw()
      },
      dispose,
    }
  } catch (error) {
    dispose()
    throw error
  }
}

function canvasRenderer(canvas: HTMLCanvasElement): FrameRenderer {
  const context = canvas.getContext('2d')
  if (!context) throw new Error('Canvas rendering is unavailable.')
  let frame: FramePlane | undefined
  let display: FrameDisplay | undefined
  let image: ImageData | undefined
  let table: Uint8ClampedArray | undefined
  const draw = () => {
    if (!frame || !display) return
    table ??= halfColourTable(frame.kind, display)
    if (!image || image.width !== frame.width || image.height !== frame.height) {
      canvas.width = frame.width
      canvas.height = frame.height
      image = context.createImageData(frame.width, frame.height)
    }
    const pixels = image.data
    for (let i = 0; i < frame.samples.length; i++) {
      const at = frame.samples[i] * 4
      pixels[i * 4] = table[at]
      pixels[i * 4 + 1] = table[at + 1]
      pixels[i * 4 + 2] = table[at + 2]
      pixels[i * 4 + 3] = 255
    }
    context.putImageData(image, 0, 0)
  }
  return {
    setFrame(next) { if (frame?.kind !== next.kind) table = undefined; frame = next; draw() },
    setDisplay(next) { display = next; table = undefined; draw() },
    dispose() { frame = undefined; image = undefined; table = undefined },
  }
}

// Own the canvas so a failed WebGL context can be replaced by a fresh Canvas 2D context.
export function createFrameRenderer(host: HTMLElement, onError: (message: string | null) => void): FrameRenderer {
  let canvas: HTMLCanvasElement
  let backend: FrameRenderer | undefined
  let frame: FramePlane | undefined
  let display: FrameDisplay | undefined
  let lost = false
  let disposed = false
  const report = (error: unknown) => onError(error instanceof Error ? error.message : 'Could not render frame.')
  const replay = () => {
    if (display) backend?.setDisplay(display)
    if (frame) backend?.setFrame(frame)
  }
  const fallback = () => {
    backend?.dispose()
    canvas?.removeEventListener('webglcontextlost', contextLost)
    canvas?.removeEventListener('webglcontextrestored', restore)
    canvas?.remove()
    canvas = document.createElement('canvas')
    host.append(canvas)
    backend = canvasRenderer(canvas)
    replay()
  }
  const restore = () => {
    if (disposed) return
    lost = false
    try {
      backend = webglRenderer(canvas, canvas.getContext('webgl2')!)
      replay()
      onError(null)
    } catch {
      try { fallback(); onError(null) } catch (error) { report(error) }
    }
  }
  function contextLost(event: Event) {
    event.preventDefault()
    lost = true
    onError('Graphics context lost; waiting for restoration…')
  }
  const update = (action: () => void) => {
    if (disposed || lost) return
    try { action(); onError(null) }
    catch { try { fallback(); onError(null) } catch (error) { report(error) } }
  }
  canvas = document.createElement('canvas')
  host.append(canvas)
  canvas.addEventListener('webglcontextlost', contextLost)
  canvas.addEventListener('webglcontextrestored', restore)
  try {
    const gl = canvas.getContext('webgl2', { alpha: false, depth: false, stencil: false, antialias: false })
    if (!gl) fallback()
    else backend = webglRenderer(canvas, gl)
  } catch { try { fallback() } catch (error) { report(error) } }
  return {
    setFrame(next) { frame = next; update(() => backend?.setFrame(next)) },
    setDisplay(next) { display = next; update(() => backend?.setDisplay(next)) },
    dispose() { disposed = true; backend?.dispose(); canvas.remove() },
  }
}
