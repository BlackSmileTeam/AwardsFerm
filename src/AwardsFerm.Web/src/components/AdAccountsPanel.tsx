import { useCallback, useEffect, useState } from 'react'
import {
  createAdAccount,
  deleteAdAccount,
  fetchAdAccounts,
  updateAdAccount,
} from '../api'
import type { AdAccount, CreateAdAccountRequest } from '../types'

const emptyForm: CreateAdAccountRequest = {
  name: '',
  gameTitle: '',
  gameUrl: '',
  token: '',
}

export function AdAccountsPanel({
  selectedId,
  onSelect,
  onChanged,
}: {
  selectedId: number | null
  onSelect: (id: number) => void
  onChanged?: () => void
}) {
  const [accounts, setAccounts] = useState<AdAccount[]>([])
  const [loading, setLoading] = useState(true)
  const [form, setForm] = useState<CreateAdAccountRequest>(emptyForm)
  const [editingId, setEditingId] = useState<number | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const data = await fetchAdAccounts()
      setAccounts(data)
      if (data.length > 0 && selectedId === null) onSelect(data[0].id)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка загрузки')
    } finally {
      setLoading(false)
    }
  }, [onSelect, selectedId])

  useEffect(() => {
    void load()
  }, [load])

  const onSave = async () => {
    setSaving(true)
    setError(null)
    try {
      if (editingId) {
        await updateAdAccount(editingId, {
          name: form.name,
          gameTitle: form.gameTitle,
          gameUrl: form.gameUrl,
          token: form.token || undefined,
        })
      } else {
        const created = await createAdAccount(form)
        onSelect(created.id)
      }
      setForm(emptyForm)
      setEditingId(null)
      await load()
      onChanged?.()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка сохранения')
    } finally {
      setSaving(false)
    }
  }

  const onEdit = (account: AdAccount) => {
    setEditingId(account.id)
    setForm({
      name: account.name,
      gameTitle: account.gameTitle,
      gameUrl: account.gameUrl,
      token: '',
    })
  }

  const onDelete = async (id: number) => {
    if (!window.confirm('Удалить рекламный аккаунт и все его сессии?')) return
    setError(null)
    try {
      await deleteAdAccount(id)
      if (selectedId === id) onSelect(accounts.find((a) => a.id !== id)?.id ?? 0)
      await load()
      onChanged?.()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ошибка удаления')
    }
  }

  return (
    <section className="accounts-panel">
      <div className="panel-header">
        <h2>Рекламные аккаунты</h2>
        <button className="btn btn-secondary btn-sm" onClick={() => void load()} disabled={loading}>
          ↻
        </button>
      </div>

      {error && <div className="panel-error">{error}</div>}

      <div className="accounts-list">
        {loading && accounts.length === 0 ? (
          <p className="panel-muted">Загрузка…</p>
        ) : accounts.length === 0 ? (
          <p className="panel-muted">Нет аккаунтов — создайте первый ниже</p>
        ) : (
          accounts.map((account) => (
            <div
              key={account.id}
              className={`account-card${selectedId === account.id ? ' account-card-active' : ''}`}
              onClick={() => onSelect(account.id)}
            >
              <div className="account-card-title">{account.name}</div>
              <div className="account-card-meta">{account.gameTitle}</div>
              <div className="account-card-actions">
                <button
                  className="btn btn-ghost btn-sm"
                  onClick={(e) => {
                    e.stopPropagation()
                    onEdit(account)
                  }}
                >
                  ✎
                </button>
                <button
                  className="btn btn-ghost btn-sm"
                  onClick={(e) => {
                    e.stopPropagation()
                    void onDelete(account.id)
                  }}
                >
                  ✕
                </button>
              </div>
            </div>
          ))
        )}
      </div>

      <div className="account-form">
        <h3>{editingId ? 'Редактирование' : 'Новый аккаунт'}</h3>
        <label className="form-field">
          <span>Название</span>
          <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
        </label>
        <label className="form-field">
          <span>Название игры</span>
          <input value={form.gameTitle} onChange={(e) => setForm({ ...form, gameTitle: e.target.value })} />
        </label>
        <label className="form-field">
          <span>Ссылка на игру</span>
          <input value={form.gameUrl} onChange={(e) => setForm({ ...form, gameUrl: e.target.value })} />
        </label>
        <label className="form-field">
          <span>Токен РСЯ{editingId ? ' (оставьте пустым, чтобы не менять)' : ''}</span>
          <input
            type="password"
            value={form.token}
            onChange={(e) => setForm({ ...form, token: e.target.value })}
          />
        </label>
        <div className="account-form-actions">
          <button
            className="btn btn-primary btn-sm"
            disabled={saving || !form.name || !form.gameTitle || !form.gameUrl || (!editingId && !form.token)}
            onClick={() => void onSave()}
          >
            {saving ? 'Сохранение…' : editingId ? 'Сохранить' : 'Создать'}
          </button>
          {editingId && (
            <button
              className="btn btn-secondary btn-sm"
              onClick={() => {
                setEditingId(null)
                setForm(emptyForm)
              }}
            >
              Отмена
            </button>
          )}
        </div>
      </div>
    </section>
  )
}
