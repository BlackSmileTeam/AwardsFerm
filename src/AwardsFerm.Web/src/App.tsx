import { useState } from 'react'
import { resetHubConnection } from './api'
import { clearAuth, getLogin, isAuthenticated } from './auth'
import { AdAccountsPanel } from './components/AdAccountsPanel'
import { LoginPage } from './components/LoginPage'
import { ProfitDashboard } from './components/ProfitDashboard'
import { ProfilePanel } from './components/ProfilePanel'
import { ProxiesPanel } from './components/ProxiesPanel'
import { SessionsPanel } from './components/SessionsPanel'
import './App.css'

type Tab = 'sessions' | 'accounts' | 'proxies' | 'profit' | 'profile'

function ProfileIcon() {
  return (
    <svg width="22" height="22" viewBox="0 0 24 24" fill="none" aria-hidden="true">
      <circle cx="12" cy="8" r="4" stroke="currentColor" strokeWidth="1.8" />
      <path
        d="M5 20c0-3.314 3.134-6 7-6s7 2.686 7 6"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
      />
    </svg>
  )
}

function App() {
  const [authed, setAuthed] = useState(isAuthenticated())
  const [userLogin, setUserLogin] = useState(getLogin() ?? '')
  const [tab, setTab] = useState<Tab>('sessions')
  const [proxyPrefillName, setProxyPrefillName] = useState<string | null>(null)

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
        <div className="header-top">
          <div className="header-brand">
            <h1>AwardsFerm</h1>
            <button
              type="button"
              className={`header-profile-btn${tab === 'profile' ? ' header-profile-btn-active' : ''}`}
              onClick={() => setTab('profile')}
              aria-label="Профиль"
              title="Профиль"
            >
              <ProfileIcon />
            </button>
          </div>
        </div>
        <p className="subtitle">
          {userLogin} · админ-панель
        </p>
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
        <button
          className={`tab-btn tab-btn-profile-desktop${tab === 'profile' ? ' tab-btn-active' : ''}`}
          onClick={() => setTab('profile')}
        >
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
      {tab === 'profile' && <ProfilePanel login={userLogin} onLogout={onLogout} />}

      <footer className="footer">
        <span>SQLite · логи в реальном времени</span>
      </footer>
    </div>
  )
}

export default App
