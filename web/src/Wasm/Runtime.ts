export interface WaveDudeRuntime {
  SmokeTest(): number
  StartSimulation(projectJson: string): Int32Array<ArrayBuffer>
  NextFrame(): Uint8Array<ArrayBuffer>
  EndSimulation(): void
}

// Vite serves public/ directly and copies it into dist/ without rewriting .NET's modules.
// BASE_URL also supports deployments below a path such as /wavedude/.
export function waveDudeUrl(): string {
  // An absolute URL also prevents Vite's development import analysis appending ?import
  // to this public module, which must be served without transformation.
  return new URL(`${import.meta.env.BASE_URL}wasm/main.mjs`, globalThis.location.href).href
}

export async function loadWaveDude(url = waveDudeUrl()): Promise<WaveDudeRuntime> {
  const module = await import(/* @vite-ignore */ url) as {
    loadWaveDude(): Promise<WaveDudeRuntime>
  }
  return module.loadWaveDude()
}
