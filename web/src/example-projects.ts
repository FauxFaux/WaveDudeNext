export const exampleProjects = import.meta.glob('./assets/examples/Example*.json', { query: '?raw', import: 'default', eager: true }) as Record<string, string>
