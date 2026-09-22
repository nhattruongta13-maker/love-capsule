import { useEffect, useState } from 'react'
import './App.css'

const API_BASE = '/api'
const API_ORIGIN = API_BASE.replace('/api', '')

const starterForm = {
  title: '',
  date: new Date().toISOString().slice(0, 10),
  mood: 'Happy',
  description: '',
  imageUrl: '',
  visibility: 'Private',
  partnerCanEdit: false,
}

const getImageSource = (imageUrl) => (
  imageUrl?.startsWith('/') ? `${API_ORIGIN}${imageUrl}` : imageUrl
)

const createCorrelationId = () => crypto.randomUUID()

const starterLoginForm = { email: '', password: '' }
const starterRegisterForm = { displayName: '', email: '', password: '' }

function App() {
  const [memories, setMemories] = useState([])
  const [formData, setFormData] = useState(starterForm)
  const [imageFile, setImageFile] = useState(null)
  const [status, setStatus] = useState('Loading memories...')
  const [editingMemoryId, setEditingMemoryId] = useState(null)
  const [search, setSearch] = useState('')
  const [moodFilter, setMoodFilter] = useState('')

  const [session, setSession] = useState(null) // null | { token, user }
  const [checkingSession, setCheckingSession] = useState(true)
  const [authMode, setAuthMode] = useState('login') // 'login' | 'register'
  const [authError, setAuthError] = useState('')
  const [loginForm, setLoginForm] = useState(starterLoginForm)
  const [registerForm, setRegisterForm] = useState(starterRegisterForm)
  const [relationshipData, setRelationshipData] = useState(null)
  const [inviteeUserId, setInviteeUserId] = useState('')
  const [relationshipMessage, setRelationshipMessage] = useState('')
  const [inviteCode, setInviteCode] = useState('')
  const [sharedMemories, setSharedMemories] = useState([])
  const [sharedMemoryMessage, setSharedMemoryMessage] = useState('')

  // On load, try the HttpOnly refresh cookie before showing the login screen.
  useEffect(() => {
    const restoreSession = async () => {
      try {
        const response = await fetch(`${API_BASE}/auth/refresh`, {
          method: 'POST',
          credentials: 'include',
        })

        if (response.ok) {
          const data = await response.json()
          setSession({ token: data.accessToken, user: data.user })
        }
      } catch (error) {
        console.error(error)
      } finally {
        setCheckingSession(false)
      }
    }

    restoreSession()
  }, [])

  useEffect(() => {
    if (!session) {
      return undefined
    }

    const abortController = new AbortController()
    loadMemories()
    loadRelationship()
    connectToMemoryEvents(abortController.signal)

    return () => abortController.abort()
  }, [session])

  // Attaches the bearer token and logs the user out if the API rejects it.
  const authFetch = async (path, options = {}) => {
    const response = await fetch(`${API_BASE}${path}`, {
      ...options,
      headers: {
        ...options.headers,
        Authorization: `Bearer ${session?.token}`,
        'X-Correlation-ID': createCorrelationId(),
      },
    })

    if (response.status === 401) {
      setSession(null)
      setAuthError('Session expired. Please log in again.')
    }

    return response
  }

  const connectToMemoryEvents = async (signal) => {
    if (!session) {
      return
    }

    let reconnectDelay = 1000
    let lastEventId = 0
    while (!signal.aborted) {
      try {
        const response = await fetch(`${API_BASE}/events?lastEventId=${lastEventId}`, {
          signal,
          headers: {
            Authorization: `Bearer ${session.token}`,
            Accept: 'text/event-stream',
            'X-Correlation-ID': createCorrelationId(),
          },
        })

        if (!response.ok || !response.body) {
          return
        }

        const reader = response.body.getReader()
        const decoder = new TextDecoder()
        let buffer = ''
        reconnectDelay = 1000

        while (!signal.aborted) {
          const { value, done } = await reader.read()
          if (done) {
            break
          }

          buffer += decoder.decode(value, { stream: true })
          const events = buffer.split('\n\n')
          buffer = events.pop() ?? ''

          for (const event of events) {
            if (event.includes('event: Memory')) {
              const dataLine = event.split('\n').find((line) => line.startsWith('data: '))
              if (dataLine) {
                const parsed = JSON.parse(dataLine.slice('data: '.length))
                lastEventId = Math.max(lastEventId, parsed.id)
              }
              await loadMemories()
              await loadRelationship()
            }
          }
        }

        if (signal.aborted) {
          return
        }
      } catch (error) {
        if (signal.aborted) {
          return
        }

        console.error('Memory event stream disconnected.', error)
      }

      await new Promise((resolve) => {
        const timeout = setTimeout(resolve, reconnectDelay)
        signal.addEventListener('abort', () => {
          clearTimeout(timeout)
          resolve()
        }, { once: true })
      })
      reconnectDelay = Math.min(reconnectDelay * 2, 30000)
    }
  }

  const handleLoginChange = (event) => {
    const { name, value } = event.target
    setLoginForm((previous) => ({ ...previous, [name]: value }))
  }

  const handleRegisterChange = (event) => {
    const { name, value } = event.target
    setRegisterForm((previous) => ({ ...previous, [name]: value }))
  }

  const handleLoginSubmit = async (event) => {
    event.preventDefault()
    setAuthError('')

    try {
      const response = await fetch(`${API_BASE}/auth/login`, {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/json',
          'X-Correlation-ID': createCorrelationId(),
        },
        body: JSON.stringify(loginForm),
      })

      if (!response.ok) {
        setAuthError('Incorrect email or password.')
        return
      }

      const data = await response.json()
      setSession({ token: data.accessToken, user: data.user })
      setLoginForm(starterLoginForm)
    } catch (error) {
      setAuthError('API is offline. Start the backend server and try again.')
      console.error(error)
    }
  }

  const handleRegisterSubmit = async (event) => {
    event.preventDefault()
    setAuthError('')

    try {
      const response = await fetch(`${API_BASE}/auth/register`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Correlation-ID': createCorrelationId(),
        },
        body: JSON.stringify(registerForm),
      })

      if (!response.ok) {
        const problem = await response.json().catch(() => null)
        setAuthError(problem?.message ?? 'Unable to create an account.')
        return
      }

      setRegisterForm(starterRegisterForm)
      setAuthMode('login')
      setAuthError('Account created. Please log in.')
    } catch (error) {
      setAuthError('API is offline. Start the backend server and try again.')
      console.error(error)
    }
  }

  const handleLogout = async () => {
    try {
      await fetch(`${API_BASE}/auth/logout`, { method: 'POST', credentials: 'include' })
    } catch (error) {
      console.error(error)
    }

    setSession(null)
    setMemories([])
    setAuthError('')
  }

  const loadRelationship = async () => {
    try {
      const response = await authFetch('/relationships')
      if (response.ok) {
        const data = await response.json()
        setRelationshipData(data)
        if (data.relationship?.relationshipId) {
          await loadSharedMemories(data.relationship.relationshipId)
        } else {
          setSharedMemories([])
          setSharedMemoryMessage('You need an accepted two-person relationship to access shared memories.')
        }
      }
    } catch (error) {
      console.error(error)
    }
  }

  const loadSharedMemories = async (relationshipId) => {
    const response = await authFetch(`/relationships/${relationshipId}/memories`)
    if (!response.ok) {
      return
    }

    const data = await response.json()
    setSharedMemories(data.memories ?? [])
    setSharedMemoryMessage(data.message ?? '')
  }

  const createRelationship = async () => {
    const response = await authFetch('/relationships', { method: 'POST' })
    const data = await response.json()
    setRelationshipMessage(data.message)
    if (response.ok) {
      await loadRelationship()
    }
  }

  const createInvite = async (event) => {
    event.preventDefault()
    const response = await authFetch('/relationships/invites', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ inviteeUserId: Number(inviteeUserId) }),
    })
    const data = await response.json()
    setRelationshipMessage(data.message)
    if (response.ok) {
      setInviteCode(data.code)
      setInviteeUserId('')
      await loadRelationship()
    }
  }

  const leaveRelationship = async () => {
    if (!window.confirm('Leave this relationship? Shared memories will become private again.')) {
      return
    }

    const response = await authFetch('/relationships/leave', { method: 'POST' })
    const data = await response.json()
    setRelationshipMessage(data.message)
    if (response.ok) {
      setInviteCode('')
      await loadRelationship()
    }
  }

  const respondToInvite = async (invite, action) => {
    const body = action === 'accept' ? { code: invite.code } : undefined
    const response = await authFetch(`/relationships/invites/${invite.id}/${action}`, {
      method: 'POST',
      ...(body ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) } : {}),
    })
    const data = await response.json()
    setRelationshipMessage(data.message)
    await loadRelationship()
  }

  const loadMemories = async (searchTerm = search, selectedMood = moodFilter) => {
    try {
      const parameters = new URLSearchParams()
      if (searchTerm.trim()) {
        parameters.set('search', searchTerm.trim())
      }
      if (selectedMood) {
        parameters.set('mood', selectedMood)
      }
      const query = parameters.size ? `?${parameters.toString()}` : ''
      const response = await authFetch(`/memories${query}`)
      const data = await response.json()
      setMemories(data)
      setStatus(data.length ? 'Your love story is growing.' : searchTerm || selectedMood ? 'No matching memories. Add one to this chapter.' : 'Start your first memory.')
    } catch (error) {
      setStatus('API is offline. Start the backend server to see the magic.')
      console.error(error)
    }
  }

  const handleChange = (event) => {
    const { name, value } = event.target
    setFormData((previous) => ({ ...previous, [name]: value }))
  }

  const handleSearchChange = (event) => {
    const nextSearch = event.target.value
    setSearch(nextSearch)
    loadMemories(nextSearch, moodFilter)
  }

  const handleMoodFilterChange = (event) => {
    const nextMood = event.target.value
    setMoodFilter(nextMood)
    loadMemories(search, nextMood)
  }

  const handleImageChange = (event) => {
    setImageFile(event.target.files[0] ?? null)
  }

  const uploadImage = async (memoryId) => {
    if (!imageFile) {
      return
    }

    const uploadData = new FormData()
    uploadData.append('image', imageFile)

    const response = await authFetch(`/memories/${memoryId}/image`, {
      method: 'POST',
      body: uploadData,
    })

    if (!response.ok) {
      throw new Error('Unable to upload image.')
    }
  }

  const handleSubmit = async (event) => {
    event.preventDefault()

    const isEditing = editingMemoryId !== null

    try {
      const response = await authFetch(
        isEditing ? `/memories/${editingMemoryId}` : '/memories',
        {
        method: isEditing ? 'PUT' : 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          ...formData,
          date: formData.date ? new Date(formData.date).toISOString() : new Date().toISOString(),
        }),
        },
      )

      if (!response.ok) {
        throw new Error('Unable to save memory.')
      }

      const savedMemory = await response.json()
      await uploadImage(savedMemory.id)
      setFormData(starterForm)
      setImageFile(null)
      setEditingMemoryId(null)
      await loadMemories()
      setStatus(isEditing ? 'Your memory was updated.' : 'A new memory was added to your timeline.')
    } catch (error) {
      setStatus('Something went wrong while saving your memory.')
      console.error(error)
    }
  }

  const startEditing = (memory) => {
    setEditingMemoryId(memory.id)
    setFormData({
      title: memory.title,
      date: memory.date.slice(0, 10),
      mood: memory.mood,
      description: memory.description,
      imageUrl: memory.imageUrl ?? '',
      visibility: memory.visibility ?? 'Private',
      partnerCanEdit: memory.partnerCanEdit ?? false,
    })
    setImageFile(null)
    setStatus(`Editing "${memory.title}".`)
  }

  const cancelEditing = () => {
    setEditingMemoryId(null)
    setFormData(starterForm)
    setImageFile(null)
    setStatus('Edit cancelled.')
  }

  const deleteMemory = async (memory) => {
    if (!window.confirm(`Delete "${memory.title}"? This cannot be undone.`)) {
      return
    }

    try {
      const response = await authFetch(`/memories/${memory.id}`, { method: 'DELETE' })
      if (!response.ok) {
        throw new Error('Unable to delete memory.')
      }

      if (editingMemoryId === memory.id) {
        cancelEditing()
      }

      await loadMemories()
      setStatus('Memory deleted.')
    } catch (error) {
      setStatus('Something went wrong while deleting your memory.')
      console.error(error)
    }
  }

  const firstDate = new Date('2024-06-15T00:00:00')
  const daysTogether = Math.max(
    0,
    Math.floor((Date.now() - firstDate.getTime()) / (1000 * 60 * 60 * 24)),
  )

  if (checkingSession) {
    return (
      <div className="page-shell">
        <div className="auth-panel">
          <span className="brand">Love Capsule</span>
          <p>Checking your session...</p>
        </div>
      </div>
    )
  }

  if (!session) {
    return (
      <div className="page-shell">
        <div className="auth-panel">
          <span className="brand">Love Capsule</span>
          <h1>{authMode === 'login' ? 'Welcome back' : 'Create your account'}</h1>

          {authError && <p className="auth-error">{authError}</p>}

          {authMode === 'login' ? (
            <form onSubmit={handleLoginSubmit} className="memory-form">
              <label>
                Email
                <input type="email" name="email" value={loginForm.email} onChange={handleLoginChange} required />
              </label>
              <label>
                Password
                <input type="password" name="password" value={loginForm.password} onChange={handleLoginChange} required />
              </label>
              <button type="submit">Log in</button>
            </form>
          ) : (
            <form onSubmit={handleRegisterSubmit} className="memory-form">
              <label>
                Display name
                <input type="text" name="displayName" value={registerForm.displayName} onChange={handleRegisterChange} required />
              </label>
              <label>
                Email
                <input type="email" name="email" value={registerForm.email} onChange={handleRegisterChange} required />
              </label>
              <label>
                Password <span className="optional-label">at least 12 characters</span>
                <input type="password" name="password" value={registerForm.password} onChange={handleRegisterChange} minLength={12} required />
              </label>
              <button type="submit">Create account</button>
            </form>
          )}

          <button
            type="button"
            className="secondary-button"
            onClick={() => {
              setAuthMode(authMode === 'login' ? 'register' : 'login')
              setAuthError('')
            }}
          >
            {authMode === 'login' ? 'Need an account? Register' : 'Already have an account? Log in'}
          </button>
        </div>
      </div>
    )
  }

  const pendingInvites = relationshipData?.pendingInvites ?? []

  return (
    <div className="page-shell">
      <header className="hero-panel">
        <nav className="topbar">
          <span className="brand">Love Capsule</span>
          <span className="tag">Our story</span>
        </nav>

        <div className="hero-content">
          <div>
            <p className="eyebrow">A little place for us</p>
            <h1>Every memory deserves a forever home.</h1>
            <p className="subtitle">
              Save the sweet moments, collect the laughter, and celebrate the days that made us feel like home.
            </p>
          </div>

          <div className="stats-card">
            <p>Signed in as {session.user.displayName}</p>
            <strong>{daysTogether}</strong>
            <span>{memories.length} captured moments</span>
            <button type="button" className="secondary-button" onClick={handleLogout}>
              Log out
            </button>
          </div>
        </div>
      </header>

      <section className="panel relationship-panel">
        <div className="panel-header">
          <h2>Our relationship</h2>
          {pendingInvites.length > 0 && <span className="status-pill">{pendingInvites.length} pending</span>}
        </div>
        {relationshipMessage && <p className="auth-error">{relationshipMessage}</p>}
        {!relationshipData?.relationship && (
          <button type="button" onClick={createRelationship}>Create relationship space</button>
        )}
        {relationshipData?.relationship && (
          <>
            <form onSubmit={createInvite} className="invite-form">
              <label>
                Partner account ID
                <input
                  type="number"
                  value={inviteeUserId}
                  onChange={(event) => setInviteeUserId(event.target.value)}
                  min="1"
                  required
                />
              </label>
              <button type="submit">Create invitation</button>
            </form>
            <button type="button" className="secondary-button leave-button" onClick={leaveRelationship}>
              Leave relationship
            </button>
          </>
        )}
        {inviteCode && (
          <p className="invite-code">Invitation code: <strong>{inviteCode}</strong></p>
        )}
        {pendingInvites.map((invite) => (
          <div className="invite-row" key={invite.id}>
            <span>Invitation from account {invite.inviterUserId}</span>
            <input
              type="text"
              placeholder="Paste invitation code"
              onChange={(event) => { invite.code = event.target.value }}
            />
            <button type="button" onClick={() => respondToInvite(invite, 'accept')}>Accept</button>
            <button type="button" className="secondary-button" onClick={() => respondToInvite(invite, 'decline')}>Decline</button>
          </div>
        ))}
      </section>

      <section className="panel relationship-panel">
        <div className="panel-header">
          <h2>Shared memories</h2>
        </div>
        <p className="empty-state">{sharedMemoryMessage}</p>
        {sharedMemories.map((memory) => (
          <article className="memory-card" key={`shared-${memory.id}`}>
            <div className="memory-date">{new Date(memory.date).toLocaleDateString()}</div>
            <div className="memory-body">
              <div className="memory-topline">
                <h3>{memory.title}</h3>
                <span>{memory.mood}</span>
              </div>
              <p>{memory.description}</p>
              {memory.imageUrl && <img className="memory-image" src={getImageSource(memory.imageUrl)} alt={memory.title} />}
            </div>
          </article>
        ))}
      </section>

      <main className="layout">
        <section className="panel form-panel">
          <div className="panel-header">
            <h2>{editingMemoryId !== null ? 'Edit memory' : 'Add a memory'}</h2>
            <span className="status-pill">{status}</span>
          </div>

          <form onSubmit={handleSubmit} className="memory-form">
            <label>
              Title
              <input
                type="text"
                name="title"
                value={formData.title}
                onChange={handleChange}
                placeholder="Sunset walk"
                required
              />
            </label>

            <div className="two-col">
              <label>
                Date
                <input
                  type="date"
                  name="date"
                  value={formData.date}
                  onChange={handleChange}
                  required
                />
              </label>

              <label>
                Mood
                <select name="mood" value={formData.mood} onChange={handleChange}>
                  <option>Happy</option>
                  <option>Loved</option>
                  <option>Excited</option>
                  <option>Peaceful</option>
                  <option>Dreamy</option>
                </select>
              </label>
            </div>

            <label>
              Story
              <textarea
                name="description"
                value={formData.description}
                onChange={handleChange}
                rows="5"
                placeholder="Tell me about the moment that made you smile."
                required
              />
            </label>

            <label>
              Photo URL <span className="optional-label">optional</span>
              <input
                type="url"
                name="imageUrl"
                value={formData.imageUrl}
                onChange={handleChange}
                placeholder="https://example.com/our-photo.jpg"
              />
            </label>

            <label>
              Privacy
              <select name="visibility" value={formData.visibility} onChange={handleChange}>
                <option value="Private">Private</option>
                <option value="Shared">Shared with partner</option>
              </select>
            </label>

            {formData.visibility === 'Shared' && (
              <label className="checkbox-label">
                <input
                  type="checkbox"
                  name="partnerCanEdit"
                  checked={formData.partnerCanEdit}
                  onChange={(event) => setFormData((previous) => ({ ...previous, partnerCanEdit: event.target.checked }))}
                />
                Allow partner to edit this memory
              </label>
            )}

            <label>
              Upload a photo <span className="optional-label">JPG, PNG, or WebP up to 5 MB</span>
              <input type="file" name="imageFile" accept=".jpg,.jpeg,.png,.webp" onChange={handleImageChange} />
            </label>

            <div className="form-actions">
              <button type="submit">{editingMemoryId !== null ? 'Update memory' : 'Save memory'}</button>
              {editingMemoryId !== null && (
                <button type="button" className="secondary-button" onClick={cancelEditing}>
                  Cancel
                </button>
              )}
            </div>
          </form>
        </section>

        <section className="panel timeline-panel">
          <div className="panel-header">
            <h2>Our timeline</h2>
            <input
              className="search-input"
              type="search"
              value={search}
              onChange={handleSearchChange}
              placeholder="Search memories"
              aria-label="Search memories"
            />
            <select
              className="mood-filter"
              value={moodFilter}
              onChange={handleMoodFilterChange}
              aria-label="Filter memories by mood"
            >
              <option value="">All moods</option>
              <option>Happy</option>
              <option>Loved</option>
              <option>Excited</option>
              <option>Peaceful</option>
              <option>Dreamy</option>
            </select>
          </div>

          <div className="timeline">
            {memories.length === 0 ? (
              <p className="empty-state">No memories yet. Add the first one.</p>
            ) : (
              memories.map((memory) => (
                <article key={memory.id} className="memory-card">
                  <div className="memory-date">{new Date(memory.date).toLocaleDateString()}</div>
                  <div className="memory-body">
                    <div className="memory-topline">
                      <h3>{memory.title}</h3>
                      <span>{memory.mood}</span>
                    </div>
                    <p>{memory.description}</p>
                    {memory.imageUrl && (
                      <img className="memory-image" src={getImageSource(memory.imageUrl)} alt={memory.title} />
                    )}
                    <div className="memory-actions">
                      <button type="button" className="text-button" onClick={() => startEditing(memory)}>
                        Edit
                      </button>
                      <button type="button" className="text-button delete-button" onClick={() => deleteMemory(memory)}>
                        Delete
                      </button>
                    </div>
                  </div>
                </article>
              ))
            )}
          </div>
        </section>
      </main>
    </div>
  )
}

export default App
