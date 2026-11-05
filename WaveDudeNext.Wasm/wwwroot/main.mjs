import { dotnet } from './_framework/dotnet.js'

let runtime

// Keep the SDK's relative module/resource URLs intact, outside Vite's bundle graph.
export function loadWaveDude() {
  runtime ??= initialise()
  return runtime
}

async function initialise() {
  const { getAssemblyExports, getConfig } = await dotnet.withDiagnosticTracing(false).create()
  const exports = await getAssemblyExports(getConfig().mainAssemblyName)
  return exports.WaveDudeNext.Wasm.Program
}
