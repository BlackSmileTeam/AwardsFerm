import { useCallback, useEffect, useState } from 'react'
import { createProxy, deleteProxy, fetchProxies } from '../api'
import type { CreateProxyRequest, ProxyConfig } from '../types'

const emptyForm: CreateProxyRequest = {
  name: '',
  scheme: 'http',
  host: '',
  port: 8080,
  login: '',
  password: '',
  timezone: 'Europe/Moscow',
  locale: 'ru-RU',
  locationLabel: '',
}

type Props = {
  onChanged?: () => void
}

export function ProxiesPanel({ onChanged }: Props) {
  const [proxies, setProxies] = useState<ProxyConfig[]>([])
  const [form, setForm] = useState<CreateProxyRequest>(emptyForm)
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [expanded, setExpanded] = useState(true)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      setProxies(await fetchProxies())
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось загрузить прокси')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const onSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setSaving(true)
    setError(null)
    try {
      const created = await createProxy({
        ...form,
        name: form.name.trim(),
        host: form.host.trim(),
        login: form.login?.trim() || undefined,
        password: form.password?.trim() || undefined,
        locationLabel: form.locationLabel?.trim() || undefined,
        timezone: form.timezone?.trim() || undefined,
        locale: form.locale?.trim() || undefined,
      })
      setProxies((prev) => [...prev, created].sort((a, b) => a.name.localeCompare(b.name)))
      setForm(emptyForm)
      onChanged?.()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось добавить прокси')
    } finally {
      setSaving(false)
    }
  }

  const onDelete = async (proxy: ProxyConfig) => {
    if (!window.confirm(`Удалить прокси «${proxy.name}»?`)) return
    try {
      await deleteProxy(proxy.id)
      setProxies((prev) => prev.filter((p) => p.id !== proxy.id))
      onChanged?.()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось удалить прокси')
    }
  }

  return (
    <section className="proxies-panel">
      <div className="proxies-panel-header">
        <button type="button" className="proxies-toggle" onClick={() => setExpanded((v) => !v)}>
          {expanded ? '▼' : '▶'} Прокси ({proxies.length})
        </button>
        <span className="proxies-hint">Добавьте прокси и выберите в слоте сессии</span>
      </div>

      {expanded && (
        <>
          {error && <div className="error-banner">{error}</div>}

          <form className="proxy-form" onSubmit={(e) => void onSubmit(e)}>
            <div className="proxy-form-row">
              <label>
                Название
                <input
                  value={form.name}
                  onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
                  placeholder="ProxyCola Beeline"
                  required
                />
              </label>
              <label>
                Тип
                <select
                  value={form.scheme}
                  onChange={(e) => setForm((f) => ({ ...f, scheme: e.target.value }))}
                >
                  <option value="http">HTTP</option>
                  <option value="socks5">SOCKS5</option>
                </select>
              </label>
              <label>
                Хост
                <input
                  value={form.host}
                  onChange={(e) => setForm((f) => ({ ...f, host: e.target.value }))}
                  placeholder="tproxy.pro"
                  required
                />
              </label>
              <label>
                Порт
                <input
                  type="number"
                  min={1}
                  max={65535}
                  value={form.port}
                  onChange={(e) => setForm((f) => ({ ...f, port: Number(e.target.value) }))}
                  required
                />
              </label>
            </div>
            <div className="proxy-form-row">
              <label>
                Логин
                <input
                  value={form.login ?? ''}
                  onChange={(e) => setForm((f) => ({ ...f, login: e.target.value }))}
                  placeholder="user"
                />
              </label>
              <label>
                Пароль
                <input
                  type="password"
                  value={form.password ?? ''}
                  onChange={(e) => setForm((f) => ({ ...f, password: e.target.value }))}
                  placeholder="••••••••"
                />
              </label>
              <label>
                Локация (подпись)
                <input
                  value={form.locationLabel ?? ''}
                  onChange={(e) => setForm((f) => ({ ...f, locationLabel: e.target.value }))}
                  placeholder="Санкт-Петербург, Россия"
                />
              </label>
              <label>
                Timezone
                <input
                  value={form.timezone ?? ''}
                  onChange={(e) => setForm((f) => ({ ...f, timezone: e.target.value }))}
                  placeholder="Europe/Moscow"
                />
              </label>
            </div>
            <div className="proxy-form-actions">
              <button type="submit" className="btn btn-primary btn-sm" disabled={saving}>
                {saving ? 'Сохранение…' : '+ Добавить прокси'}
              </button>
            </div>
          </form>

          <div className="proxy-list">
            {loading ? (
              <span className="log-empty">Загрузка…</span>
            ) : proxies.length === 0 ? (
              <span className="log-empty">Прокси пока нет — добавьте выше</span>
            ) : (
              proxies.map((proxy) => (
                <div key={proxy.id} className="proxy-list-item">
                  <div>
                    <strong>{proxy.name}</strong>
                    <span className="proxy-list-addr">
                      {proxy.scheme}://{proxy.host}:{proxy.port}
                      {proxy.login ? ` · ${proxy.login}` : ''}
                      {proxy.hasPassword ? ' · пароль ✓' : ''}
                    </span>
                    {proxy.locationLabel && (
                      <span className="proxy-list-geo">{proxy.locationLabel}</span>
                    )}
                  </div>
                  <button
                    type="button"
                    className="btn btn-danger btn-sm"
                    onClick={() => void onDelete(proxy)}
                  >
                    ✕
                  </button>
                </div>
              ))
            )}
          </div>
        </>
      )}
    </section>
  )
}
