import { useEffect, useState } from 'react'
import { checkApiHealth, resetHubConnection } from './api'
import { clearAuth, getLogin, isAuthenticated } from './auth'
import { AdAccountsPanel } from './components/AdAccountsPanel'
import { LoginPage } from './components/LoginPage'
import { ProfitDashboard } from './components/ProfitDashboard'
import { ProfilePanel } from './components/ProfilePanel'
import { ProxiesPanel } from './components/ProxiesPanel'
import { SessionsPanel } from './components/SessionsPanel'
import './App.css'

type Tab = 'sessions' | 'accounts' | 'proxies' | 'profit' | 'profile'

function App() {
  const [authed, setAuthed] = useState(isAuthenticated())
  const [userLogin, setUserLogin] = useState(getLogin() ?? '')
  const [tab, setTab] = useState<Tab>('sessions')
  const [proxyPrefillName, setProxyPrefillName] = useState<string | null>(null)
  const [apiUp, setApiUp] = useState(false)
  const [mskTime, setMskTime] = useState('')

  useEffect(() => {
    if (!authed) return
    const check = async () => setApiUp(await checkApiHealth())
    void check()
    const interval = window.setInterval(() => void check(), 10000)
    return () => window.clearInterval(interval)
  }, [authed])

  useEffect(() => {
    const tick = () => {
      setMskTime(
        new Date().toLocaleTimeString('ru-RU', {
          timeZone: 'Europe/Moscow',
          hour: '2-digit',
          minute: '2-digit',
          second: '2-digit',
        }),
      )
    }
    tick()
    const id = window.setInterval(tick, 1000)
    return () => window.clearInterval(id)
  }, [])

  const onLogout = () => {
    clearAuth()
    resetHubConnection()
    setAuthed(false)
    setUserLogin('')
  }

  if (!authed) {
    return (
      <LoginPage
        onSuccess={(login) => {
          setAuthed(true)
          setUserLogin(login)
        }}
      />
    )
  }

  return (
    <div className="app">
      <header className="header">
        <div>
          <h1>AwardsFerm</h1>
          <p className="subtitle">
            {userLogin} · админ-панель
          </p>
        </div>
        <div className="header-meta">
          <span className="badge">МСК {mskTime}</span>
          <span className={`badge ${apiUp ? 'badge-ok' : 'badge-warn'}`}>
            API {apiUp ? 'доступен' : 'недоступен'}
          </span>
          <button className="btn btn-secondary btn-sm" onClick={onLogout}>
            Выйти
          </button>
        </div>
      </header>

      <nav className="app-tabs">
        <button className={`tab-btn${tab === 'sessions' ? ' tab-btn-active' : ''}`} onClick={() => setTab('sessions')}>
          Сессии
        </button>
        <button className={`tab-btn${tab === 'accounts' ? ' tab-btn-active' : ''}`} onClick={() => setTab('accounts')}>
          Аккаунты
        </button>
        <button className={`tab-btn${tab === 'proxies' ? ' tab-btn-active' : ''}`} onClick={() => setTab('proxies')}>
          Прокси
        </button>
        <button className={`tab-btn${tab === 'profit' ? ' tab-btn-active' : ''}`} onClick={() => setTab('profit')}>
          Прибыль
        </button>
        <button className={`tab-btn${tab === 'profile' ? ' tab-btn-active' : ''}`} onClick={() => setTab('profile')}>
          Профиль
        </button>
      </nav>

      {tab === 'sessions' && <SessionsPanel />}
      {tab === 'accounts' && (
        <AdAccountsPanel
          selectedId={null}
          onSelect={() => {}}
          onOpenProxies={(account) => {
            setProxyPrefillName(account.name)
            setTab('proxies')
          }}
        />
      )}
      {tab === 'proxies' && (
        <ProxiesPanel
          prefillName={proxyPrefillName}
          onPrefillUsed={() => setProxyPrefillName(null)}
        />
      )}
      {tab === 'profit' && <ProfitDashboard />}
      {tab === 'profile' && <ProfilePanel login={userLogin} />}

      <footer className="footer">
        <span>SQLite · headless · логи в реальном времени (SignalR)</span>
      </footer>
    </div>
  )
}

export default App
