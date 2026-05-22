import { useMemo, useRef, useState } from 'react'
import './App.css'

function App() {
  const fileInputRef = useRef(null)
  const [selectedFile, setSelectedFile] = useState(null)
  const [fields, setFields] = useState({})
  const [status, setStatus] = useState('Choose a PDF to extract readable fields and document information.')
  const [error, setError] = useState('')
  const [isUploading, setIsUploading] = useState(false)

  const fieldEntries = useMemo(() => Object.entries(fields), [fields])

  async function handleUpload(event) {
    event.preventDefault()

    if (!selectedFile) {
      setError('Select a PDF file first.')
      return
    }

    const formData = new FormData()
    formData.append('file', selectedFile)

    setIsUploading(true)
    setError('')
    setStatus('Analyzing PDF content...')

    try {
      const response = await fetch('/api/pdf/extract-fields', {
        method: 'POST',
        body: formData,
      })

      const responseText = await response.text()
      const result = responseText ? JSON.parse(responseText) : {}

      if (!response.ok) {
        throw new Error(result.error || `Unable to extract fields from this PDF. Server returned ${response.status}.`)
      }

      setFields(result.fields || {})
      setStatus(
        result.fieldCount > 0
          ? `Loaded ${result.fieldCount} extracted item${result.fieldCount === 1 ? '' : 's'} from ${result.fileName}.`
          : `${result.fileName} was readable, but no clear fields or values were found.`,
      )
    } catch (uploadError) {
      setFields({})
      setError(
        uploadError instanceof SyntaxError
          ? 'The API returned a response React could not read. Restart the .NET API and try again.'
          : uploadError.message,
      )
      setStatus('Extraction failed.')
    } finally {
      setIsUploading(false)
    }
  }

  function updateField(name, value) {
    setFields((currentFields) => ({
      ...currentFields,
      [name]: value,
    }))
  }

  function clearForm() {
    setSelectedFile(null)
    setFields({})
    setError('')
    setStatus('Choose a PDF to extract readable fields and document information.')

    if (fileInputRef.current) {
      fileInputRef.current.value = ''
    }
  }

  return (
    <main className="app-shell">
      <section className="workspace" aria-labelledby="page-title">
        <div className="intro">
          <p className="eyebrow">React + .NET PDF extractor</p>
          <h1 id="page-title">Upload a PDF and extract its information.</h1>
          <p>
            The API reads embedded PDF fields, analyzes visible document text, and can use a
            configured model to infer labels and values without a predefined field list.
          </p>
        </div>

        <form className="upload-panel" onSubmit={handleUpload}>
          <label className="file-drop">
            <span className="file-drop-icon" aria-hidden="true">+</span>
            <span>
              <strong>{selectedFile ? selectedFile.name : 'Select PDF file'}</strong>
              <small>Searchable PDFs work best; image scans need OCR.</small>
            </span>
            <input
              ref={fileInputRef}
              type="file"
              accept="application/pdf,.pdf"
              onChange={(event) => setSelectedFile(event.target.files?.[0] || null)}
            />
          </label>

          <div className="actions">
            <button type="submit" disabled={isUploading}>
              {isUploading ? 'Extracting...' : 'Extract fields'}
            </button>
            <button type="button" className="secondary" onClick={clearForm}>
              Clear
            </button>
          </div>

          <p className={error ? 'status error' : 'status'}>{error || status}</p>
        </form>

        <section className="form-panel" aria-label="Extracted PDF form fields">
          <div className="panel-heading">
            <div>
              <p className="eyebrow">Extracted values</p>
              <h2>Editable extracted data</h2>
            </div>
            <span>{fieldEntries.length} fields</span>
          </div>

          {fieldEntries.length > 0 ? (
            <div className="field-grid">
              {fieldEntries.map(([name, value]) => (
                <label className="field" key={name}>
                  <span>{name}</span>
                  <input value={value ?? ''} onChange={(event) => updateField(name, event.target.value)} />
                </label>
              ))}
            </div>
          ) : (
            <div className="empty-state">
              <h2>No values loaded yet</h2>
              <p>Upload a searchable PDF and the extracted values will appear here.</p>
            </div>
          )}
        </section>
      </section>
    </main>
  )
}

export default App
