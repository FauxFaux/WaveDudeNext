// Scene metadata from Model/Scene.cs; image bytes will use the workspace API separately.
export interface Scene {
  mode: 'Canvas' | 'Image'
  image: string
  imagePath: string
  width: number
  height: number
  dampingEdge: number
  edges: 'Open' | 'Reflective'
  refractiveIndex: number
  imageSources: boolean
  imageSourceScale: number
  objectSources: boolean
}

export function createScene(): Scene {
  return {
    mode: 'Canvas', image: '', imagePath: '', width: 1920, height: 1080,
    dampingEdge: -32, edges: 'Open', refractiveIndex: 1,
    imageSources: false, imageSourceScale: 1, objectSources: true,
  }
}
