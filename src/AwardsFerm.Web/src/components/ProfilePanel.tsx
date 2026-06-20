import { useState } from 'react'
import { changePassword } from '../api'

export function ProfilePanel({ login, onLogout }: { login: string; onLogout: () => void }) {
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [repeatPassword, setRepeatPassword] = useState('')
  const [saving, setSaving] = useState(false)
  const [notice, setNotice] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null)

  const onSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setNotice(null)

    if (newPassword.length < 8) {
      setNotice({ kind: 'error', text: 'Новый пароль должен быть не короче 8 символов' })
      return
    }
    if (newPassword !== repeatPassword) {
      setNotice({ kind: 'error', text: 'Повтор пароля не совпадает' })
      return
    }

    setSaving(true)
    try {
      await changePassword(currentPassword, newPassword)
      setCurrentPassword('')
      setNewPassword('')
      setRepeatPassword('')
      setNotice({ kind: 'ok', text: 'Пароль успешно изменён' })
    } catch (err) {
      setNotice({ kind: 'error', text: err instanceof Error ? err.message : 'Ошибка смены пароля' })
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="accounts-panel">
      <div className="panel-header">
        <h2>Профиль</h2>
      </div>
      <p className="panel-muted">Пользователь: {login}</p>

      {notice && (
        <div className={notice.kind === 'ok' ? 'panel-success' : 'panel-error'}>
          {notice.text}
        </div>
      )}

      <form className="account-form" onSubmit={(e) => void onSubmit(e)}>
        <h3>Смена пароля</h3>

        <label className="form-field">
          <span>Текущий пароль</span>
          <input
            type="password"
            value={currentPassword}
            onChange={(e) => setCurrentPassword(e.target.value)}
            required
          />
        </label>

        <label className="form-field">
          <span>Новый пароль</span>
          <input
            type="password"
            value={newPassword}
            onChange={(e) => setNewPassword(e.target.value)}
            required
          />
        </label>

        <label className="form-field">
          <span>Повтор нового пароля</span>
          <input
            type="password"
            value={repeatPassword}
            onChange={(e) => setRepeatPassword(e.target.value)}
            required
          />
        </label>

        <div className="account-form-actions">
          <button className="btn btn-primary btn-sm" type="submit" disabled={saving}>
            {saving ? 'Сохранение…' : 'Сменить пароль'}
          </button>
        </div>
      </form>

      <div className="profile-logout">
        <button className="btn btn-secondary btn-sm" type="button" onClick={onLogout}>
          Выйти из аккаунта
        </button>
      </div>
    </section>
  )
}
