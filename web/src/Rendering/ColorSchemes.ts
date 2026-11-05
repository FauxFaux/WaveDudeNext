import type { SharedColorScheme } from '../Api/ColorSchemes'
import type { ColorSchemeKind } from '../Api/Project.generated'
import type { GradientStop } from './FrameColours'
import { greyStops } from './FrameColours'

/** Project schemes take precedence over shipped schemes, as in the desktop catalogue. */
export function schemeChoices(own: readonly SharedColorScheme[], shared: readonly SharedColorScheme[], kind: ColorSchemeKind) {
  const names = new Set<string>()
  return [...own, ...shared.filter(scheme => scheme.kind === kind || scheme.kind === 'Unspecified')]
    .filter(scheme => {
      const name = scheme.name.toLowerCase()
      if (names.has(name)) return false
      names.add(name)
      return true
    })
}

// Preserve the desktop's old project names when looking up the renamed shipped schemes.
const legacyNames: Record<string, string> = {
  blue_to_yellow_gnu: 'Blue to yellow', flare_inv: 'Flare inverted',
  gray_scales_inv: 'Gray scales inverted', turky_brown: 'Turkey brown',
}

export function findScheme(schemes: readonly SharedColorScheme[], name: string) {
  const match = (name: string) => schemes.find(scheme => scheme.name.toLowerCase() === name.toLowerCase())
  return match(name) ?? (name.includes('_') ? match(legacyNames[name.toLowerCase()] ?? name.replaceAll('_', ' ')) : undefined)
}

/** Copy the gradient into the renderer's format, retaining hard edges and uneven stop positions. */
export function schemeStops(scheme?: SharedColorScheme): readonly GradientStop[] {
  return scheme?.stops.length ? [...scheme.stops].sort((a, b) => a.position - b.position).map(stop => ({
    position: stop.position,
    colour: [1, 3, 5].map(start => parseInt(stop.colorText.slice(start, start + 2), 16)) as [number, number, number],
  })) : greyStops
}
