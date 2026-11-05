import { z } from 'zod'
import { useEffect, useRef, useState } from 'preact/hooks'
import type { PropertyPath } from '../Model/Project'
import { initialValue } from '../Model/SchemaDefaults'

const labels: Record<string, string> = {
  colorText: 'Colour', colorScheme: 'Colour scheme', colorSchemes: 'Colour schemes',
  fiber: 'Fibre', Fiber: 'Fibre', GrinLens: 'GRIN lens', XorGate: 'XOR gate', AndGate: 'AND gate', OrGate: 'OR gate',
  ax: 'A: X', ay: 'A: Y', bx: 'B: X', by: 'B: Y', r1: 'R1', r2: 'R2', fps: 'FPS',
  visualizeEvery: 'Visualise every',
}

export function propertyLabel(key: string): string {
  return labels[key] ?? key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/^./, letter => letter.toUpperCase())
}

type ChangeProperty = (path: PropertyPath, value: unknown) => Promise<void>

interface ProjectPropertiesProps {
  schema: z.ZodObject
  value: Record<string, unknown>
  onChange: ChangeProperty
  disabled?: boolean
}

function ScalarProperty({ schema, value, label, disabled, onChange }: {
  schema: z.ZodType, value: unknown, label: string, disabled: boolean, onChange: (value: unknown) => Promise<void>,
}) {
  const [text, setText] = useState(String(value))
  const [dirty, setDirty] = useState(false)
  const version = useRef(0)
  const numeric = schema instanceof z.ZodNumber
  const candidate = numeric ? Number(text) : text
  const valid = (!numeric || text.trim() !== '') && schema.safeParse(candidate).success
  useEffect(() => { if (!dirty) setText(String(value)) }, [value, dirty])

  function commit() {
    if (!dirty || !valid) return
    const submitted = version.current
    void onChange(candidate).then(() => {
      if (version.current === submitted) setDirty(false)
    }).catch(() => {})
  }

  const title = schema.meta()?.description
  if (schema instanceof z.ZodBoolean) return <label class="checkbox-row" title={title}>
    <input type="checkbox" checked={value === true} disabled={disabled} onChange={event => { void onChange(event.currentTarget.checked).catch(() => {}) }} />{label}
  </label>
  if (schema instanceof z.ZodEnum) return <label class="form-row" title={title}><span>{label}</span>
    <select value={String(value)} disabled={disabled} onChange={event => { void onChange(event.currentTarget.value).catch(() => {}) }}>
      {schema.options.map(option => <option key={String(option)} value={String(option)}>{propertyLabel(String(option))}</option>)}
    </select>
  </label>
  return <label class="form-row" title={title}><span>{label}</span>
    <input type={numeric ? 'number' : 'text'} value={text} disabled={disabled} aria-invalid={!valid}
      min={numeric ? schema.minValue ?? undefined : undefined} max={numeric ? schema.maxValue ?? undefined : undefined}
      step={numeric ? schema.isInt ? 1 : 'any' : undefined}
      onInput={event => { version.current++; setText(event.currentTarget.value); setDirty(true) }}
      onBlur={commit} onKeyDown={event => {
        if (event.key === 'Enter') { event.preventDefault(); event.currentTarget.blur() }
        if (event.key === 'Escape') { event.preventDefault(); version.current++; setText(String(value)); setDirty(false) }
      }} />
  </label>
}

function fields(schema: z.ZodObject, value: Record<string, unknown>, path: PropertyPath, onChange: ChangeProperty, disabled: boolean) {
  return Object.entries(schema.shape).filter(([key]) => key !== 'id' && key !== 'imageAssetId').map(([key, child]) => (
    <Property key={key} schema={child as z.ZodType} value={value[key]} path={[...path, key]} label={propertyLabel(key)} onChange={onChange} disabled={disabled} />
  ))
}

function Property({ schema, value, path, label, onChange, disabled }: {
  schema: z.ZodType, value: unknown, path: PropertyPath, label: string, onChange: ChangeProperty, disabled: boolean,
}) {
  if (schema instanceof z.ZodLiteral) return null
  if (schema instanceof z.ZodNullable) {
    const inner = schema.unwrap() as z.ZodType
    return <details open class="form-section"><summary>{label}</summary><div class="form-fields">
      <label class="checkbox-row"><input type="checkbox" checked={value !== null} disabled={disabled}
        onChange={event => { void onChange(path, event.currentTarget.checked ? initialValue(inner) : null).catch(() => {}) }} />Use {label.toLowerCase()}</label>
      {value !== null && inner instanceof z.ZodObject && fields(inner, value as Record<string, unknown>, path, onChange, disabled)}
    </div></details>
  }
  if (schema instanceof z.ZodObject) return <details open class="form-section"><summary>{label}</summary>
    <div class="form-fields">{fields(schema, value as Record<string, unknown>, path, onChange, disabled)}</div>
  </details>
  if (schema instanceof z.ZodArray && Array.isArray(value)) return <details open class="form-section"><summary>{label} ({value.length})</summary>
    <div class="form-fields">
      {value.map((entry, index) => <div class="property-list-entry" key={`${value.length}:${index}`}>
        <div class="list-entry-header"><span>{label} {index + 1}</span>
          <button disabled={disabled} aria-label={`Remove ${label.toLowerCase()} ${index + 1}`}
            onClick={() => { void onChange(path, value.filter((_, at) => at !== index)).catch(() => {}) }}>−</button>
        </div>
        {schema.element instanceof z.ZodObject && fields(schema.element, entry, [...path, index], onChange, disabled)}
      </div>)}
      <button class="property-add" disabled={disabled} onClick={() => { void onChange(path, [...value, initialValue(schema.element as z.ZodType)]).catch(() => {}) }}>Add {label.toLowerCase()} entry</button>
    </div>
  </details>
  return <ScalarProperty schema={schema} value={value} label={label} disabled={disabled} onChange={value => onChange(path, value)} />
}

export function ProjectProperties({ schema, value, onChange, disabled = false }: ProjectPropertiesProps) {
  return <div class="property-form"><div class="form-fields">{fields(schema, value, [], onChange, disabled)}</div></div>
}
