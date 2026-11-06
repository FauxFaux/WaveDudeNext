import { render } from 'preact'
import './index.css'
import { MainWindow } from './MainWindow.tsx'
import { checkRustKernel } from './Wasm/RustKernel'

render(<MainWindow />, document.getElementById('app')!)

void checkRustKernel().then(
  versions => console.info('Rust kernel worker check:', versions),
  error => console.error('Rust kernel worker check failed:', error),
)
