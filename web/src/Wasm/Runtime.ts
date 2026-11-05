export interface WaveDudeRuntime {
  SmokeTest(): number
}

// Vite serves public/ directly and copies it into dist/ without rewriting .NET's modules.
// BASE_URL also supports deployments below a path such as /wavedude/.
export async function loadWaveDude(): Promise<WaveDudeRuntime> {
  // An absolute URL also prevents Vite's development import analysis appending ?import
  // to this public module, which must be served without transformation.
  const url = new URL(`${import.meta.env.BASE_URL}wasm/main.mjs`, globalThis.location.href).href
  const module = await import(/* @vite-ignore */ url) as {
    loadWaveDude(): Promise<WaveDudeRuntime>
  }
  return module.loadWaveDude()
}
