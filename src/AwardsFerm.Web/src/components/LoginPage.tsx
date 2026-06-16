import { useState } from 'react'
import { login } from '../api'
import { setAuth } from '../auth'

export function LoginPage({ onSuccess }: { onSuccess: (login: string) => void }) {
  const [loginName, setLoginName] = useState('')
  const [password, setPassword] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const onSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setLoading(true)
    setError(null)
    try {
      const result = await login(loginName.trim(), password)
      setAuth(result.token, result.login)
      onSuccess(result.login)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Ошибка входа')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="login-page">
      <form className="login-card" onSubmit={(e) => void onSubmit(e)}>
        <h1>AwardsFerm</h1>
        <p className="login-subtitle">Вход в панель управления</p>

        {error && <div className="login-error">{error}</div>}

        <label className="form-field">
          <span>Логин</span>
          <input
            type="text"
            value={loginName}
            onChange={(e) => setLoginName(e.target.value)}
            autoComplete="username"
            required
          />
        </label>

        <label className="form-field">
          <span>Пароль</span>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            required
          />
        </label>

        <button className="btn btn-primary login-submit" type="submit" disabled={loading}>
          {loading ? 'Вход…' : 'Войти'}
        </button>
      </form>
    </div>
  )
}
